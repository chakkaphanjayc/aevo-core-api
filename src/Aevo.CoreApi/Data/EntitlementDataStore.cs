using Aevo.CoreApi.Security;
using Aevo.CoreApi.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed partial class CoreDataStore
{
    /// <summary>
    /// Reads the Core-owned entitlement projection without applying defaults.
    /// Missing rows are meaningful: the evaluator must deny access instead of
    /// silently treating a new organization as entitled.
    /// </summary>
    public Task<ApplicationEntitlementSnapshot> GetApplicationEntitlementSnapshotAsync(
        Guid organizationId,
        string application,
        Guid? storeId,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "entitlement.snapshot",
            () => GetApplicationEntitlementSnapshotFromDatabaseAsync(organizationId, application, storeId, cancellationToken));

    private async Task<ApplicationEntitlementSnapshot> GetApplicationEntitlementSnapshotFromDatabaseAsync(
        Guid organizationId,
        string application,
        Guid? storeId,
        CancellationToken cancellationToken)
    {
        var normalizedApplication = EntitlementEvaluator.NormalizeApplicationCode(application);
        var featureKey = EntitlementEvaluator.FeatureKeyForApplication(application);
        if (normalizedApplication is null || featureKey is null)
        {
            return new ApplicationEntitlementSnapshot(
                application.Trim().ToUpperInvariant(),
                false,
                false,
                null,
                null,
                storeId is not null,
                false);
        }

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                select
                  ar.code is not null,
                  coalesce(ar.status = 'ACTIVE', false),
                  subscription.plan_id,
                  subscription.status,
                  subscription.trial_end,
                  subscription.current_period_start,
                  subscription.current_period_end,
                  entitlement.feature_key,
                  entitlement.is_enabled,
                  entitlement.limit_value,
                  store_quota.feature_key,
                  store_quota.is_enabled,
                  store_quota.limit_value,
                  case when @store_id is null then false else store_access.app_code is not null end,
                  case when @store_id is null then false else coalesce(store_access.status = 'ACTIVE', false) end,
                  (select count(*)::integer
                   from aevo_store_application_bindings active_binding
                   where active_binding.organization_id = @organization_id
                     and active_binding.status = 'ACTIVE')
                from (select @application_code::text as code, @feature_key::text as feature_key) requested
                left join aevo_application_registry ar
                  on ar.code = requested.code
                left join lateral (
                  select s.plan_id, s.status, s.trial_end, s.current_period_start, s.current_period_end
                  from aevo_organization_subscriptions s
                  where s.organization_id = @organization_id
                  order by s.updated_at desc, s.created_at desc
                  limit 1
                ) subscription on true
                left join aevo_organization_entitlements entitlement
                  on entitlement.organization_id = @organization_id
                 and lower(entitlement.feature_key) = lower(requested.feature_key)
                left join aevo_organization_entitlements store_quota
                  on store_quota.organization_id = @organization_id
                 and store_quota.feature_key = 'store_application_bindings'
                left join aevo_store_application_bindings store_access
                  on store_access.organization_id = @organization_id
                 and store_access.store_id = @store_id
                 and store_access.app_code = requested.code
                """, connection);
            command.Parameters.AddWithValue("organization_id", organizationId);
            command.Parameters.AddWithValue("application_code", normalizedApplication);
            command.Parameters.AddWithValue("feature_key", featureKey);
            command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new CoreDatabaseException("The entitlement projection returned no decision row.");
            }

            var subscription = reader.IsDBNull(2)
                ? null
                : new EntitlementSubscriptionSnapshot(
                    reader.GetString(2),
                    reader.GetString(3),
                    NullableDateTimeOffset(reader, 4),
                    NullableDateTimeOffset(reader, 5),
                    NullableDateTimeOffset(reader, 6));
            var entitlement = reader.IsDBNull(7)
                ? null
                : new EntitlementFeatureSnapshot(
                    reader.GetString(7),
                    !reader.IsDBNull(8) && reader.GetBoolean(8),
                    NullableInt32(reader, 9));
            var storeQuota = reader.IsDBNull(10)
                ? null
                : new EntitlementFeatureSnapshot(
                    reader.GetString(10),
                    !reader.IsDBNull(11) && reader.GetBoolean(11),
                    NullableInt32(reader, 12));

            return new ApplicationEntitlementSnapshot(
                normalizedApplication,
                reader.GetBoolean(0),
                reader.GetBoolean(1),
                subscription,
                entitlement,
                reader.GetBoolean(13),
                reader.GetBoolean(14),
                storeQuota,
                reader.GetInt32(15));
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("The entitlement projection is not available.", error);
        }
    }

    public async Task<ApplicationEntitlementDecision> EvaluateApplicationEntitlementAsync(
        Guid organizationId,
        string application,
        Guid? storeId,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetApplicationEntitlementSnapshotAsync(organizationId, application, storeId, cancellationToken);
        return EntitlementEvaluator.Evaluate(
            organizationId,
            storeId,
            application,
            snapshot,
            DateTimeOffset.UtcNow);
    }

}
