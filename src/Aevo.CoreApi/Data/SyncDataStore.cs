using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Runtime;

namespace Aevo.CoreApi.Data;

public sealed record SyncManifestRecord(
    string ETag,
    DateTimeOffset GeneratedAt,
    string Application,
    Guid UserId,
    Guid? OrganizationId,
    Guid? StoreId,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyDictionary<string, SyncDeltaDescriptor> Deltas);

public sealed record SyncDeltaDescriptor(
    string Strategy,
    int PageSize,
    string Endpoint,
    string CursorParameter);

public sealed partial class CoreDataStore
{
    /// <summary>
    /// Returns cheap change markers for private client data. The manifest is
    /// never an authorization decision: protected resources still perform
    /// their normal server-side authorization. Its ETag is derived from
    /// source-of-truth update timestamps plus the validated app/session scope.
    /// </summary>
    public Task<SyncManifestRecord> GetSyncManifestAsync(
        CoreSession session,
        string application,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "sync.manifest",
            () => GetSyncManifestFromDatabaseAsync(session, application, cancellationToken));

    private async Task<SyncManifestRecord> GetSyncManifestFromDatabaseAsync(
        CoreSession session,
        string application,
        CancellationToken cancellationToken)
    {
        var normalizedApplication = application.Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            session.UserId,
            session.OrganizationId,
            session.StoreId,
            normalizedApplication,
            session.PlatformRole,
            cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select
              coalesce((
                select max(aa.updated_at)
                from aevo_application_assignments aa
                where aa.user_id = @user_id
                  and (@organization_id is null or aa.organization_id is null or aa.organization_id = @organization_id)
              ), 'epoch'::timestamptz),
              coalesce((
                select max(m.updated_at)
                from public.memberships m
                where m.user_id = @user_id
                  and (@organization_id is null or m.organization_id = @organization_id)
              ), 'epoch'::timestamptz),
              coalesce((
                select max(binding.updated_at)
                from aevo_store_application_bindings binding
                where (@organization_id is null or binding.organization_id = @organization_id)
                  and (@store_id is null or binding.store_id = @store_id)
              ), 'epoch'::timestamptz),
              coalesce((
                select max(entitlement.updated_at)
                from aevo_organization_entitlements entitlement
                where (@organization_id is null or entitlement.organization_id = @organization_id)
              ), 'epoch'::timestamptz),
              coalesce((
                select max(platform_role.updated_at)
                from aevo_platform_roles platform_role
                where platform_role.user_id = @user_id
              ), 'epoch'::timestamptz),
              coalesce((
                select max(identity_user.updated_at)
                from aevo_identity_users identity_user
                where identity_user.id = @user_id
              ), 'epoch'::timestamptz),
              coalesce((select max(registry.updated_at) from aevo_application_registry registry), 'epoch'::timestamptz)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", session.UserId);
        command.Parameters.Add(new NpgsqlParameter("organization_id", NpgsqlDbType.Uuid) { Value = (object?)session.OrganizationId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)session.StoreId ?? DBNull.Value });
        DateTimeOffset assignmentVersion;
        DateTimeOffset membershipVersion;
        DateTimeOffset bindingVersion;
        DateTimeOffset entitlementVersion;
        DateTimeOffset roleVersion;
        DateTimeOffset identityVersion;
        DateTimeOffset applicationVersion;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("The sync manifest could not be generated.");
            assignmentVersion = reader.GetFieldValue<DateTimeOffset>(0);
            membershipVersion = reader.GetFieldValue<DateTimeOffset>(1);
            bindingVersion = reader.GetFieldValue<DateTimeOffset>(2);
            entitlementVersion = reader.GetFieldValue<DateTimeOffset>(3);
            roleVersion = reader.GetFieldValue<DateTimeOffset>(4);
            identityVersion = reader.GetFieldValue<DateTimeOffset>(5);
            applicationVersion = reader.GetFieldValue<DateTimeOffset>(6);
        }
        await transaction.CommitAsync(cancellationToken);
        var securityVersion = Max(Max(Max(assignmentVersion, membershipVersion), bindingVersion), roleVersion);
        var scopeVersion = Max(Max(securityVersion, entitlementVersion), identityVersion);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["access"] = scopeVersion.ToString("O"),
            ["assignments"] = assignmentVersion.ToString("O"),
            ["memberships"] = membershipVersion.ToString("O"),
            ["storeApplications"] = bindingVersion.ToString("O"),
            ["entitlements"] = entitlementVersion.ToString("O"),
            ["roles"] = roleVersion.ToString("O"),
            ["identity"] = identityVersion.ToString("O"),
            ["applications"] = applicationVersion.ToString("O")
        };
        var deltas = new Dictionary<string, SyncDeltaDescriptor>(StringComparer.Ordinal);
        if (normalizedApplication == "ADMIN")
        {
            // The audit endpoint already uses a signed, filter-bound keyset
            // cursor. Advertise that contract so clients do not attempt to
            // synchronize a large immutable stream with offset pagination.
            deltas["auditLogs"] = new SyncDeltaDescriptor(
                "signed-keyset",
                100,
                "/api/v1/admin/audit-logs",
                "cursor");
        }
        var fingerprint = string.Join(
            "|",
            session.Id,
            session.UserId,
            normalizedApplication,
            session.OrganizationId,
            session.StoreId,
            string.Join(";", resources.Select(pair => $"{pair.Key}={pair.Value}")));
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant()}\"";
        return new SyncManifestRecord(
            etag,
            DateTimeOffset.UtcNow,
            normalizedApplication,
            session.UserId,
            session.OrganizationId,
            session.StoreId,
            resources,
            deltas);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
        => first >= second ? first : second;
}
