using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record HubOnboardingSessionRecord(
    Guid Id,
    Guid UserId,
    Guid? OrganizationId,
    Guid? StoreId,
    string CurrentStep,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> CompletedSteps,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed partial class CoreDataStore
{
    public async Task<HubOnboardingSessionRecord> GetOrCreateHubOnboardingSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var lookup = new NpgsqlCommand(
            "select id,user_id,organization_id,store_id,current_step,objectives,completed_steps,is_completed,created_at,updated_at from public.onboarding_sessions where user_id=@user_id order by created_at desc limit 1",
            connection))
        {
            lookup.Parameters.AddWithValue("user_id", userId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) return ReadOnboardingSession(reader);
        }

        await using var insert = new NpgsqlCommand(
            "insert into public.onboarding_sessions (user_id,current_step,objectives,completed_steps,is_completed) values (@user_id,'REGISTER','[]'::jsonb,'[\"REGISTER\"]'::jsonb,false) returning id,user_id,organization_id,store_id,current_step,objectives,completed_steps,is_completed,created_at,updated_at",
            connection);
        insert.Parameters.AddWithValue("user_id", userId);
        await using var created = await insert.ExecuteReaderAsync(cancellationToken);
        if (!await created.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Onboarding session creation returned no session.");
        return ReadOnboardingSession(created);
    }

    public async Task<HubOnboardingSessionRecord?> GetHubOnboardingSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id,user_id,organization_id,store_id,current_step,objectives,completed_steps,is_completed,created_at,updated_at from public.onboarding_sessions where id=@id and user_id=@user_id",
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOnboardingSession(reader) : null;
    }

    public async Task<HubOnboardingSessionRecord> UpdateHubOnboardingSessionAsync(
        Guid userId,
        Guid sessionId,
        Guid? organizationId,
        Guid? storeId,
        string currentStep,
        IReadOnlyCollection<string> objectives,
        IReadOnlyCollection<string> completedSteps,
        bool? isCompleted,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update public.onboarding_sessions set organization_id=coalesce(@organization_id,organization_id),store_id=coalesce(@store_id,store_id),current_step=@current_step,objectives=@objectives,completed_steps=@completed_steps,is_completed=coalesce(@is_completed,is_completed),updated_at=now() where id=@id and user_id=@user_id returning id,user_id,organization_id,store_id,current_step,objectives,completed_steps,is_completed,created_at,updated_at",
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
        command.Parameters.AddWithValue("current_step", currentStep);
        command.Parameters.Add(new NpgsqlParameter("objectives", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(objectives) });
        command.Parameters.Add(new NpgsqlParameter("completed_steps", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(completedSteps) });
        command.Parameters.Add(new NpgsqlParameter("is_completed", NpgsqlDbType.Boolean) { Value = (object?)isCompleted ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Onboarding session was not found for this user.");
        return ReadOnboardingSession(reader);
    }

    public async Task<JsonElement> GetHubOnboardingChecklistAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select jsonb_build_object(
              'organization', exists (select 1 from public.organizations where id=@organization_id and status='ACTIVE'),
              'stores', (select count(*) from public.stores where organization_id=@organization_id and status='ACTIVE'),
              'members', (select count(*) from public.memberships where organization_id=@organization_id and status='ACTIVE'),
              'enabledApps', (select count(*) from aevo_application_assignments a where a.organization_id=@organization_id and a.user_id=@user_id and a.status='active' and a.app_code <> 'HUB'),
              'enabledStoreApps', (select count(*) from aevo_store_application_bindings where organization_id=@organization_id and status='ACTIVE'),
              'venues', (select count(*) from public.venues where organization_id=@organization_id and status='ACTIVE')
            )
            """,
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("organization_id", organizationId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "{}");
    }

    public async Task<JsonElement?> SetupHubOnboardingBookingAsync(
        Guid organizationId,
        Guid? storeId,
        JsonElement body,
        CancellationToken cancellationToken)
    {
        var venueName = JsonString(body, "venueName")?.Trim();
        if (string.IsNullOrWhiteSpace(venueName)) throw new CoreDatabaseException("A venueName is required for booking setup.");
        var slug = $"{Slugify(venueName)}-{Guid.NewGuid().ToString("N")[..10]}";
        var duration = Math.Clamp(JsonInt(body, "durationMinutes") ?? 60, 15, 240);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid venueId;
        await using (var venue = new NpgsqlCommand(
            "insert into public.venues (organization_id,store_id,name,slug,description,timezone,slot_duration_minutes,status) values (@organization_id,@store_id,@name,@slug,@description,@timezone,@duration,'ACTIVE') returning id",
            connection, transaction))
        {
            venue.Parameters.AddWithValue("organization_id", organizationId);
            venue.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
            venue.Parameters.AddWithValue("name", venueName);
            venue.Parameters.AddWithValue("slug", slug);
            venue.Parameters.AddWithValue("description", JsonString(body, "businessType") is { } businessType ? $"Venue created during onboarding for {businessType}" : "Venue created during onboarding");
            venue.Parameters.AddWithValue("timezone", JsonString(body, "timezone") ?? "Asia/Bangkok");
            venue.Parameters.AddWithValue("duration", duration);
            venueId = (Guid)(await venue.ExecuteScalarAsync(cancellationToken) ?? throw new CoreDatabaseException("Booking venue creation failed."));
        }

        var resourceNames = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("resourceNames", out var resources) && resources.ValueKind == JsonValueKind.Array
            ? resources.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Take(100).ToArray()
            : Array.Empty<string>();
        foreach (var resourceName in resourceNames)
        {
            await using var resource = new NpgsqlCommand(
                "insert into public.bookable_resources (organization_id,venue_id,name,resource_type,capacity,base_price_minor,status) values (@organization_id,@venue_id,@name,@resource_type,4,@price,'ACTIVE')",
                connection, transaction);
            resource.Parameters.AddWithValue("organization_id", organizationId);
            resource.Parameters.AddWithValue("venue_id", venueId);
            resource.Parameters.AddWithValue("name", resourceName);
            resource.Parameters.AddWithValue("resource_type", JsonString(body, "businessType")?.Contains("sport", StringComparison.OrdinalIgnoreCase) == true ? "COURT" : "TABLE");
            resource.Parameters.AddWithValue("price", Math.Max(0, JsonInt(body, "priceMinor") ?? 0));
            await resource.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return JsonDocument.Parse(JsonSerializer.Serialize(new { venueId, resourceCount = resourceNames.Length })).RootElement.Clone();
    }

    public async Task MarkHubOrganizationOnboardingCompleteAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update public.organizations set onboarding_status='COMPLETED',updated_at=now() where id=@organization_id", connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static HubOnboardingSessionRecord ReadOnboardingSession(NpgsqlDataReader reader)
    {
        return new HubOnboardingSessionRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            NullableGuid(reader, 2),
            NullableGuid(reader, 3),
            reader.GetString(4),
            JsonStringArray(reader.GetValue(5)),
            JsonStringArray(reader.GetValue(6)),
            reader.GetBoolean(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }

    private static string[] JsonStringArray(object value)
    {
        var text = value is string stringValue ? stringValue : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "[]";
        try { return JsonSerializer.Deserialize<string[]>(text) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "aevo-venue" : slug[..Math.Min(slug.Length, 50)];
    }
}
