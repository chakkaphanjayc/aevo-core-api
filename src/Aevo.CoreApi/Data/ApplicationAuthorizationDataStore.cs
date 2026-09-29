using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Runtime;
using Aevo.CoreApi.Security;

namespace Aevo.CoreApi.Data;

public sealed record CoreApplicationAuthorization(
    Guid MembershipId,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed record ConsumedAuthorizationCode(
    Guid UserId,
    string Application,
    Guid? OrganizationId,
    Guid? StoreId,
    string ReturnPath,
    string? State,
    bool RememberMe = false);

public sealed record CoreIdentityUser(Guid Id, string Email, string? DisplayName);

public sealed record ApplicationLaunchTarget(
    string Application,
    string Audience,
    string ContractVersion,
    string RegistryStatus,
    string LifecycleStatus,
    bool StoreScoped,
    string? BaseUrl,
    string? ConnectionStatus,
    DateTimeOffset? ConnectionCheckedAt);

public sealed record HandoffAuthorizationDecision(
    bool Allowed,
    string Reason,
    CoreApplicationAuthorization? Authorization);

public sealed record AccessSnapshot(
    CoreApplicationAuthorization? Authorization,
    ApplicationEntitlementDecision? Entitlement,
    DateTimeOffset CachedAt);

public sealed partial class CoreDataStore
{
    public Task<ApplicationLaunchTarget?> GetApplicationLaunchTargetAsync(
        string application,
        string environment,
        CancellationToken cancellationToken)
    {
        var appCode = application.Trim().ToUpperInvariant();
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        return memoryCache is null
            ? GetApplicationLaunchTargetFromDatabaseAsync(appCode, normalizedEnvironment, cancellationToken)
            : memoryCache.GetOrCreateAsync(
                AevoCacheKeys.ApplicationLaunchTarget(appCode, normalizedEnvironment),
                TimeSpan.FromSeconds(30),
                _ => GetApplicationLaunchTargetFromDatabaseAsync(appCode, normalizedEnvironment, cancellationToken),
                cancellationToken);
    }

    private async Task<ApplicationLaunchTarget?> GetApplicationLaunchTargetFromDatabaseAsync(
        string appCode,
        string environment,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.code, r.audience, r.contract_version, r.status,
                   r.lifecycle_status, r.store_scoped, c.base_url,
                   c.status, c.checked_at
            from aevo_application_registry r
            left join aevo_application_connections c
              on c.app_code = r.code and c.environment = @environment
            where r.code = @app_code
            """, connection);
        command.Parameters.AddWithValue("app_code", appCode);
        command.Parameters.AddWithValue("environment", environment.Trim().ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ApplicationLaunchTarget(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableDateTimeOffset(reader, 8));
    }

    public async Task<HandoffAuthorizationDecision> ResolveHandoffAuthorizationAsync(
        ConsumedAuthorizationCode handoff,
        CancellationToken cancellationToken)
    {
        if (handoff.OrganizationId is not { } organizationId) return new(false, "MEMBERSHIP_REQUIRED", null);
        var syntheticHubSession = new CoreSession(
            Guid.Empty,
            handoff.UserId,
            "HUB",
            DateTimeOffset.UtcNow.AddMinutes(5),
            organizationId,
            handoff.StoreId,
            "handoff",
            null,
            null,
            null,
            false);
        var snapshot = await ResolveAccessSnapshotAsync(
            syntheticHubSession,
            handoff.Application,
            organizationId,
            handoff.StoreId,
            false,
            cancellationToken);
        if (snapshot.Authorization is null) return new(false, "APP_ASSIGNMENT_REQUIRED", null);
        return snapshot.Entitlement is { Allowed: false }
            ? new(false, snapshot.Entitlement.Reason, snapshot.Authorization)
            : new(true, "ALLOWED", snapshot.Authorization);
    }

    public Task<AccessSnapshot> ResolveAccessSnapshotAsync(
        CoreSession session,
        string application,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId,
        bool requireSessionApplicationBinding,
        CancellationToken cancellationToken)
    {
        var normalizedApplication = application.Trim().ToUpperInvariant();
        var effectiveOrganizationId = requestedOrganizationId ?? session.OrganizationId;
        var effectiveStoreId = requestedStoreId ?? session.StoreId;
        var key = AevoCacheKeys.AccessSnapshot(
            session.Id,
            session.UserId,
            normalizedApplication,
            effectiveOrganizationId,
            effectiveStoreId,
            requireSessionApplicationBinding);

        if (memoryCache is null)
        {
            return accessSnapshotFlights.RunAsync(
                key,
                () => LoadAccessSnapshotAsync(
                    session,
                    normalizedApplication,
                    requestedOrganizationId,
                    requestedStoreId,
                    requireSessionApplicationBinding,
                    cancellationToken));
        }

        return memoryCache.GetOrCreateAsync(
            key,
            AccessSnapshotTtl,
            _ => accessSnapshotFlights.RunAsync(
                key,
                () => LoadAccessSnapshotAsync(
                    session,
                    normalizedApplication,
                    requestedOrganizationId,
                    requestedStoreId,
                    requireSessionApplicationBinding,
                    cancellationToken)),
            cancellationToken,
            snapshot => snapshot.CachedAt.Add(AccessSnapshotTtl) > DateTimeOffset.UtcNow);
    }

    public void InvalidateAccessSnapshots()
    {
        memoryCache?.RemoveByPrefix(AevoCacheKeys.AccessSnapshotPrefix);
    }

    private async Task<AccessSnapshot> LoadAccessSnapshotAsync(
        CoreSession session,
        string application,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId,
        bool requireSessionApplicationBinding,
        CancellationToken cancellationToken)
    {
        var effectiveOrganizationId = requestedOrganizationId ?? session.OrganizationId;
        var authorizationTask = ResolveApplicationAuthorizationAsync(
            session,
            application,
            requestedOrganizationId,
            requestedStoreId,
            requireSessionApplicationBinding,
            cancellationToken);
        Task<ApplicationEntitlementDecision?> entitlementTask = effectiveOrganizationId is { } organizationId
            && EntitlementEvaluator.IsCommercialApplication(application)
                ? EvaluateEntitlementNullableAsync(organizationId, application, requestedStoreId ?? session.StoreId, cancellationToken)
                : Task.FromResult<ApplicationEntitlementDecision?>(null);
        var authorization = await authorizationTask;
        var entitlement = await entitlementTask;
        return new AccessSnapshot(authorization, entitlement, DateTimeOffset.UtcNow);
    }

    private async Task<ApplicationEntitlementDecision?> EvaluateEntitlementNullableAsync(
        Guid organizationId,
        string application,
        Guid? storeId,
        CancellationToken cancellationToken)
        => await EvaluateApplicationEntitlementAsync(organizationId, application, storeId, cancellationToken);

    public async Task RecordApplicationLaunchAsync(
        Guid actorId,
        string application,
        Guid organizationId,
        Guid? storeId,
        string outcome,
        string reason,
        string requestId,
        string? audience,
        string? readiness,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var after = JsonSerializer.SerializeToElement(new
        {
            application = application.Trim().ToUpperInvariant(),
            organizationId,
            storeId,
            outcome,
            reason,
            audience,
            readiness
        });
        await InsertAuditAsync(
            connection,
            transaction,
            actorId,
            "HUB",
            "HUB_APPLICATION_LAUNCH",
            "application",
            application.Trim().ToUpperInvariant(),
            reason,
            null,
            after,
            requestId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CoreIdentityUser?> GetIdentityUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id, email, display_name from aevo_identity_users where id = @id and status = 'active'",
            connection);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CoreIdentityUser(reader.GetGuid(0), reader.GetString(1), NullableString(reader, 2))
            : null;
    }

    public Task<CoreApplicationAuthorization?> ResolveApplicationAuthorizationAsync(
        CoreSession session,
        string application,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId,
        CancellationToken cancellationToken)
        => ResolveApplicationAuthorizationAsync(
            session,
            application,
            requestedOrganizationId,
            requestedStoreId,
            true,
            cancellationToken);

    public Task<CoreApplicationAuthorization?> ResolveApplicationAuthorizationAsync(
        CoreSession session,
        string application,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId,
        bool requireSessionApplicationBinding,
        CancellationToken cancellationToken)
    {
        var normalizedApplication = application.Trim().ToUpperInvariant();
        var key = string.Join(":", new object?[]
        {
            session.Id,
            session.UserId,
            normalizedApplication,
            requestedOrganizationId ?? session.OrganizationId,
            requestedStoreId ?? session.StoreId,
            requireSessionApplicationBinding
        });
        return applicationAuthorizationFlights.RunAsync(
            key,
            () => RequestPerformance.MeasureDatabaseAsync(
                "authorization.resolve",
                () => ResolveApplicationAuthorizationFromDatabaseAsync(
                    session,
                    normalizedApplication,
                    requestedOrganizationId,
                    requestedStoreId,
                    requireSessionApplicationBinding,
                    cancellationToken)));
    }

    private async Task<CoreApplicationAuthorization?> ResolveApplicationAuthorizationFromDatabaseAsync(
        CoreSession session,
        string application,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId,
        bool requireSessionApplicationBinding,
        CancellationToken cancellationToken)
    {
        var appCode = application.Trim().ToUpperInvariant();
        if (!ApplicationCodes.All.Contains(appCode)
            || (requireSessionApplicationBinding && !string.Equals(session.AppCode, appCode, StringComparison.Ordinal))) return null;

        var organizationId = requestedOrganizationId ?? session.OrganizationId;
        var storeId = requestedStoreId ?? session.StoreId;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            session.UserId,
            organizationId,
            storeId,
            appCode,
            session.PlatformRole,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select m.id, m.organization_id, o.name, o.slug, r.code,
                   coalesce(array_agg(distinct rp.permission_code)
                     filter (where rp.permission_code is not null), '{}')
            from public.memberships m
            join public.organizations o on o.id = m.organization_id and o.status = 'ACTIVE'
            join public.roles r on r.id = m.role_id
            join aevo_application_assignments aa on aa.user_id = m.user_id
              and (aa.organization_id is null or aa.organization_id = m.organization_id)
              and aa.app_code = @application_code
              and aa.status = 'active'
              and aa.starts_at <= now()
              and (aa.expires_at is null or aa.expires_at > now())
              and (
                (@store_id is null and aa.store_id is null)
                or (@store_id is not null and (aa.store_id is null or aa.store_id = @store_id))
              )
            left join public.role_permissions rp on rp.role_id = r.id
            where m.user_id = @user_id
              and m.status = 'ACTIVE'
              and (@organization_id is null or m.organization_id = @organization_id)
              and (
                @store_id is null
                or r.code in ('OWNER', 'ADMIN')
                or exists (
                  select 1 from public.membership_stores ms
                  where ms.membership_id = m.id and ms.store_id = @store_id
                )
                or aa.store_id = @store_id
              )
              and (
                @store_id is null
                or @application_code not in ('PLAY', 'POS', 'KIOSK', 'QUEUE')
                or exists (
                  select 1 from aevo_store_application_bindings store_access
                  where store_access.organization_id = m.organization_id
                    and store_access.store_id = @store_id
                    and store_access.app_code = @application_code
                    and store_access.status = 'ACTIVE'
                )
              )
            group by m.id, m.organization_id, o.name, o.slug, r.code, m.created_at
            order by m.created_at
            limit 1
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", session.UserId);
        command.Parameters.AddWithValue("application_code", appCode);
        command.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CoreApplicationAuthorization(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<string[]>(5));
    }

    public async Task<string> CreateAuthorizationCodeAsync(
        Guid userId,
        string application,
        Guid? organizationId,
        Guid? storeId,
        string returnPath,
        string? state,
        string? codeChallenge,
        string? userAgent,
        string? ipAddress,
        CancellationToken cancellationToken,
        bool rememberMe = false)
    {
        var appCode = application.Trim().ToUpperInvariant();
        if (!ApplicationCodes.All.Contains(appCode) || appCode is "HUB" or "ADMIN") throw new CoreDatabaseException("The application cannot receive a user handoff.");
        var code = OpaqueToken();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_authorization_codes
              (code_hash, user_id, app_code, organization_id, store_id, remember_me, return_path, state_hash, code_challenge, expires_at, user_agent, ip_address)
            values
              (@code_hash, @user_id, @app_code, @organization_id, @store_id, @remember_me, @return_path, @state_hash, @code_challenge, @expires_at, @user_agent, @ip_address)
            """, connection);
        command.Parameters.AddWithValue("code_hash", SessionHash(code));
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("app_code", appCode);
            command.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
            command.Parameters.AddWithValue("remember_me", rememberMe);
            command.Parameters.AddWithValue("return_path", returnPath);
        command.Parameters.Add(new NpgsqlParameter("state_hash", NpgsqlDbType.Text) { Value = (object?)(state is null ? null : SessionHash(state)) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("code_challenge", NpgsqlDbType.Text) { Value = (object?)codeChallenge ?? DBNull.Value });
        command.Parameters.AddWithValue("expires_at", now.AddMinutes(5));
        command.Parameters.AddWithValue("user_agent", (object?)Limit(userAgent, 512) ?? DBNull.Value);
        command.Parameters.AddWithValue("ip_address", (object?)Limit(ipAddress, 128) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return code;
    }

    public async Task<ConsumedAuthorizationCode?> ConsumeAuthorizationCodeAsync(
        string code,
        string application,
        string? state,
        string? codeVerifier,
        CancellationToken cancellationToken)
    {
        var appCode = application.Trim().ToUpperInvariant();
        if (!ApplicationCodes.All.Contains(appCode) || appCode is "HUB" or "ADMIN" || string.IsNullOrWhiteSpace(code)) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var lookup = new NpgsqlCommand(
            """
            select id, user_id, organization_id, store_id, remember_me, return_path, state_hash, code_challenge
            from aevo_authorization_codes
            where code_hash = @code_hash
              and app_code = @app_code
              and consumed_at is null
              and expires_at > now()
            for update
            """, connection, transaction);
        lookup.Parameters.AddWithValue("code_hash", SessionHash(code));
        lookup.Parameters.AddWithValue("app_code", appCode);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var id = reader.GetGuid(0);
            var userId = reader.GetGuid(1);
            var organizationId = NullableGuid(reader, 2);
            var storeId = NullableGuid(reader, 3);
            var rememberMe = reader.GetBoolean(4);
            var returnPath = reader.GetString(5);
            var stateHash = NullableString(reader, 6);
            var challenge = NullableString(reader, 7);
        await reader.DisposeAsync();

        if (stateHash is not null && (state is null || !string.Equals(stateHash, SessionHash(state), StringComparison.Ordinal))) return null;
        if (challenge is not null && (codeVerifier is null || !string.Equals(challenge, SessionHash(codeVerifier), StringComparison.Ordinal))) return null;

        await using var consume = new NpgsqlCommand(
            "update aevo_authorization_codes set consumed_at = now() where id = @id and consumed_at is null",
            connection, transaction);
        consume.Parameters.AddWithValue("id", id);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return new ConsumedAuthorizationCode(userId, appCode, organizationId, storeId, returnPath, state, rememberMe);
    }

    private static string? Limit(string? value, int length)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}
