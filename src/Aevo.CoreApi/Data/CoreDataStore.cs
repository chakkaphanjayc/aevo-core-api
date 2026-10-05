using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Runtime;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed class CoreDatabaseException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record CoreSession(
    Guid Id,
    Guid UserId,
    string AppCode,
    DateTimeOffset ExpiresAt,
    Guid? OrganizationId,
    Guid? StoreId,
    string Email,
    string? DisplayName,
    string? PlatformRole,
    string? CsrfTokenHash,
    bool RememberMe = false,
    DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? AbsoluteExpiresAt = null);

public sealed record SessionSnapshot(CoreSession Session, DateTimeOffset CachedAt);

public sealed record HubSessionBootstrapRecord(
    CoreSession Session,
    HubPrincipalRecord? Principal,
    IReadOnlyList<HubStoreRecord> Stores);

public sealed record IssuedCoreSession(
    Guid SessionId,
    Guid UserId,
    string AppCode,
    string Email,
    string? DisplayName,
    string SessionToken,
    string CsrfToken,
    DateTimeOffset ExpiresAt,
    bool RememberMe = false,
    DateTimeOffset? AbsoluteExpiresAt = null);

public sealed record PasswordRecoveryGrant(
    Guid UserId,
    string Email,
    string ProviderAccessToken);

