using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record BillingWebhookProjectionResult(
    bool Duplicate,
    string Status,
    Guid? OrganizationId,
    string? ErrorCode);

public sealed partial class CoreDataStore
{
    public async Task<BillingWebhookProjectionResult> ApplyBillingWebhookAsync(
        string provider,
        string eventId,
        string eventType,
        JsonElement payload,
        string requestId,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = provider.Trim().ToUpperInvariant();
        var normalizedEventId = eventId.Trim();
        var normalizedEventType = eventType.Trim();
        if (normalizedProvider is not ("STRIPE" or "OPN" or "XENDIT" or "MANUAL"))
        {
            throw new ArgumentException("The billing provider is not supported.", nameof(provider));
        }
        if (string.IsNullOrWhiteSpace(normalizedEventId) || normalizedEventId.Length > 255)
        {
            throw new ArgumentException("A bounded billing event id is required.", nameof(eventId));
        }
        if (string.IsNullOrWhiteSpace(normalizedEventType) || normalizedEventType.Length > 160)
        {
            throw new ArgumentException("A bounded billing event type is required.", nameof(eventType));
        }

        var eventObject = JsonObjectAt(payload, "data", "object") ?? payload;
        var metadata = JsonObjectAt(eventObject, "metadata");
        var organizationId = ParseGuid(JsonStringValue(metadata, "organization_id") ?? JsonStringValue(payload, "organization_id"));
        var providerCustomerId = JsonStringValue(eventObject, "customer");
        var providerSubscriptionId = JsonStringValue(eventObject, "subscription")
            ?? (normalizedEventType.Contains("subscription", StringComparison.OrdinalIgnoreCase) ? JsonStringValue(eventObject, "id") : null);
        var payloadJson = payload.GetRawText();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insertEvent = new NpgsqlCommand(
            """
            insert into aevo_billing_webhook_events
              (provider, event_id, event_type, organization_id, provider_customer_id, provider_subscription_id, payload, status)
            values
              (@provider, @event_id, @event_type, @organization_id, @provider_customer_id, @provider_subscription_id, @payload, 'RECEIVED')
            on conflict (provider, event_id) do nothing
            returning event_id
            """, connection, transaction))
        {
            insertEvent.Parameters.AddWithValue("provider", normalizedProvider);
            insertEvent.Parameters.AddWithValue("event_id", normalizedEventId);
            insertEvent.Parameters.AddWithValue("event_type", normalizedEventType);
            AddNullableGuid(insertEvent, "organization_id", organizationId);
            AddNullableText(insertEvent, "provider_customer_id", providerCustomerId);
            AddNullableText(insertEvent, "provider_subscription_id", providerSubscriptionId);
            insertEvent.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });
            var inserted = await insertEvent.ExecuteScalarAsync(cancellationToken);
            if (inserted is null)
            {
                await using var duplicate = new NpgsqlCommand(
                    "select status, organization_id, error_code from aevo_billing_webhook_events where provider=@provider and event_id=@event_id",
                    connection,
                    transaction);
                duplicate.Parameters.AddWithValue("provider", normalizedProvider);
                duplicate.Parameters.AddWithValue("event_id", normalizedEventId);
                await using var duplicateReader = await duplicate.ExecuteReaderAsync(cancellationToken);
                if (!await duplicateReader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("The duplicate billing event could not be read.");
                var duplicateStatus = duplicateReader.GetString(0);
                var duplicateOrganizationId = duplicateReader.IsDBNull(1) ? (Guid?)null : duplicateReader.GetGuid(1);
                var duplicateError = duplicateReader.IsDBNull(2) ? null : duplicateReader.GetString(2);
                await duplicateReader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return new BillingWebhookProjectionResult(true, duplicateStatus, duplicateOrganizationId, duplicateError);
            }
        }

        if (organizationId is null)
        {
            organizationId = await ResolveBillingOrganizationAsync(
                connection,
                transaction,
                normalizedProvider,
                providerCustomerId,
                providerSubscriptionId,
                cancellationToken);
        }

        if (organizationId is null)
        {
            await UpdateBillingEventAsync(connection, transaction, normalizedProvider, normalizedEventId, null, "IGNORED", "UNKNOWN_CUSTOMER", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BillingWebhookProjectionResult(false, "IGNORED", null, "UNKNOWN_CUSTOMER");
        }

        var projectionStatus = BillingProjectionStatus(normalizedEventType, eventObject);
        if (projectionStatus is null)
        {
            await UpdateBillingEventAsync(connection, transaction, normalizedProvider, normalizedEventId, organizationId, "IGNORED", "EVENT_NOT_PROJECTED", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BillingWebhookProjectionResult(false, "IGNORED", organizationId, "EVENT_NOT_PROJECTED");
        }

        var planCode = NormalizePlanCode(JsonStringValue(metadata, "plan_id") ?? JsonStringValue(metadata, "plan_code"));
        var trialStart = JsonDateValue(eventObject, "trial_start");
        var trialEnd = JsonDateValue(eventObject, "trial_end");
        var currentPeriodStart = JsonDateValue(eventObject, "current_period_start") ?? JsonDateValue(eventObject, "period_start");
        var currentPeriodEnd = JsonDateValue(eventObject, "current_period_end") ?? JsonDateValue(eventObject, "period_end");
        var canceledAt = JsonDateValue(eventObject, "canceled_at") ?? (projectionStatus == "CANCELED" ? DateTimeOffset.UtcNow : null);
        var cancelAtPeriodEnd = JsonBoolValue(eventObject, "cancel_at_period_end") ?? false;
        var subscriptionId = providerSubscriptionId ?? JsonStringValue(eventObject, "id");

        await UpsertOrganizationSubscriptionAsync(
            connection,
            transaction,
            organizationId.Value,
            normalizedProvider,
            subscriptionId,
            providerCustomerId,
            planCode,
            projectionStatus,
            trialStart,
            trialEnd,
            currentPeriodStart,
            currentPeriodEnd,
            cancelAtPeriodEnd,
            canceledAt,
            payloadJson,
            normalizedEventId,
            cancellationToken);

        await SyncBillingEntitlementsAsync(connection, transaction, organizationId.Value, projectionStatus, normalizedEventId, cancellationToken);
        await SyncBillingInstallationAsync(
            connection,
            transaction,
            organizationId.Value,
            normalizedProvider,
            subscriptionId,
            providerCustomerId,
            planCode,
            projectionStatus,
            trialEnd,
            currentPeriodStart,
            currentPeriodEnd,
            canceledAt,
            normalizedEventId,
            cancellationToken);
        await UpdateBillingEventAsync(connection, transaction, normalizedProvider, normalizedEventId, organizationId, "APPLIED", null, cancellationToken);

        var eventPayload = JsonSerializer.SerializeToElement(new
        {
            provider = normalizedProvider,
            eventId = normalizedEventId,
            eventType = normalizedEventType,
            organizationId,
            status = projectionStatus,
            projectionVersion = "billing-v1"
        });
        await using (var outbox = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values ('BILLING_PROJECTION_APPLIED', 'organization_subscription', @aggregate_id, @payload)",
            connection,
            transaction))
        {
            outbox.Parameters.AddWithValue("aggregate_id", organizationId.Value.ToString("D"));
            outbox.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = eventPayload.GetRawText() });
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BillingWebhookProjectionResult(false, "APPLIED", organizationId, null);
    }

    private static async Task<Guid?> ResolveBillingOrganizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string provider,
        string? providerCustomerId,
        string? providerSubscriptionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select organization_id
            from aevo_organization_subscriptions
            where provider = @provider
              and ((@provider_subscription_id is not null and provider_subscription_id = @provider_subscription_id)
                or (@provider_customer_id is not null and provider_customer_id = @provider_customer_id))
            order by updated_at desc
            limit 1
            """, connection, transaction);
        command.Parameters.AddWithValue("provider", provider);
        AddNullableText(command, "provider_subscription_id", providerSubscriptionId);
        AddNullableText(command, "provider_customer_id", providerCustomerId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid organizationId ? organizationId : null;
    }

    private static async Task UpsertOrganizationSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string provider,
        string? providerSubscriptionId,
        string? providerCustomerId,
        string? planCode,
        string status,
        DateTimeOffset? trialStart,
        DateTimeOffset? trialEnd,
        DateTimeOffset? currentPeriodStart,
        DateTimeOffset? currentPeriodEnd,
        bool cancelAtPeriodEnd,
        DateTimeOffset? canceledAt,
        string payloadJson,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_organization_subscriptions (
              organization_id, plan_id, provider, provider_customer_id, provider_subscription_id, status,
              trial_start, trial_end, current_period_start, current_period_end, cancel_at_period_end,
              canceled_at, metadata, projection_version, last_event_id
            )
            values (
              @organization_id, coalesce(@plan_id, 'starter'), @provider, @provider_customer_id, @provider_subscription_id, @status,
              @trial_start, @trial_end, @current_period_start, @current_period_end, @cancel_at_period_end,
              @canceled_at, @metadata, 'billing-v1', @event_id
            )
            on conflict (organization_id) do update set
              plan_id = case when @plan_id is null then aevo_organization_subscriptions.plan_id else excluded.plan_id end,
              provider = excluded.provider,
              provider_customer_id = coalesce(excluded.provider_customer_id, aevo_organization_subscriptions.provider_customer_id),
              provider_subscription_id = coalesce(excluded.provider_subscription_id, aevo_organization_subscriptions.provider_subscription_id),
              status = excluded.status,
              trial_start = coalesce(excluded.trial_start, aevo_organization_subscriptions.trial_start),
              trial_end = coalesce(excluded.trial_end, aevo_organization_subscriptions.trial_end),
              current_period_start = coalesce(excluded.current_period_start, aevo_organization_subscriptions.current_period_start),
              current_period_end = coalesce(excluded.current_period_end, aevo_organization_subscriptions.current_period_end),
              cancel_at_period_end = excluded.cancel_at_period_end,
              canceled_at = coalesce(excluded.canceled_at, aevo_organization_subscriptions.canceled_at),
              metadata = excluded.metadata,
              projection_version = 'billing-v1',
              last_event_id = excluded.last_event_id,
              updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableText(command, "plan_id", planCode);
        command.Parameters.AddWithValue("provider", provider);
        AddNullableText(command, "provider_customer_id", providerCustomerId);
        AddNullableText(command, "provider_subscription_id", providerSubscriptionId);
        command.Parameters.AddWithValue("status", status);
        AddNullableTimestamp(command, "trial_start", trialStart);
        AddNullableTimestamp(command, "trial_end", trialEnd);
        AddNullableTimestamp(command, "current_period_start", currentPeriodStart);
        AddNullableTimestamp(command, "current_period_end", currentPeriodEnd);
        command.Parameters.AddWithValue("cancel_at_period_end", cancelAtPeriodEnd);
        AddNullableTimestamp(command, "canceled_at", canceledAt);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = payloadJson });
        command.Parameters.AddWithValue("event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncBillingEntitlementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string status,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_organization_entitlements (
              organization_id, feature_key, is_enabled, custom_override, limit_value, source, projection_version, last_event_id
            )
            select
              subscription.organization_id,
              plan_entitlement.feature_key,
              case when subscription.status in ('CANCELED', 'EXPIRED') then false else plan_entitlement.is_enabled end,
              false,
              plan_entitlement.limit_value,
              'BILLING',
              'entitlements-v1',
              @event_id
            from aevo_organization_subscriptions subscription
            join aevo_plan_entitlements plan_entitlement on plan_entitlement.plan_id = subscription.plan_id
            where subscription.organization_id = @organization_id
            on conflict (organization_id, feature_key) do update set
              is_enabled = case when aevo_organization_entitlements.custom_override then aevo_organization_entitlements.is_enabled else excluded.is_enabled end,
              limit_value = case when aevo_organization_entitlements.custom_override then aevo_organization_entitlements.limit_value else excluded.limit_value end,
              source = case when aevo_organization_entitlements.custom_override then aevo_organization_entitlements.source else 'BILLING' end,
              projection_version = 'entitlements-v1',
              last_event_id = excluded.last_event_id,
              updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SyncBillingInstallationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string provider,
        string? providerSubscriptionId,
        string? providerCustomerId,
        string? planCode,
        string status,
        DateTimeOffset? trialEnd,
        DateTimeOffset? currentPeriodStart,
        DateTimeOffset? currentPeriodEnd,
        DateTimeOffset? canceledAt,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_application_installations (
              organization_id, app_code, status, source, plan_code, provider,
              provider_customer_id, provider_subscription_id, trial_ends_at,
              current_period_starts_at, current_period_ends_at, last_event_id,
              projection_version
            )
            values (
              @organization_id, 'HUB', @status, 'BILLING', coalesce(@plan_code, 'starter'), @provider,
              @provider_customer_id, @provider_subscription_id, @trial_end,
              @current_period_start, @current_period_end, @event_id, 'installation-v1'
            )
            on conflict (organization_id, app_code) do update set
              status = excluded.status,
              source = 'BILLING',
              plan_code = coalesce(excluded.plan_code, aevo_application_installations.plan_code),
              provider = excluded.provider,
              provider_customer_id = coalesce(excluded.provider_customer_id, aevo_application_installations.provider_customer_id),
              provider_subscription_id = coalesce(excluded.provider_subscription_id, aevo_application_installations.provider_subscription_id),
              trial_ends_at = coalesce(excluded.trial_ends_at, aevo_application_installations.trial_ends_at),
              current_period_starts_at = coalesce(excluded.current_period_starts_at, aevo_application_installations.current_period_starts_at),
              current_period_ends_at = coalesce(excluded.current_period_ends_at, aevo_application_installations.current_period_ends_at),
              last_event_id = excluded.last_event_id,
              projection_version = 'installation-v1',
              updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("status", status);
        AddNullableText(command, "plan_code", planCode);
        command.Parameters.AddWithValue("provider", provider);
        AddNullableText(command, "provider_customer_id", providerCustomerId);
        AddNullableText(command, "provider_subscription_id", providerSubscriptionId);
        AddNullableTimestamp(command, "trial_end", trialEnd);
        AddNullableTimestamp(command, "current_period_start", currentPeriodStart);
        AddNullableTimestamp(command, "current_period_end", currentPeriodEnd);
        command.Parameters.AddWithValue("event_id", eventId);
        _ = canceledAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBillingEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string provider,
        string eventId,
        Guid? organizationId,
        string status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_billing_webhook_events set organization_id=coalesce(@organization_id, organization_id), status=@status, error_code=@error_code, processed_at=now() where provider=@provider and event_id=@event_id",
            connection,
            transaction);
        AddNullableGuid(command, "organization_id", organizationId);
        command.Parameters.AddWithValue("status", status);
        AddNullableText(command, "error_code", errorCode);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("event_id", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? BillingProjectionStatus(string eventType, JsonElement eventObject)
    {
        if (eventType.Equals("invoice.paid", StringComparison.OrdinalIgnoreCase)) return "ACTIVE";
        if (eventType.Equals("invoice.payment_failed", StringComparison.OrdinalIgnoreCase)) return "PAST_DUE";
        if (eventType.Equals("customer.subscription.deleted", StringComparison.OrdinalIgnoreCase)) return "CANCELED";
        if (!eventType.StartsWith("customer.subscription.", StringComparison.OrdinalIgnoreCase)) return null;
        return JsonStringValue(eventObject, "status")?.Trim().ToLowerInvariant() switch
        {
            "trialing" => "TRIALING",
            "active" => "ACTIVE",
            "past_due" => "PAST_DUE",
            "canceled" => "CANCELED",
            "unpaid" => "EXPIRED",
            _ => "PAST_DUE"
        };
    }

    private static string? NormalizePlanCode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "starter" or "business" or "enterprise" ? normalized : null;
    }

    private static JsonElement? JsonObjectAt(JsonElement value, params string[] path)
    {
        var current = value;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Object ? current : null;
    }

    private static string? JsonStringValue(JsonElement? value, string property)
    {
        return value is { ValueKind: JsonValueKind.Object }
            && value.Value.TryGetProperty(property, out var candidate)
            && candidate.ValueKind == JsonValueKind.String
            ? candidate.GetString()
            : null;
    }

    private static bool? JsonBoolValue(JsonElement value, string property)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var candidate)
            && (candidate.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? candidate.GetBoolean()
            : null;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var result) ? result : null;

    private static DateTimeOffset? JsonDateValue(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var candidate)) return null;
        if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out var unixSeconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); } catch (ArgumentOutOfRangeException) { return null; }
        }
        if (candidate.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(candidate.GetString(), out var parsed)) return parsed.ToUniversalTime();
        return null;
    }

    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value });
}