public sealed record ApplicationRegistryRecord(
    string Code,
    string Name,
    string Kind,
    string Status,
    string ManifestVersion,
    string OwnerRepository,
    string ContractVersion,
    string Audience,
    string InstallScope,
    bool StoreScoped,
    string LaunchPath,
    string LifecycleStatus,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ConfigSchemaRefs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationConnectionRecord(
    string AppCode,
    string Label,
    string Status,
    string? BaseUrl,
    DateTimeOffset? CheckedAt,
    int? LatencyMs,
    string? LastErrorCode,
    JsonElement? Metadata);

public sealed record PlatformOverviewRecord(
    long TotalOrganizations,
    long TotalStores,
    long TotalUsers,
    long TotalDevices,
    long TotalSubscriptions,
    string OperatingMode,
    bool Unlimited);

public sealed record MigrationAuthorityRecord(
    string SchemaName,
    string TableName,
    string SemanticDomain,
    IReadOnlyList<string> HistoricalMigrationRepos,
    string CurrentOwner,
    string TargetOwner,
    IReadOnlyList<string> RuntimeWriters,
    IReadOnlyList<string> Readers,
    string CurrentWriteMode,
    string MigrationPhase,
    string TransitionState,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset UpdatedAt,
    string? DeleteAfter);

public sealed record AdminAuditRecord(
    Guid Id,
    Guid ActorId,
    string? ActorEmail,
    string? PlatformRole,
    string AppCode,
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    JsonElement? BeforeState,
    JsonElement? AfterState,
    string? RequestId,
    DateTimeOffset CreatedAt);

public sealed record AdminAuditPage(
    IReadOnlyList<AdminAuditRecord> Items,
    bool HasMore);

public sealed record AdminPlaceRegistryRecord(
    Guid PlaceId,
    string Slug,
    string Name,
    string Status,
    string CategoryId,
    double? DisplayLatitude,
    double? DisplayLongitude,
    long Revision,
    string SourceRevision,
    string? ProjectionStatus,
    string? FreshnessState,
    string? ProjectionVersion,
    DateTimeOffset? ProjectionGeneratedAt);

public sealed record GoFeatureFlagRecord(
    string FlagKey,
    bool Enabled,
    int RolloutPercent,
    JsonElement Config,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

public sealed record GoSettingsRecord(
    JsonElement Settings,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy,
    IReadOnlyList<GoFeatureFlagRecord> FeatureFlags);

public sealed record GoDailyActivityRecord(
    DateOnly Date,
    long Events,
    long Impressions,
    long Interactions);

public sealed record GoOverviewRecord(
    DateTimeOffset AsOf,
    int WindowDays,
    long ActivePlaces,
    long PublicStores,
    long PublishedTraces,
    long TotalJourneys,
    long CompletedJourneys,
    long BookingsCreated,
    long BookingsCompleted,
    long BookingsCancelled,
    long OrdersCreated,
    long OrdersCompleted,
    long RatingsCount,
    decimal AverageRating,
    long OpenReports,
    long ActivityEvents,
    long FeedImpressions,
    long FeedInteractions,
    long PendingOutbox,
    int EnabledFeatureFlags,
    int FeatureFlagCount,
    IReadOnlyList<GoDailyActivityRecord> DailyActivity);

public sealed partial class CoreDataStore : IAsyncDisposable
{
    private static readonly Action<ILogger, string, Exception?> InvalidDatabaseConfigurationLog =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1001, nameof(InvalidDatabaseConfigurationLog)), "Core API database configuration is invalid: {Message}");

    private readonly NpgsqlDataSource? dataSource;
    private readonly string? configurationError;
    private readonly byte[]? recoveryEncryptionKey;
    private readonly IAevoMemoryCache? memoryCache;
    private readonly string environment; // runtime environment used by Hub read-model warmup
    private readonly AevoSingleFlight<CoreSession?> sessionResolutionFlights = new();
    private readonly AevoSingleFlight<CoreApplicationAuthorization?> applicationAuthorizationFlights = new();
    private readonly AevoSingleFlight<HubPrincipalRecord?> hubPrincipalFlights = new();
    private readonly AevoSingleFlight<HubSessionBootstrapRecord?> hubBootstrapFlights = new();
    private readonly AevoSingleFlight<AccessSnapshot> accessSnapshotFlights = new();
    private readonly TimeSpan sessionSnapshotTtl;
    private readonly TimeSpan accessSnapshotTtl;

    public CoreDataStore(IConfiguration configuration, ILogger<CoreDataStore> logger, IAevoMemoryCache? memoryCache = null)
    {
        this.memoryCache = memoryCache;
        environment = configuration["AEVO_ENVIRONMENT"]?.Trim() is { Length: > 0 } configuredEnvironment
            ? configuredEnvironment
            : "development";
        sessionSnapshotTtl = ReadSnapshotTtl(configuration["AEVO_SESSION_SNAPSHOT_TTL_SECONDS"], 60);
        accessSnapshotTtl = ReadSnapshotTtl(configuration["AEVO_ACCESS_SNAPSHOT_TTL_SECONDS"], 60);
        var recoverySecret = configuration["AEVO_RECOVERY_GRANT_ENCRYPTION_KEY"]?.Trim()
            ?? configuration["AEVO_SESSION_SECRET"]?.Trim();
        if (!string.IsNullOrWhiteSpace(recoverySecret))
        {
            recoveryEncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(recoverySecret));
        }

        var rawConnectionString = configuration["AEVO_DATABASE_URL"]?.Trim();
        if (string.IsNullOrWhiteSpace(rawConnectionString)) return;

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(NormalizeDatabaseConnectionString(rawConnectionString));
            connectionString.MinPoolSize = ReadBoundedInt(configuration["AEVO_DATABASE_POOL_MIN_SIZE"], 4, 0, 32);
            connectionString.MaxPoolSize = ReadBoundedInt(configuration["AEVO_DATABASE_POOL_MAX_SIZE"], 100, 1, 256);
            if (connectionString.MaxPoolSize < connectionString.MinPoolSize)
            {
                connectionString.MaxPoolSize = connectionString.MinPoolSize;
            }
            dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
        }
        catch (Exception)
        {
            configurationError = "The AEVO_DATABASE_URL value is invalid.";
            // Never attach the provider exception here: Npgsql may echo the
            // full connection string, including a database password.
            InvalidDatabaseConfigurationLog(logger, configurationError, null);
        }
    }

    public bool IsConfigured => dataSource is not null;

    internal TimeSpan SessionSnapshotTtl => sessionSnapshotTtl;

    internal TimeSpan AccessSnapshotTtl => accessSnapshotTtl;

    private static TimeSpan ReadSnapshotTtl(string? value, int fallbackSeconds)
    {
        var seconds = int.TryParse(value, out var parsed) ? parsed : fallbackSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    public void InvalidateApplicationRegistryCache(string? application = null, string? environment = null)
    {
        if (memoryCache is null) return;
        memoryCache.Remove(AevoCacheKeys.ApplicationRegistry);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            memoryCache.Remove(AevoCacheKeys.ApplicationConnections(environment));
        }
        else
        {
            memoryCache.RemoveByPrefix(AevoCacheKeys.ApplicationConnectionsPrefix);
        }
        if (!string.IsNullOrWhiteSpace(application) && !string.IsNullOrWhiteSpace(environment))
        {
            memoryCache.Remove(AevoCacheKeys.ApplicationLaunchTarget(application, environment));
        }
        else if (!string.IsNullOrWhiteSpace(application))
        {
            foreach (var knownEnvironment in new[] { "local", "development", "test", "staging", "production" })
            {
                memoryCache.Remove(AevoCacheKeys.ApplicationLaunchTarget(application, knownEnvironment));
            }
        }
    }

    /// <summary>
    /// Removes the short-lived session snapshot after a security-state write
    /// or an idle-expiry extension. Authorization snapshots are also removed
    /// because they contain the session context used to authorize the request.
    /// </summary>
    public void InvalidateSessionSnapshot(string? appCode, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return;
        var tokenHash = SessionHash(sessionToken);
        if (memoryCache is not null)
        {
            if (!string.IsNullOrWhiteSpace(appCode))
            {
                memoryCache.Remove(AevoCacheKeys.Session(appCode, tokenHash));
            }
            else
            {
                foreach (var code in new[] { "HUB", "ADMIN", "GO", "PLAY", "POS", "KIOSK", "QUEUE", "DIGITAL_SIGN", "*" })
                {
                    memoryCache.Remove(AevoCacheKeys.Session(code, tokenHash));
                }
            }
            memoryCache.Remove(AevoCacheKeys.HubBootstrap(tokenHash));
        }

        InvalidateAuthorizationCaches();
    }

    /// <summary>
    /// Clears all in-process authorization snapshots. This is deliberately
    /// broad: role, assignment, membership, entitlement, and store-binding
    /// writes can affect more than one cached access key. The cache remains an
    /// optimization only; Core still evaluates every miss against the DB/RLS
    /// source of truth.
    /// </summary>
    public void InvalidateAuthorizationCaches()
    {
        memoryCache?.RemoveByPrefix(AevoCacheKeys.AccessSnapshotPrefix);
        memoryCache?.RemoveByPrefix(AevoCacheKeys.HubPrincipalPrefix);
        memoryCache?.RemoveByPrefix(AevoCacheKeys.HubBootstrapPrefix);
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        if (dataSource is null) return false;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception error)
        {
            throw DatabaseUnavailable(error);
        }
    }

    public async Task WarmConnectionPoolAsync(int connectionCount, CancellationToken cancellationToken)
    {
        var boundedCount = Math.Clamp(connectionCount, 1, 16);
        var warmups = Enumerable.Range(0, boundedCount)
            .Select(_ => CanConnectAsync(cancellationToken))
            .ToArray();
        await Task.WhenAll(warmups);
    }

    public Task<CoreSession?> ResolveSessionAsync(string sessionToken, string? appCode, CancellationToken cancellationToken)
    {
        // Session state is mutable security state: logout, password changes,
        // refresh-token rotation, idle expiry, and administrator revocation
        // must take effect on the next request. Coalesce only concurrent
        // source-of-truth reads; AevoSingleFlight never retains a result.
        var normalizedApp = appCode?.Trim().ToUpperInvariant() ?? "*";
        var tokenHash = SessionHash(sessionToken);
        var key = $"{normalizedApp}:{tokenHash}";
        if (memoryCache is null)
        {
            return ResolveSessionSingleFlightAsync(key, sessionToken, appCode, cancellationToken);
        }

        return ResolveSessionSnapshotAsync(key, tokenHash, sessionToken, appCode, cancellationToken);
    }

    /// <summary>
    /// Resolves the Hub session, principal, and authorized store index in one
    /// database statement. The read model is short-lived and invalidated with
    /// the ordinary session/authorization caches; it is not a permission
    /// authority and never crosses the opaque session-token boundary.
    /// </summary>
    public Task<HubSessionBootstrapRecord?> ResolveHubSessionBootstrapAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = SessionHash(sessionToken);
        var key = tokenHash;
        return hubBootstrapFlights.RunAsync(key, () =>
        {
            if (memoryCache is null)
            {
                return RequestPerformance.MeasureDatabaseAsync(
                    "hub.bootstrap",
                    () => ResolveHubSessionBootstrapFromDatabaseAsync(sessionToken, cancellationToken));
            }

            return memoryCache.GetOrCreateAsync(
                AevoCacheKeys.HubBootstrap(tokenHash),
                sessionSnapshotTtl,
                ct => RequestPerformance.MeasureDatabaseAsync(
                    "hub.bootstrap",
                    () => ResolveHubSessionBootstrapFromDatabaseAsync(sessionToken, ct)),
                cancellationToken,
                IsHubSessionBootstrapUsable);
        });
    }

    private static bool IsHubSessionBootstrapUsable(HubSessionBootstrapRecord? bootstrap)
    {
        if (bootstrap is null) return false;
        var now = DateTimeOffset.UtcNow;
        return bootstrap.Session.ExpiresAt > now
            && (bootstrap.Session.AbsoluteExpiresAt is null || bootstrap.Session.AbsoluteExpiresAt > now);
    }

    private async Task<HubSessionBootstrapRecord?> ResolveHubSessionBootstrapFromDatabaseAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with session_row as materialized (
              select s.id, s.user_id, s.app_code, s.expires_at, s.organization_id, s.store_id,
                     u.email, u.display_name, r.role_code, s.csrf_token_hash, s.remember_me, s.last_seen_at,
                     s.absolute_expires_at
              from aevo_app_sessions s
              join aevo_identity_users u on u.id = s.user_id and u.status = 'active'
              left join aevo_platform_roles r on r.user_id = s.user_id and r.status = 'active'
              where s.session_hash = @session_hash
                and s.app_code = 'HUB'
                and s.revoked_at is null
                and s.expires_at > now()
                and s.idle_expires_at > now()
                and s.absolute_expires_at > now()
              limit 1
            ), context as materialized (
              select
                set_config('aevo.user_id', session_row.user_id::text, true),
                set_config('aevo.organization_id', coalesce(session_row.organization_id::text, ''), true),
                set_config('aevo.store_id', coalesce(session_row.store_id::text, ''), true),
                set_config('aevo.app_code', 'HUB', true),
                set_config('aevo.platform_role', coalesce(session_row.role_code, ''), true)
              from session_row
            ), principal_row as materialized (
              select m.id as membership_id, m.organization_id, o.name as organization_name, o.slug as organization_slug,
                     r.code as role, coalesce(array_agg(distinct rp.permission_code)
                       filter (where rp.permission_code is not null), '{}') as permissions
              from session_row session
              cross join context
              join public.memberships m on m.user_id = session.user_id
                and m.status = 'ACTIVE'
                and (session.organization_id is null or m.organization_id = session.organization_id)
              join public.organizations o on o.id = m.organization_id and o.status = 'ACTIVE'
              join public.roles r on r.id = m.role_id
              left join public.role_permissions rp on rp.role_id = r.id
              where exists (
                select 1
                from aevo_application_assignments aa
                where aa.user_id = m.user_id
                  and (aa.organization_id is null or aa.organization_id = m.organization_id)
                  and aa.app_code = 'HUB'
                  and aa.status = 'active'
                  and aa.starts_at <= now()
                  and (aa.expires_at is null or aa.expires_at > now())
              )
              group by m.id, m.organization_id, o.name, o.slug, r.code, m.created_at
              order by m.created_at
              limit 1
            ), store_rows as materialized (
              select s.id, s.organization_id, s.code, s.name, s.timezone, s.currency, s.status,
                     s.store_mode, s.address, s.phone, s.tax_id, s.created_at, s.updated_at
              from public.stores s
              cross join principal_row principal
              where s.organization_id = principal.organization_id
                and (
                  s.status = 'ACTIVE'
                  or exists (
                    select 1
                    from aevo_data_deletion_requests deletion
                    where deletion.organization_id = s.organization_id
                      and deletion.resource_type = 'STORE'
                      and deletion.resource_id = s.id
                      and deletion.status = 'PENDING'
                  )
                )
                and (
                  principal.role in ('OWNER', 'ADMIN')
                  or exists (
                    select 1
                    from public.membership_stores ms
                    where ms.membership_id = principal.membership_id
                      and ms.store_id = s.id
                  )
                )
            )
            select jsonb_build_object(
              'session', coalesce((
                select jsonb_build_object(
                  'id', id,
                  'userId', user_id,
                  'appCode', app_code,
                  'expiresAt', expires_at,
                  'organizationId', organization_id,
                  'storeId', store_id,
                  'email', email,
                  'displayName', display_name,
                  'platformRole', role_code,
                  'csrfTokenHash', csrf_token_hash,
                  'rememberMe', remember_me,
                  'lastSeenAt', last_seen_at,
                  'absoluteExpiresAt', absolute_expires_at
                ) from session_row
              ), 'null'::jsonb),
              'principal', coalesce((
                select jsonb_build_object(
                  'userId', session.user_id,
                  'membershipId', principal.membership_id,
                  'organizationId', principal.organization_id,
                  'organizationName', principal.organization_name,
                  'organizationSlug', principal.organization_slug,
                  'role', principal.role,
                  'permissions', to_jsonb(principal.permissions)
                )
                from principal_row principal
                cross join session_row session
              ), 'null'::jsonb),
              'stores', coalesce((
                select jsonb_agg(jsonb_build_object(
                  'id', id,
                  'organizationId', organization_id,
                  'code', code,
                  'name', name,
                  'timezone', timezone,
                  'currency', currency,
                  'status', status,
                  'storeMode', store_mode,
                  'address', address,
                  'phone', phone,
                  'taxId', tax_id,
                  'createdAt', created_at,
                  'updatedAt', updated_at
                ) order by created_at)
                from store_rows
              ), '[]'::jsonb)
            )
            ;

            -- The first authenticated Hub read also needs the organization
            -- dashboard. Derive the same authorized organization here so the
            -- bootstrap and dashboard read models cross the remote boundary
            -- in one command exchange.
            with session_context as materialized (
              select s.organization_id, s.user_id
              from aevo_app_sessions s
              join aevo_identity_users u on u.id = s.user_id and u.status = 'active'
              where s.session_hash = @session_hash
                and s.app_code = 'HUB'
                and s.revoked_at is null
                and s.expires_at > now()
                and s.idle_expires_at > now()
                and s.absolute_expires_at > now()
              limit 1
            ), principal_context as materialized (
              select m.organization_id
              from session_context session
              join public.memberships m on m.user_id = session.user_id
                and m.status = 'ACTIVE'
                and (session.organization_id is null or m.organization_id = session.organization_id)
              join public.organizations o on o.id = m.organization_id and o.status = 'ACTIVE'
              join public.roles r on r.id = m.role_id
              where exists (
                select 1
                from aevo_application_assignments aa
                where aa.user_id = m.user_id
                  and (aa.organization_id is null or aa.organization_id = m.organization_id)
                  and aa.app_code = 'HUB'
                  and aa.status = 'active'
                  and aa.starts_at <= now()
                  and (aa.expires_at is null or aa.expires_at > now())
              )
              order by m.created_at
              limit 1
            )
            select
              (
                select projection
                from aevo_hub_dashboard_projections
                where organization_id = (select organization_id from principal_context)
                  and store_id is null
              ) as projection,
              (
                select generated_at
                from aevo_hub_dashboard_projections
                where organization_id = (select organization_id from principal_context)
                  and store_id is null
              ) as generated_at,
              case when (select organization_id from principal_context) is null then '{}'::jsonb else
                jsonb_build_object(
                  'organizationId', (select organization_id from principal_context),
                  'totalStores', (select count(*) from public.stores where organization_id = (select organization_id from principal_context) and status = 'ACTIVE'),
                  'totalMembers', (select count(*) from public.memberships where organization_id = (select organization_id from principal_context) and status = 'ACTIVE'),
                  'activeApps', (select count(distinct app_code) from aevo_store_application_bindings where organization_id = (select organization_id from principal_context) and status = 'ACTIVE'),
                  'activeDevices', (select count(*) from public.devices where organization_id = (select organization_id from principal_context) and status = 'ACTIVE')
                )
              end as stats,
              (
                select coalesce(jsonb_agg(jsonb_build_object(
                  'appCode', r.code,
                  'label', r.name,
                  'status', coalesce(c.status, 'not_configured'),
                  'baseUrl', c.base_url,
                  'checkedAt', c.checked_at,
                  'latencyMs', c.latency_ms,
                  'lastErrorCode', c.last_error_code,
                  'metadata', c.metadata
                ) order by r.code), '[]'::jsonb)
                from aevo_application_registry r
                left join aevo_application_connections c
                  on c.app_code = r.code and c.environment = @environment
              ) as connections
            """, connection);
        command.Parameters.AddWithValue("session_hash", SessionHash(sessionToken));
        command.Parameters.AddWithValue("environment", environment);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0)) return null;
        using var document = JsonDocument.Parse(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "{}");
        var root = document.RootElement;
        var sessionElement = root.GetProperty("session");
        if (sessionElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var session = sessionElement.Deserialize<CoreSession>(options)
            ?? throw new CoreDatabaseException("Hub session bootstrap returned no session.");
        var principalElement = root.GetProperty("principal");
        var principal = principalElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : principalElement.Deserialize<HubPrincipalRecord>(options);
        var stores = root.GetProperty("stores").Deserialize<HubStoreRecord[]>(options) ?? [];
        var bootstrap = new HubSessionBootstrapRecord(session, principal, stores);

        if (principal is null || memoryCache is null) return bootstrap;

        try
        {
            if (!await reader.NextResultAsync(cancellationToken) || !await reader.ReadAsync(cancellationToken)) return bootstrap;
            JsonElement? storedProjection = reader.IsDBNull(0) ? null : ParseJson(reader.GetValue(0), "{}");
            var generatedAt = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
            var stats = ParseJson(reader.GetValue(2), "{}");
            var connections = JsonSerializer.Deserialize<ApplicationConnectionRecord[]>(
                ParseJson(reader.GetValue(3), "[]").GetRawText(),
                HubDashboardJsonOptions) ?? [];
            JsonElement dashboard;
            if (storedProjection.HasValue && generatedAt.HasValue && generatedAt.Value >= DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                dashboard = storedProjection.Value;
            }
            else
            {
                var checkedAt = connections
                    .Where(connection => connection.CheckedAt.HasValue)
                    .Select(connection => connection.CheckedAt!.Value)
                    .DefaultIfEmpty(DateTimeOffset.UtcNow)
                    .Max();
                var partial = connections.Any(connection => connection.Status is "degraded" or "not_connected");
                dashboard = BuildHubDashboardProjection(
                    principal.OrganizationId,
                    null,
                    stats,
                    connections,
                    partial ? "partial" : "fresh",
                    checkedAt,
                    DateTimeOffset.UtcNow,
                    partial ? "APPLICATION_CONNECTION_PARTIAL" : null);
            }
            // Seed the dashboard scope from the same authoritative read so a
            // following dashboard route does not reopen the remote connection.
            memoryCache.Put(
                AevoCacheKeys.HubDashboardProjection(principal.OrganizationId, null),
                dashboard,
                TimeSpan.FromMinutes(5));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Dashboard warmup is an optional read model. Keep the
            // authoritative session/bootstrap result available if its second
            // result set cannot be consumed.
        }

        return bootstrap;
    }

    private async Task<CoreSession?> ResolveSessionSingleFlightAsync(
        string key,
        string sessionToken,
        string? appCode,
        CancellationToken cancellationToken)
        => await sessionResolutionFlights.RunAsync(
            key,
            () => RequestPerformance.MeasureDatabaseAsync(
                "session.resolve",
                () => ResolveSessionCoreAsync(sessionToken, appCode, cancellationToken)));

    private async Task<CoreSession?> ResolveSessionSnapshotAsync(
        string key,
        string tokenHash,
        string sessionToken,
        string? appCode,
        CancellationToken cancellationToken)
    {
        var cacheKey = AevoCacheKeys.Session(appCode ?? "*", tokenHash);
        var snapshot = await memoryCache!.GetOrCreateAsync<SessionSnapshot?>(
            cacheKey,
            sessionSnapshotTtl,
            _ => LoadSessionSnapshotAsync(key, sessionToken, appCode, cancellationToken),
            cancellationToken,
            IsSessionSnapshotUsable);
        return snapshot?.Session;
    }

    private async Task<SessionSnapshot?> LoadSessionSnapshotAsync(
        string key,
        string sessionToken,
        string? appCode,
        CancellationToken cancellationToken)
    {
        var session = await sessionResolutionFlights.RunAsync(
            key,
            () => RequestPerformance.MeasureDatabaseAsync(
                "session.resolve",
                () => ResolveSessionCoreAsync(sessionToken, appCode, cancellationToken)));
        return session is null ? null : new SessionSnapshot(session, DateTimeOffset.UtcNow);
    }

    private static bool IsSessionSnapshotUsable(SessionSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        var now = DateTimeOffset.UtcNow;
        return snapshot.Session.ExpiresAt > now
            && (snapshot.Session.AbsoluteExpiresAt is null || snapshot.Session.AbsoluteExpiresAt > now);
    }

    private async Task<CoreSession?> ResolveSessionCoreAsync(string sessionToken, string? appCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select s.id, s.user_id, s.app_code, s.expires_at, s.organization_id, s.store_id,
                   u.email, u.display_name, r.role_code, s.csrf_token_hash, s.remember_me, s.last_seen_at,
                   s.absolute_expires_at
            from aevo_app_sessions s
            join aevo_identity_users u on u.id = s.user_id and u.status = 'active'
            left join aevo_platform_roles r on r.user_id = s.user_id and r.status = 'active'
            where s.session_hash = @session_hash
              and (@app_code is null or s.app_code = @app_code)
              and s.revoked_at is null
              and s.expires_at > now()
              and s.idle_expires_at > now()
              and s.absolute_expires_at > now()
            limit 1
            """, connection);
        command.Parameters.AddWithValue("session_hash", SessionHash(sessionToken));
        command.Parameters.Add(new NpgsqlParameter("app_code", NpgsqlDbType.Text) { Value = (object?)appCode ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CoreSession(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            NullableGuid(reader, 4),
            NullableGuid(reader, 5),
            reader.GetString(6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableString(reader, 9),
            reader.GetBoolean(10),
            NullableDateTimeOffset(reader, 11),
            reader.GetFieldValue<DateTimeOffset>(12));
    }

    public Task<IssuedCoreSession> CreateSessionAsync(
        Guid userId,
        string email,
        string? displayName,
        string appCode,
        Guid? organizationId,
        Guid? storeId,
        int idleTimeoutSeconds,
        int absoluteTimeoutSeconds,
        CancellationToken cancellationToken,
        bool rememberMe = false)
        => RequestPerformance.MeasureDatabaseAsync(
            "session.create",
            () => CreateSessionFromDatabaseAsync(
                userId,
                email,
                displayName,
                appCode,
                organizationId,
                storeId,
                idleTimeoutSeconds,
                absoluteTimeoutSeconds,
                rememberMe,
                cancellationToken));

    private async Task<IssuedCoreSession> CreateSessionFromDatabaseAsync(
        Guid userId,
        string email,
        string? displayName,
        string appCode,
        Guid? organizationId,
        Guid? storeId,
        int idleTimeoutSeconds,
        int absoluteTimeoutSeconds,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        if (!ApplicationCodes.All.Contains(appCode)) throw new CoreDatabaseException("Unknown application code.");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) throw new CoreDatabaseException("A valid identity email is required.");
        ValidateSessionTimeouts(idleTimeoutSeconds, absoluteTimeoutSeconds);

        var sessionId = Guid.NewGuid();
        var sessionToken = OpaqueToken();
        var csrfToken = OpaqueToken();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddSeconds(idleTimeoutSeconds);
        var absoluteExpiresAt = now.AddSeconds(absoluteTimeoutSeconds);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // The three projections are transactionally coupled, but they do not
        // need three client/server round trips. Keep transaction control in
        // the same command as the writes so the remote pooler sees one
        // remote PostgreSQL exchange for session issuance. The command is intentionally
        // self-contained so a pooler cannot split the transaction boundary or
        // make the supervisor restart path change the write semantics.
        await using (var sessionCommand = new NpgsqlCommand(
            """
            begin;

            insert into aevo_identity_users (id, email, display_name, status)
            values (@user_id, @email, @display_name, 'active')
            on conflict (id) do update set
              email = excluded.email,
              display_name = excluded.display_name,
              status = 'active',
              updated_at = now();

            -- Keep the application-facing identity projection in sync with
            -- the provider identity. Core owns the session boundary; the
            -- legacy public profile table remains a compatibility read model.
            insert into public.user_profiles (id, email, display_name, status)
            values (@user_id, @email, coalesce(@display_name, ''), 'ACTIVE')
            on conflict (id) do update set
              email = excluded.email,
              display_name = excluded.display_name,
              status = 'ACTIVE',
              updated_at = now();

            insert into aevo_app_sessions
              (id, user_id, app_code, organization_id, store_id, session_hash, csrf_token_hash, expires_at, idle_expires_at, absolute_expires_at, remember_me, last_seen_at)
            values
              (@id, @user_id, @app_code, @organization_id, @store_id, @session_hash, @csrf_token_hash, @expires_at, @idle_expires_at, @absolute_expires_at, @remember_me, now())
            ;

            commit;
            """, connection))
        {
            sessionCommand.Parameters.AddWithValue("id", sessionId);
            sessionCommand.Parameters.AddWithValue("user_id", userId);
            sessionCommand.Parameters.Add(new NpgsqlParameter("app_code", NpgsqlDbType.Text) { Value = appCode });
            sessionCommand.Parameters.Add(new NpgsqlParameter("email", NpgsqlDbType.Text) { Value = email.Trim().ToLowerInvariant() });
            sessionCommand.Parameters.Add(new NpgsqlParameter("display_name", NpgsqlDbType.Text) { Value = (object?)displayName ?? DBNull.Value });
            sessionCommand.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
            sessionCommand.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
            sessionCommand.Parameters.AddWithValue("session_hash", SessionHash(sessionToken));
            sessionCommand.Parameters.AddWithValue("csrf_token_hash", SessionHash(csrfToken));
            sessionCommand.Parameters.AddWithValue("expires_at", expiresAt);
            sessionCommand.Parameters.AddWithValue("idle_expires_at", expiresAt);
            sessionCommand.Parameters.AddWithValue("absolute_expires_at", absoluteExpiresAt);
            sessionCommand.Parameters.AddWithValue("remember_me", rememberMe);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var issued = new IssuedCoreSession(sessionId, userId, appCode, email.Trim().ToLowerInvariant(), displayName, sessionToken, csrfToken, expiresAt, rememberMe, absoluteExpiresAt);
        if (appCode == "HUB") await PrimeHubSessionBootstrapAsync(sessionToken, cancellationToken);
        return issued;
    }

    private async Task PrimeHubSessionBootstrapAsync(string sessionToken, CancellationToken cancellationToken)
    {
        if (memoryCache is null) return;
        try
        {
            var bootstrap = await ResolveHubSessionBootstrapAsync(sessionToken, cancellationToken);
            if (bootstrap?.Principal is null) return;

            // The Hub shell requests the organization dashboard immediately
            // after login. Prime the same tenant-scoped read model only after
            // the authoritative session and membership check has succeeded.
            await GetHubDashboardProjectionAsync(
                bootstrap.Principal.OrganizationId,
                null,
                environment,
                cancellationToken);
        }
        catch (CoreDatabaseException)
        {
            // Session issuance must remain available if the optional read-model
            // prime is unavailable. The next authenticated request retries the
            // authoritative bootstrap read.
        }
    }

    public Task<IssuedCoreSession?> RefreshSessionAsync(
        string sessionToken,
        string appCode,
        int idleTimeoutSeconds,
        int absoluteTimeoutSeconds,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "session.refresh",
            () => RefreshSessionFromDatabaseAsync(
                sessionToken,
                appCode,
                idleTimeoutSeconds,
                absoluteTimeoutSeconds,
                cancellationToken));

    private async Task<IssuedCoreSession?> RefreshSessionFromDatabaseAsync(
        string sessionToken,
        string appCode,
        int idleTimeoutSeconds,
        int absoluteTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!ApplicationCodes.All.Contains(appCode)) throw new CoreDatabaseException("Unknown application code.");
        ValidateSessionTimeouts(idleTimeoutSeconds, absoluteTimeoutSeconds);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid sessionId;
        Guid userId;
        string email;
        string? displayName;
        Guid? organizationId;
        Guid? storeId;
        DateTimeOffset absoluteExpiresAt;
        bool rememberMe;

        await using (var lookup = new NpgsqlCommand(
            """
            select s.id, s.user_id, u.email, u.display_name, s.organization_id, s.store_id, s.absolute_expires_at, s.remember_me
            from aevo_app_sessions s
            join aevo_identity_users u on u.id = s.user_id and u.status = 'active'
            where s.session_hash = @session_hash
              and s.app_code = @app_code
              and s.revoked_at is null
              and s.expires_at > now()
              and s.idle_expires_at > now()
              and s.absolute_expires_at > now()
            for update
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("session_hash", SessionHash(sessionToken));
            lookup.Parameters.AddWithValue("app_code", appCode);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            sessionId = reader.GetGuid(0);
            userId = reader.GetGuid(1);
            email = reader.GetString(2);
            displayName = NullableString(reader, 3);
            organizationId = NullableGuid(reader, 4);
            storeId = NullableGuid(reader, 5);
            absoluteExpiresAt = reader.GetFieldValue<DateTimeOffset>(6);
            rememberMe = reader.GetBoolean(7);
        }

        var replacement = Guid.NewGuid();
        var replacementToken = OpaqueToken();
        var replacementCsrf = OpaqueToken();
        var now = DateTimeOffset.UtcNow;
        if (now >= absoluteExpiresAt) return null;
        var expiresAt = now.AddSeconds(idleTimeoutSeconds);
        if (expiresAt > absoluteExpiresAt) expiresAt = absoluteExpiresAt;

        await using (var revoke = new NpgsqlCommand(
            "update aevo_app_sessions set revoked_at = now() where id = @id and revoked_at is null",
            connection, transaction))
        {
            revoke.Parameters.AddWithValue("id", sessionId);
            if (await revoke.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var insert = new NpgsqlCommand(
            """
            insert into aevo_app_sessions
              (id, user_id, app_code, organization_id, store_id, session_hash, csrf_token_hash, expires_at, idle_expires_at, absolute_expires_at, remember_me, last_seen_at)
            values
              (@id, @user_id, @app_code, @organization_id, @store_id, @session_hash, @csrf_token_hash, @expires_at, @idle_expires_at, @absolute_expires_at, @remember_me, now())
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", replacement);
            insert.Parameters.AddWithValue("user_id", userId);
            insert.Parameters.AddWithValue("app_code", appCode);
            insert.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
            insert.Parameters.AddWithValue("session_hash", SessionHash(replacementToken));
            insert.Parameters.AddWithValue("csrf_token_hash", SessionHash(replacementCsrf));
            insert.Parameters.AddWithValue("expires_at", expiresAt);
            insert.Parameters.AddWithValue("idle_expires_at", expiresAt);
            insert.Parameters.AddWithValue("absolute_expires_at", absoluteExpiresAt);
            insert.Parameters.AddWithValue("remember_me", rememberMe);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        // A rotated token must not remain usable from a stale in-process
        // session cache if a cache implementation is supplied in the future.
        InvalidateSessionSnapshot(appCode, sessionToken);
        return new IssuedCoreSession(replacement, userId, appCode, email, displayName, replacementToken, replacementCsrf, expiresAt, rememberMe, absoluteExpiresAt);
    }

    public Task<DateTimeOffset?> TouchSessionAsync(Guid sessionId, int idleTimeoutSeconds, CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync("session.touch", () => TouchSessionCoreAsync(sessionId, idleTimeoutSeconds, 0, cancellationToken));

    public Task<DateTimeOffset?> TouchSessionIfDueAsync(
        Guid sessionId,
        int idleTimeoutSeconds,
        int touchIntervalSeconds,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "session.touch_if_due",
            () => TouchSessionCoreAsync(sessionId, idleTimeoutSeconds, Math.Clamp(touchIntervalSeconds, 1, 600), cancellationToken));

    private async Task<DateTimeOffset?> TouchSessionCoreAsync(
        Guid sessionId,
        int idleTimeoutSeconds,
        int touchIntervalSeconds,
        CancellationToken cancellationToken)
    {
        if (idleTimeoutSeconds is < 60 or > 60 * 60 * 24 * 30)
        {
            throw new CoreDatabaseException("The session idle timeout is outside the allowed range.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with current_session as (
              select id, expires_at, absolute_expires_at, last_seen_at
              from aevo_app_sessions
              where id = @id
                and revoked_at is null
                and expires_at > now()
                and idle_expires_at > now()
                and absolute_expires_at > now()
            ), touched_session as (
              update aevo_app_sessions s
              set expires_at = least(now() + (@idle_timeout_seconds * interval '1 second'), s.absolute_expires_at),
                  idle_expires_at = least(now() + (@idle_timeout_seconds * interval '1 second'), s.absolute_expires_at),
                  last_seen_at = now()
              from current_session c
              where s.id = c.id
                and (@touch_interval_seconds = 0
                     or c.last_seen_at is null
                     or c.last_seen_at <= now() - (@touch_interval_seconds * interval '1 second'))
              returning s.expires_at
            )
            select expires_at from touched_session
            union all
            select expires_at from current_session
            where not exists (select 1 from touched_session)
            limit 1
            """, connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("idle_timeout_seconds", idleTimeoutSeconds);
        command.Parameters.AddWithValue("touch_interval_seconds", touchIntervalSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }

    public Task<IReadOnlyList<ApplicationRegistryRecord>> ListApplicationsAsync(CancellationToken cancellationToken)
    {
        return memoryCache is null
            ? ListApplicationsFromDatabaseAsync(cancellationToken)
            : memoryCache.GetOrCreateAsync(
                AevoCacheKeys.ApplicationRegistry,
                TimeSpan.FromSeconds(30),
                ListApplicationsFromDatabaseAsync,
                cancellationToken);
    }

    private Task<IReadOnlyList<ApplicationRegistryRecord>> ListApplicationsFromDatabaseAsync(CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.applications",
            () => ListApplicationsFromDatabaseCoreAsync(cancellationToken));

    private async Task<IReadOnlyList<ApplicationRegistryRecord>> ListApplicationsFromDatabaseCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select code, name, kind, status, manifest_version, owner_repository,
                   contract_version, audience, install_scope, store_scoped,
                   launch_path, lifecycle_status, capabilities, config_schema_refs,
                   created_at, updated_at
            from aevo_application_registry
            order by code
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ApplicationRegistryRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApplicationRegistryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetBoolean(9),
                reader.GetString(10),
                reader.GetString(11),
                ReadJsonStringArray(reader, 12),
                ReadJsonStringArray(reader, 13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetFieldValue<DateTimeOffset>(15)));
        }
        return result;
    }

    public Task<PlatformOverviewRecord> GetPlatformOverviewAsync(
        string operatingMode,
        bool unlimited,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.overview",
            () => GetPlatformOverviewFromDatabaseAsync(operatingMode, unlimited, cancellationToken));

    private async Task<PlatformOverviewRecord> GetPlatformOverviewFromDatabaseAsync(
        string operatingMode,
        bool unlimited,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              (select count(*) from public.organizations where status = 'ACTIVE'),
              (select count(*) from public.stores where status = 'ACTIVE'),
              (select count(*) from aevo_identity_users where status = 'active'),
              (select count(*) from public.devices where status = 'ACTIVE'),
              (select count(*) from aevo_organization_subscriptions)
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CoreDatabaseException("The platform overview query returned no row.");
        }

        return new PlatformOverviewRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            string.IsNullOrWhiteSpace(operatingMode) ? "development" : operatingMode.Trim().ToLowerInvariant(),
            unlimited);
    }

    public Task<IReadOnlyList<ApplicationConnectionRecord>> ListConnectionsAsync(string environment, CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return ListConnectionsFromDatabaseAsync(environment, cancellationToken);
        }

        return memoryCache.GetOrCreateAsync(
            AevoCacheKeys.ApplicationConnections(environment),
            TimeSpan.FromSeconds(15),
            ct => ListConnectionsFromDatabaseAsync(environment, ct),
            cancellationToken);
    }

    private Task<IReadOnlyList<ApplicationConnectionRecord>> ListConnectionsFromDatabaseAsync(
        string environment,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "application.connections.list",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """
                    select r.code, r.name, coalesce(c.status, 'not_configured'), c.base_url,
                           c.checked_at, c.latency_ms, c.last_error_code, c.metadata
                    from aevo_application_registry r
                    left join aevo_application_connections c
                      on c.app_code = r.code and c.environment = @environment
                    order by r.code
                    """, connection);
                command.Parameters.AddWithValue("environment", environment);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var result = new List<ApplicationConnectionRecord>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new ApplicationConnectionRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        NullableString(reader, 3),
                        NullableDateTimeOffset(reader, 4),
                        NullableInt32(reader, 5),
                        NullableString(reader, 6),
                        JsonValue(reader, 7)));
                }
                return (IReadOnlyList<ApplicationConnectionRecord>)result;
            });

    public async Task<IReadOnlyList<MigrationAuthorityRecord>> ListMigrationAuthorityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select schema_name, table_name, semantic_domain, historical_migration_repos,
                   current_owner, target_owner, runtime_writers, readers,
                   current_write_mode, migration_phase, transition_state,
                   last_reconciled_at, updated_at, delete_after
            from aevo_control_plane_migration_ledger
            order by schema_name, table_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MigrationAuthorityRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MigrationAuthorityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<string[]>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                NullableDateTimeOffset(reader, 11),
                reader.GetFieldValue<DateTimeOffset>(12),
                NullableString(reader, 13)));
        }
        return result;
    }

    public async Task<IReadOnlyList<AdminAuditRecord>> ListAuditLogsAsync(int limit, string? applicationCode, CancellationToken cancellationToken)
    {
        var page = await ListAuditLogsPageAsync(limit, applicationCode, null, cancellationToken);
        return page.Items;
    }

    public Task<AdminAuditPage> ListAuditLogsPageAsync(
        int limit,
        string? applicationCode,
        AuditCursorPosition? after,
        CancellationToken cancellationToken)
        => ListAuditLogsPageAsync(limit, applicationCode, after, null, cancellationToken);

    public Task<AdminAuditPage> ListAuditLogsPageAsync(
        int limit,
        string? applicationCode,
        AuditCursorPosition? after,
        string? query,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.audit_logs",
            () => ListAuditLogsPageFromDatabaseAsync(limit, applicationCode, after, query, cancellationToken));

    private async Task<AdminAuditPage> ListAuditLogsPageFromDatabaseAsync(
        int limit,
        string? applicationCode,
        AuditCursorPosition? after,
        string? query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(limit, 1, 50);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select a.id, a.actor_id, u.email, r.role_code, a.app_code, a.action,
                   a.target_type, a.target_id, a.reason, a.before_state, a.after_state,
                   a.request_id, a.created_at
            from aevo_audit_logs a
            left join aevo_identity_users u on u.id = a.actor_id
            left join aevo_platform_roles r on r.user_id = a.actor_id and r.status = 'active'
              where (@application_code is null or a.app_code = @application_code)
              and (@query is null or lower(a.action) like '%' || lower(@query) || '%' or lower(a.target_type) like '%' || lower(@query) || '%' or lower(coalesce(a.target_id, '')) like '%' || lower(@query) || '%' or lower(coalesce(a.reason, '')) like '%' || lower(@query) || '%' or lower(coalesce(u.email, '')) like '%' || lower(@query) || '%')
              and (
                @cursor_created_at is null
                or a.created_at < @cursor_created_at
                or (a.created_at = @cursor_created_at and a.id > @cursor_id)
              )
            order by a.created_at desc, a.id asc
            limit @limit_plus_one
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("application_code", NpgsqlDbType.Text) { Value = (object?)applicationCode?.Trim().ToUpperInvariant() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)query?.Trim() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_created_at", NpgsqlDbType.TimestampTz) { Value = (object?)after?.CreatedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursor_id", NpgsqlDbType.Uuid) { Value = (object?)after?.Id ?? DBNull.Value });
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AdminAuditRecord>(pageSize + 1);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminAuditRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                JsonValue(reader, 9),
                JsonValue(reader, 10),
                NullableString(reader, 11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }

        var hasMore = result.Count > pageSize;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return new AdminAuditPage(result, hasMore);
    }

    public async Task<IReadOnlyList<AdminPlaceRegistryRecord>> ListAdminPlaceRegistryAsync(
        int limit,
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var page = await ListAdminPlaceRegistryPageAsync(limit, 0, query, status, cancellationToken);
        return page.Items;
    }

    private static string PlaceRegistryOrderBy(string? sort, string? direction)
    {
        var column = sort switch
        {
            "place" => "r.name",
            "status" => "r.status",
            "projection" => "p.projection_status",
            "revision" => "r.revision",
            "generated" => "p.generated_at",
            _ => "r.updated_at"
        };
        var orderDirection = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        return $"{column} {orderDirection}, r.place_id asc";
    }

    public async Task<AdminDirectoryPage<AdminPlaceRegistryRecord>> ListAdminPlaceRegistryPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        CancellationToken cancellationToken)
        => await ListAdminPlaceRegistryPageAsync(limit, offset, query, status, "generated", "desc", cancellationToken);

    public async Task<AdminDirectoryPage<AdminPlaceRegistryRecord>> ListAdminPlaceRegistryPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetPlaceAccessAsync(connection, transaction, cancellationToken);
        var orderBy = PlaceRegistryOrderBy(sort, direction);
        await using var command = new NpgsqlCommand(
            $"""
            select r.place_id, r.slug, r.name, r.status,
                   coalesce(r.category->>'id', 'uncategorized') as category_id,
                   r.display_latitude, r.display_longitude, r.revision,
                   r.source_revision, p.projection_status, p.freshness_state,
                   p.projection_version, p.generated_at
            from aevo_place_registry r
            left join aevo_place_public_projections p on p.place_id = r.place_id
            where (@query is null or lower(r.name) like '%' || lower(@query) || '%' or lower(r.slug) like '%' || lower(@query) || '%')
              and (@status is null or r.status = @status)
            order by {orderBy}
            limit @limit_plus_one offset @offset
            """,
            connection,
            transaction);
        var pageSize = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        command.Parameters.AddWithValue("offset", Math.Max(offset, 0));
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)query ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });

        var result = new List<AdminPlaceRegistryRecord>();
        try
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new AdminPlaceRegistryRecord(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        reader.IsDBNull(6) ? null : reader.GetDouble(6),
                        reader.GetInt64(7),
                        reader.GetString(8),
                        NullableString(reader, 9),
                        NullableString(reader, 10),
                        NullableString(reader, 11),
                        NullableDateTimeOffset(reader, 12)));
                }
            }
            await transaction.CommitAsync(cancellationToken);
            var hasMore = result.Count > pageSize;
            if (hasMore) result.RemoveAt(result.Count - 1);
            return new AdminDirectoryPage<AdminPlaceRegistryRecord>(result, hasMore);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place registry is not available.", error);
        }
    }

    public async Task<IReadOnlyList<string>> ResolvePermissionsAsync(
        Guid userId,
        string appCode,
        Guid? organizationId,
        Guid? storeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select permissions
            from aevo_application_assignments
            where user_id = @user_id
              and app_code = @app_code
              and status = 'active'
              and (organization_id is null or organization_id = @organization_id)
              and (store_id is null or store_id = @store_id)
              and (expires_at is null or expires_at > now())
            order by organization_id nulls first, store_id nulls first
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("app_code", appCode);
        command.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)organizationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)storeId ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreach (var permission in reader.GetFieldValue<string[]>(0)) permissions.Add(permission);
        }
        return permissions.ToArray();
    }

    public async Task RevokeSessionAsync(string sessionToken, string? appCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update aevo_app_sessions set revoked_at = now() where session_hash = @session_hash and (@app_code is null or app_code = @app_code) and revoked_at is null",
            connection);
        var tokenHash = SessionHash(sessionToken);
        command.Parameters.AddWithValue("session_hash", tokenHash);
        command.Parameters.Add(new NpgsqlParameter("app_code", NpgsqlDbType.Text) { Value = (object?)appCode ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);

        InvalidateSessionSnapshot(appCode, sessionToken);
    }

    public async Task RevokeSessionsForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update aevo_app_sessions set revoked_at = now() where user_id = @user_id and revoked_at is null returning session_hash, app_code",
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        var revoked = new List<(string SessionHash, string AppCode)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                revoked.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (memoryCache is not null)
        {
            foreach (var session in revoked)
            {
                memoryCache.Remove(AevoCacheKeys.Session(session.AppCode, session.SessionHash));
            }
        }
        InvalidateAuthorizationCaches();
    }

    public async Task<string> CreatePasswordRecoveryGrantAsync(
        Guid userId,
        string email,
        string providerAccessToken,
        DateTimeOffset expiresAt,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (recoveryEncryptionKey is null)
        {
            throw new CoreDatabaseException("Password recovery encryption is not configured.");
        }
        if (string.IsNullOrWhiteSpace(providerAccessToken))
        {
            throw new CoreDatabaseException("The identity provider returned no recovery credential.");
        }
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new CoreDatabaseException("The password recovery grant has expired.");
        }

        var grantToken = OpaqueToken();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var identity = new NpgsqlCommand(
            """
            insert into aevo_identity_users (id, email, status)
            values (@id, @email, 'active')
            on conflict (id) do update set
              email = excluded.email,
              status = 'active',
              updated_at = now()
            """, connection, transaction))
        {
            identity.Parameters.AddWithValue("id", userId);
            identity.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
            await identity.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_password_recovery_grants
              (grant_hash, user_id, email, provider_access_token_ciphertext, expires_at, request_id)
            values
              (@grant_hash, @user_id, @email, @provider_access_token_ciphertext, @expires_at, @request_id)
            """, connection, transaction);
        command.Parameters.AddWithValue("grant_hash", SessionHash(grantToken));
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("provider_access_token_ciphertext", EncryptRecoverySecret(providerAccessToken));
        command.Parameters.AddWithValue("expires_at", expiresAt);
        command.Parameters.Add(new NpgsqlParameter("request_id", NpgsqlDbType.Text) { Value = (object?)requestId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return grantToken;
    }

    public async Task<PasswordRecoveryGrant?> ConsumePasswordRecoveryGrantAsync(
        string grantToken,
        CancellationToken cancellationToken)
    {
        if (recoveryEncryptionKey is null || string.IsNullOrWhiteSpace(grantToken)) return null;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid userId;
        string email;
        string ciphertext;
        await using (var lookup = new NpgsqlCommand(
            """
            select user_id, email, provider_access_token_ciphertext
            from aevo_password_recovery_grants
            where grant_hash = @grant_hash
              and consumed_at is null
              and expires_at > now()
            for update
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("grant_hash", SessionHash(grantToken));
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            userId = reader.GetGuid(0);
            email = reader.GetString(1);
            ciphertext = reader.GetString(2);
        }

        await using (var consume = new NpgsqlCommand(
            "update aevo_password_recovery_grants set consumed_at = now() where grant_hash = @grant_hash and consumed_at is null",
            connection,
            transaction))
        {
            consume.Parameters.AddWithValue("grant_hash", SessionHash(grantToken));
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PasswordRecoveryGrant(userId, email, DecryptRecoverySecret(ciphertext));
    }

    public async Task UpdateApplicationStatusAsync(
        Guid actorId,
        string requestId,
        string reason,
        string appCode,
        string status,
        CancellationToken cancellationToken)
    {
        if (status is not ("ACTIVE" or "DISABLED")) throw new CoreDatabaseException("Application status must be ACTIVE or DISABLED.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadJsonAsync(connection, transaction, "select jsonb_build_object('status', status) from aevo_application_registry where code = @app_code", cancellationToken, ("app_code", appCode));
        if (before is null) throw new CoreDatabaseException("Application is not registered.");

        await using (var command = new NpgsqlCommand(
            "update aevo_application_registry set status = @status, updated_at = now() where code = @app_code",
            connection, transaction))
        {
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("app_code", appCode);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new CoreDatabaseException("Application is not registered.");
        }

        var after = JsonSerializer.SerializeToElement(new { status });
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "APPLICATION_STATUS_UPDATED", "application", appCode, reason, before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        InvalidateApplicationRegistryCache(appCode);
        InvalidateAuthorizationCaches();
    }

    public async Task RecordConnectionProbeAsync(ConnectionProbeUpdate probe, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_application_connections
              (app_code, environment, status, base_url, health_path, checked_at, latency_ms, last_error_code, metadata, updated_at)
            values
              (@app_code, @environment, @status, @base_url, @health_path, @checked_at, @latency_ms, @last_error_code, @metadata, now())
            on conflict (app_code, environment) do update set
              status = excluded.status,
              base_url = excluded.base_url,
              health_path = excluded.health_path,
              checked_at = excluded.checked_at,
              latency_ms = excluded.latency_ms,
              last_error_code = excluded.last_error_code,
              metadata = aevo_application_connections.metadata || excluded.metadata,
              updated_at = now()
            """, connection);
        command.Parameters.AddWithValue("app_code", probe.AppCode);
        command.Parameters.AddWithValue("environment", probe.Environment);
        command.Parameters.AddWithValue("status", probe.Status);
        command.Parameters.AddWithValue("base_url", (object?)probe.BaseUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("health_path", probe.HealthPath);
        command.Parameters.Add(new NpgsqlParameter("checked_at", NpgsqlDbType.TimestampTz) { Value = (object?)probe.CheckedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("latency_ms", NpgsqlDbType.Integer) { Value = (object?)probe.LatencyMs ?? DBNull.Value });
        command.Parameters.AddWithValue("last_error_code", (object?)probe.LastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, probe.Metadata?.GetRawText() ?? "{}");
        await command.ExecuteNonQueryAsync(cancellationToken);
        InvalidateApplicationRegistryCache(probe.AppCode, probe.Environment);
    }

    public async Task<GoSettingsRecord?> GetGoSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select config, updated_at, updated_by from aevo_go_settings where setting_key = 'default'",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var settings = JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(1);
        var updatedBy = NullableGuid(reader, 2);
        await reader.CloseAsync();

        await using var flagCommand = new NpgsqlCommand(
            "select flag_key, enabled, rollout_percent, config, updated_by, updated_at from aevo_go_feature_flags order by flag_key",
            connection);
        await using var flagReader = await flagCommand.ExecuteReaderAsync(cancellationToken);
        var flags = new List<GoFeatureFlagRecord>();
        while (await flagReader.ReadAsync(cancellationToken))
        {
            flags.Add(new GoFeatureFlagRecord(
                flagReader.GetString(0),
                flagReader.GetBoolean(1),
                flagReader.GetInt32(2),
                JsonDocument.Parse(flagReader.GetString(3)).RootElement.Clone(),
                NullableGuid(flagReader, 4),
                flagReader.GetFieldValue<DateTimeOffset>(5)));
        }

        return new GoSettingsRecord(settings, updatedAt, updatedBy, flags);
    }

    public async Task<GoOverviewRecord> GetGoOverviewAsync(int requestedDays, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(requestedDays, 1, 90);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              count(*) as activity_events,
              count(*) filter (where event_name = 'feed_impression') as feed_impressions,
              count(*) filter (where event_name = 'feed_interaction') as feed_interactions,
              count(*) filter (where event_name = 'journey_started') as total_journeys,
              count(*) filter (where event_name = 'journey_completed') as completed_journeys,
              count(*) filter (where event_name = 'booking_created') as bookings_created,
              count(*) filter (where event_name = 'booking_completed') as bookings_completed,
              count(*) filter (where event_name = 'booking_cancelled') as bookings_cancelled,
              count(*) filter (where event_name = 'order_created') as orders_created,
              count(*) filter (where event_name = 'order_completed') as orders_completed,
              count(*) filter (where event_name = 'rating_submitted') as ratings_count,
              coalesce(avg(case when event_name = 'rating_submitted'
                and payload->>'rating' ~ '^[0-9]+(\\.[0-9]+)?$'
                then (payload->>'rating')::numeric end), 0) as average_rating,
              count(*) filter (where event_name = 'report_opened') as open_reports,
              count(*) filter (where event_name = 'place_active') as active_places,
              count(*) filter (where event_name = 'store_public') as public_stores,
              count(*) filter (where event_name = 'trace_published') as published_traces
            from aevo_product_events
            where app_code = 'GO'
              and occurred_at >= now() - make_interval(days => @days)
            """, connection);
        command.Parameters.AddWithValue("days", days);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Aevo Go event aggregate returned no row.");

        var activityEvents = reader.GetInt64(0);
        var feedImpressions = reader.GetInt64(1);
        var feedInteractions = reader.GetInt64(2);
        var totalJourneys = reader.GetInt64(3);
        var completedJourneys = reader.GetInt64(4);
        var bookingsCreated = reader.GetInt64(5);
        var bookingsCompleted = reader.GetInt64(6);
        var bookingsCancelled = reader.GetInt64(7);
        var ordersCreated = reader.GetInt64(8);
        var ordersCompleted = reader.GetInt64(9);
        var ratingsCount = reader.GetInt64(10);
        var averageRating = reader.GetDecimal(11);
        var openReports = reader.GetInt64(12);
        var activePlaces = reader.GetInt64(13);
        var publicStores = reader.GetInt64(14);
        var publishedTraces = reader.GetInt64(15);
        await reader.CloseAsync();

        await using var dailyCommand = new NpgsqlCommand(
            """
            select (occurred_at at time zone 'UTC')::date as activity_date,
                   count(*) as events,
                   count(*) filter (where event_name = 'feed_impression') as impressions,
                   count(*) filter (where event_name = 'feed_interaction') as interactions
            from aevo_product_events
            where app_code = 'GO'
              and occurred_at >= now() - make_interval(days => @days)
            group by activity_date
            order by activity_date
            """, connection);
        dailyCommand.Parameters.AddWithValue("days", days);
        await using var dailyReader = await dailyCommand.ExecuteReaderAsync(cancellationToken);
        var daily = new List<GoDailyActivityRecord>();
        while (await dailyReader.ReadAsync(cancellationToken))
        {
            daily.Add(new GoDailyActivityRecord(
                DateOnly.FromDateTime(dailyReader.GetDateTime(0)),
                dailyReader.GetInt64(1),
                dailyReader.GetInt64(2),
                dailyReader.GetInt64(3)));
        }
        await dailyReader.CloseAsync();

        await using var flagsCommand = new NpgsqlCommand(
            "select count(*) filter (where enabled and rollout_percent > 0), count(*) from aevo_go_feature_flags",
            connection);
        await using var flagsReader = await flagsCommand.ExecuteReaderAsync(cancellationToken);
        var enabledFlags = 0;
        var flagCount = 0;
        if (await flagsReader.ReadAsync(cancellationToken))
        {
            enabledFlags = checked((int)flagsReader.GetInt64(0));
            flagCount = checked((int)flagsReader.GetInt64(1));
        }

        return new GoOverviewRecord(
            DateTimeOffset.UtcNow,
            days,
            activePlaces,
            publicStores,
            publishedTraces,
            totalJourneys,
            completedJourneys,
            bookingsCreated,
            bookingsCompleted,
            bookingsCancelled,
            ordersCreated,
            ordersCompleted,
            ratingsCount,
            averageRating,
            openReports,
            activityEvents,
            feedImpressions,
            feedInteractions,
            0,
            enabledFlags,
            flagCount,
            daily);
    }

    public async Task UpdateGoSettingsAsync(Guid actorId, string requestId, string reason, JsonElement settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadJsonAsync(connection, transaction, "select config from aevo_go_settings where setting_key = 'default'", cancellationToken);

        await using (var command = new NpgsqlCommand(
            "update aevo_go_settings set config = @config, updated_by = @updated_by, updated_at = now() where setting_key = 'default'",
            connection, transaction))
        {
            command.Parameters.AddWithValue("config", NpgsqlDbType.Jsonb, settings.GetRawText());
            command.Parameters.AddWithValue("updated_by", actorId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new CoreDatabaseException("Aevo Go settings row is not configured.");
        }

        await InsertAuditAsync(connection, transaction, actorId, "GO", "GO_SETTINGS_UPDATED", "aevo_go_settings", "default", reason, before, settings, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateGoFeatureFlagAsync(Guid actorId, string requestId, string reason, string flagKey, bool enabled, int rolloutPercent, CancellationToken cancellationToken)
    {
        if (rolloutPercent is < 0 or > 100) throw new CoreDatabaseException("Rollout percent must be between 0 and 100.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadJsonAsync(connection, transaction, "select jsonb_build_object('enabled', enabled, 'rolloutPercent', rollout_percent, 'config', config) from aevo_go_feature_flags where flag_key = @flag_key", cancellationToken, ("flag_key", flagKey));
        if (before is null) throw new CoreDatabaseException("Aevo Go feature flag is not configured.");

        await using (var command = new NpgsqlCommand(
            "update aevo_go_feature_flags set enabled = @enabled, rollout_percent = @rollout_percent, updated_by = @updated_by, updated_at = now() where flag_key = @flag_key",
            connection, transaction))
        {
            command.Parameters.AddWithValue("enabled", enabled);
            command.Parameters.AddWithValue("rollout_percent", rolloutPercent);
            command.Parameters.AddWithValue("updated_by", actorId);
            command.Parameters.AddWithValue("flag_key", flagKey);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new CoreDatabaseException("Aevo Go feature flag is not configured.");
        }

        var after = JsonSerializer.SerializeToElement(new { enabled, rolloutPercent });
        await InsertAuditAsync(connection, transaction, actorId, "GO", "GO_FEATURE_FLAG_UPDATED", "aevo_go_feature_flags", flagKey, reason, before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (dataSource is null)
        {
            throw new CoreDatabaseException(configurationError ?? "Core API database is not configured.");
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await dataSource.OpenConnectionAsync(cancellationToken);
            }
            finally
            {
                stopwatch.Stop();
                RequestPerformance.Current?.RecordDatabaseConnectionOpen(stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception error)
        {
            throw DatabaseUnavailable(error);
        }
    }

    private static async Task<JsonElement?> ReadJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : JsonDocument.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "{}").RootElement.Clone();
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        string appCode,
        string action,
        string targetType,
        string targetId,
        string reason,
        JsonElement? before,
        JsonElement after,
        string requestId,
        CancellationToken cancellationToken)
    {
        await TenantContextSql.ApplyAsync(connection, transaction, actorId, null, null, appCode, null, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_audit_logs
              (actor_id, app_code, action, target_type, target_id, reason, before_state, after_state, request_id)
            values
              (@actor_id, @app_code, @action, @target_type, @target_id, @reason, @before_state, @after_state, @request_id)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("app_code", appCode);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("target_type", targetType);
        command.Parameters.AddWithValue("target_id", targetId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("before_state", NpgsqlDbType.Jsonb, before?.GetRawText() ?? "{}");
        command.Parameters.AddWithValue("after_state", NpgsqlDbType.Jsonb, after.GetRawText());
        command.Parameters.AddWithValue("request_id", requestId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CoreDatabaseException DatabaseUnavailable(Exception error) =>
        new("Core API could not reach Cloud SQL.", error);

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "postgres", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AEVO_DATABASE_URL must be a PostgreSQL URI or an Npgsql connection string.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ? "postgres" : uri.AbsolutePath.Trim('/')
        };

        var userInfo = uri.UserInfo;
        if (!string.IsNullOrWhiteSpace(userInfo))
        {
            var separator = userInfo.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(separator >= 0 ? userInfo[..separator] : userInfo);
            if (separator >= 0) builder.Password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
        }

        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = Uri.UnescapeDataString(pair[0]).Trim().ToLowerInvariant();
            var queryValue = Uri.UnescapeDataString(pair[1]).Trim();
            if (key != "sslmode") continue;
            builder.SslMode = queryValue.ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("AEVO_DATABASE_URL contains an unsupported sslmode.")
            };
        }

        return builder.ConnectionString;
    }

    private static void ValidateSessionTimeouts(int idleTimeoutSeconds, int absoluteTimeoutSeconds)
    {
        if (idleTimeoutSeconds is < 60 or > 60 * 60 * 24 * 30)
        {
            throw new CoreDatabaseException("The session idle timeout is outside the allowed range.");
        }

        if (absoluteTimeoutSeconds is < 60 or > 60 * 60 * 24 * 30)
        {
            throw new CoreDatabaseException("The session absolute timeout is outside the allowed range.");
        }

        if (idleTimeoutSeconds > absoluteTimeoutSeconds)
        {
            throw new CoreDatabaseException("The session idle timeout cannot exceed the absolute timeout.");
        }
    }

    private string EncryptRecoverySecret(string value)
    {
        if (recoveryEncryptionKey is null) throw new CoreDatabaseException("Password recovery encryption is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(recoveryEncryptionKey, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return $"v1.{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
    }

    private string DecryptRecoverySecret(string value)
    {
        if (recoveryEncryptionKey is null) throw new CoreDatabaseException("Password recovery encryption is not configured.");
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "v1") throw new CoreDatabaseException("The password recovery grant is invalid.");
        try
        {
            var nonce = Base64UrlBytes(parts[1]);
            var ciphertext = Base64UrlBytes(parts[2]);
            var tag = Base64UrlBytes(parts[3]);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(recoveryEncryptionKey, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new CoreDatabaseException("The password recovery grant is invalid.", error);
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/') + "==="[..((4 - value.Length % 4) % 4)];
        return Convert.FromBase64String(padded);
    }

    public static string SessionHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string OpaqueToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool VerifyCsrf(CoreSession session, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(session.CsrfTokenHash)) return false;
        var expected = Encoding.UTF8.GetBytes(session.CsrfTokenHash);
        var actual = Encoding.UTF8.GetBytes(SessionHash(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableDateTimeOffset(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private static int? NullableInt32(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static int ReadBoundedInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private static JsonElement? JsonValue(NpgsqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : JsonDocument.Parse(reader.GetString(ordinal)).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (dataSource is not null) await dataSource.DisposeAsync();
    }
}
