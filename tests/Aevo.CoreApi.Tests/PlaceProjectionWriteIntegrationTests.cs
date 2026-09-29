using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceProjectionWriteIntegrationTests
{
    [Fact]
    public async Task AdminMutationCanCreateEditFallbackAndSoftDeleteAnIsolatedFixture()
    {
        if (!DatabaseConfigured() || !MutationIntegrationEnabled()) return;

        await using var connection = await OpenConnectionAsync();
        var actor = await ReadPrivilegedActorAsync(connection);
        if (actor is null) return;
        var actorIdentity = actor;

        var placeId = Guid.NewGuid();
        var marker = $"place-write-{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_DATABASE_URL"] = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")
            })
            .Build();
        await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
        Guid? replayRunId = null;

        try
        {
        var create = await database.UpsertAdminPlaceAsync(
            actorIdentity.UserId,
            actorIdentity.RoleCode,
            $"{marker}-create",
            Request(placeId, marker, "Created mutation fixture", "visible"),
            CancellationToken.None);
        Assert.Equal(1, create.Revision);
        Assert.Equal("created", create.Operation);
        Assert.True(create.ProjectionPublished);
        Assert.Equal("active", (await ReadPlaceStateAsync(connection, placeId)).ProjectionStatus);

        var adminDetail = await database.ReadAdminPlaceAsync(
            actorIdentity.UserId,
            actorIdentity.RoleCode,
            placeId,
            CancellationToken.None);
        Assert.NotNull(adminDetail);
        Assert.Equal(1, adminDetail!.Revision);
        Assert.Equal("Created mutation fixture", adminDetail.Summary.Name);
        Assert.Equal("derived", adminDetail.SourceKind);

        var createdProjection = await database.ReadPlaceProjectionDetailAsync(placeId, CancellationToken.None);
        Assert.NotNull(createdProjection);
        Assert.Equal("Created mutation fixture", createdProjection!.Payload.GetProperty("name").GetString());

        var publicRead = new PlacePublicReadService(database);
        var publicMap = await publicRead.GetMapOverlayAsync(
            new PlaceMapOverlayRequestContract(
                new PlaceBoundsContract(100, 13, 101, 14),
                12,
                "Created mutation fixture",
                null,
                3),
            $"{marker}-map",
            CancellationToken.None);
        Assert.Single(publicMap.Features);
        Assert.Equal(placeId.ToString("D"), publicMap.Features[0].Properties.PlaceId);

        var publicSearch = await publicRead.SearchAsync(
            new PlaceSearchRequestContract("Created mutation fixture", Limit: 3),
            $"{marker}-search",
            CancellationToken.None);
        Assert.Single(publicSearch.Data);
        Assert.Equal(placeId.ToString("D"), publicSearch.Data[0].Id);

        var savedMap = await publicRead.GetMapOverlayAsync(
            new PlaceMapOverlayRequestContract(
                new PlaceBoundsContract(100, 13, 101, 14),
                12,
                "Created mutation fixture",
                null,
                3,
                true),
            $"{marker}-saved-map",
            CancellationToken.None,
            new HashSet<Guid> { placeId });
        Assert.Single(savedMap.Features);
        Assert.Equal(placeId.ToString("D"), savedMap.Features[0].Properties.PlaceId);

        var savedSearch = await publicRead.SearchAsync(
            new PlaceSearchRequestContract("Created mutation fixture", SavedOnly: true, Limit: 3),
            $"{marker}-saved-search",
            CancellationToken.None,
            new HashSet<Guid> { placeId },
            "test-principal-binding");
        Assert.Single(savedSearch.Data);
        Assert.Equal(placeId.ToString("D"), savedSearch.Data[0].Id);

        var nonMatchingSavedSearch = await publicRead.SearchAsync(
            new PlaceSearchRequestContract("Created mutation fixture", SavedOnly: true, Limit: 3),
            $"{marker}-saved-search-empty",
            CancellationToken.None,
            new HashSet<Guid> { Guid.NewGuid() },
            "test-principal-binding");
        Assert.Empty(nonMatchingSavedSearch.Data);

        var publicNearby = await publicRead.NearbyAsync(
            new PlaceNearbyRequestContract(new PlaceGeoPointContract(100.501, 13.701), 1_000, Limit: 3),
            $"{marker}-nearby",
            CancellationToken.None);
        Assert.Single(publicNearby.Data);
        Assert.Equal(placeId.ToString("D"), publicNearby.Data[0].Id);

        var publicDetail = await publicRead.GetDetailAsync(
            placeId,
            $"{marker}-detail",
            CancellationToken.None);
        Assert.Equal(placeId.ToString("D"), publicDetail.Place.Id);
        Assert.Equal("Created mutation fixture", publicDetail.Place.Name);
        Assert.Empty(publicDetail.Place.Children);
        Assert.Empty(publicDetail.Place.BusinessLinks);
        Assert.Empty(publicDetail.Place.LegacyReferences);

        replayRunId = Guid.NewGuid();
        var replayObservedAt = DateTimeOffset.UtcNow;
        var replay = await database.ApplyPlaceProjectionReplayAsync(
            $"{marker}-replay",
            new PlaceProjectionReplayRequestContract(
                replayRunId.Value,
                actorIdentity.UserId,
                "places-public-v1",
                $"{marker}-create",
                replayObservedAt,
                300,
                new[] { new PlaceProjectionReplayFixtureContract("created-fixture", adminDetail.Summary) },
                "test approved projection replay"),
            CancellationToken.None);
        Assert.True(replay.Success);
        Assert.Equal("completed", replay.Status);
        Assert.Equal(1, replay.PublishedCount);
        Assert.False(replay.AlreadyApplied);
        Assert.NotEmpty(replay.ReplayFingerprint);

        var replayRetry = await database.ApplyPlaceProjectionReplayAsync(
            $"{marker}-replay-retry",
            new PlaceProjectionReplayRequestContract(
                replayRunId.Value,
                actorIdentity.UserId,
                "places-public-v1",
                $"{marker}-create",
                replayObservedAt,
                300,
                new[] { new PlaceProjectionReplayFixtureContract("created-fixture", adminDetail.Summary) },
                "test approved projection replay retry"),
            CancellationToken.None);
        Assert.True(replayRetry.Success);
        Assert.Equal("already_applied", replayRetry.Status);
        Assert.True(replayRetry.AlreadyApplied);
        Assert.Equal(replay.ReplayFingerprint, replayRetry.ReplayFingerprint);
        Assert.Equal(1, await CountCompletedProjectionRunsAsync(connection, replayRunId.Value));
        Assert.Equal(1, await CountProjectionReplayAuditRowsAsync(connection, replayRunId.Value));

        var update = await database.UpsertAdminPlaceAsync(
            actorIdentity.UserId,
            actorIdentity.RoleCode,
            $"{marker}-update",
            Request(placeId, marker, "Edited mutation fixture", "limited", expectedRevision: 1, sourceRevisionSuffix: "edit-1"),
            CancellationToken.None);
        Assert.Equal(2, update.Revision);
        Assert.Equal("updated", update.Operation);
        Assert.Equal("limited", update.Status);
        Assert.Equal("active", (await ReadPlaceStateAsync(connection, placeId)).ProjectionStatus);

        var updatedProjection = await database.ReadPlaceProjectionDetailAsync(placeId, CancellationToken.None);
        Assert.NotNull(updatedProjection);
        Assert.Equal("Edited mutation fixture", updatedProjection!.Payload.GetProperty("name").GetString());

        await MarkProjectionFailedWithLastKnownValidAsync(connection, placeId);
        var fallbackProjection = await database.ReadPlaceProjectionDetailAsync(placeId, CancellationToken.None);
        Assert.NotNull(fallbackProjection);
        Assert.Equal("stale", fallbackProjection!.FreshnessState);
        Assert.Equal("Edited mutation fixture", fallbackProjection.Payload.GetProperty("name").GetString());

        var rebuilt = await database.UpsertAdminPlaceAsync(
            actorIdentity.UserId,
            actorIdentity.RoleCode,
            $"{marker}-rebuild",
            Request(placeId, marker, "Rebuilt mutation fixture", "visible", expectedRevision: 2, sourceRevisionSuffix: "edit-2"),
            CancellationToken.None);
        Assert.Equal(3, rebuilt.Revision);
        Assert.True(rebuilt.ProjectionPublished);
        var rebuiltState = await ReadPlaceStateAsync(connection, placeId);
        Assert.Equal("active", rebuiltState.ProjectionStatus);
        Assert.Equal("visible", rebuiltState.Status);

        var deleted = await database.SoftDeleteAdminPlaceAsync(
            actorIdentity.UserId,
            actorIdentity.RoleCode,
            $"{marker}-delete",
            placeId,
            new PlaceAdminDeleteRequestContract("remove isolated mutation fixture", 3),
            CancellationToken.None);
        Assert.Equal(4, deleted.Revision);
        Assert.Equal("soft_deleted", deleted.Operation);
        Assert.Equal("removed", deleted.Status);
        Assert.False(deleted.ProjectionPublished);

        var deletedState = await ReadPlaceStateAsync(connection, placeId);
        Assert.Equal(4, deletedState.Revision);
        Assert.Equal("removed", deletedState.Status);
        Assert.Equal("disabled", deletedState.ProjectionStatus);
        Assert.Equal(4, await CountRevisionsAsync(connection, placeId));
        Assert.Equal(4, await CountAuditRowsAsync(connection, placeId, marker));
        }
        finally
        {
            await CleanupPlaceFixtureAsync(database, connection, actorIdentity, placeId, marker, replayRunId);
        }
    }

    [Fact]
    public async Task AdminCanHardDeleteScopedLegacyCompatibilityRowsWithAudit()
    {
        if (!DatabaseConfigured() || !MutationIntegrationEnabled()) return;

        await using var connection = await OpenConnectionAsync();
        var actor = await ReadPrivilegedActorAsync(connection);
        if (actor is null) return;
        var actorIdentity = actor;
        var marker = $"legacy-delete-{Guid.NewGuid():N}";
        var @namespace = $"test.{marker}";
        var externalId = $"external-{marker}";
        var sourceVersion = "fixture-1";
        Guid sourceLinkId = Guid.Empty;

        await using (var insertMapping = new NpgsqlCommand(
            """
            insert into aevo_place_legacy_mappings
              (namespace, external_id, source_version, match_status, match_reason)
            values (@namespace, @external_id, @source_version, 'rejected', 'test fixture')
            """,
            connection))
        {
            insertMapping.Parameters.AddWithValue("namespace", @namespace);
            insertMapping.Parameters.AddWithValue("external_id", externalId);
            insertMapping.Parameters.AddWithValue("source_version", sourceVersion);
            await insertMapping.ExecuteNonQueryAsync();
        }

        await using (var insertSourceLink = new NpgsqlCommand(
            """
            insert into aevo_place_source_links
              (source_kind, namespace, external_id, source_version, match_status, raw_snapshot_ref)
            values ('derived', @namespace, @external_id, @source_version, 'retired', @raw_snapshot_ref)
            returning source_link_id
            """,
            connection))
        {
            insertSourceLink.Parameters.AddWithValue("namespace", @namespace);
            insertSourceLink.Parameters.AddWithValue("external_id", externalId);
            insertSourceLink.Parameters.AddWithValue("source_version", sourceVersion);
            insertSourceLink.Parameters.AddWithValue("raw_snapshot_ref", marker);
            sourceLinkId = (Guid)(await insertSourceLink.ExecuteScalarAsync())!;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_DATABASE_URL"] = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")
            })
            .Build();
        await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);

        try
        {
            var mapping = await database.DeleteAdminLegacyMappingAsync(
                actorIdentity.UserId,
                actorIdentity.RoleCode,
                $"{marker}-mapping",
                new PlaceAdminLegacyMappingDeleteRequestContract(@namespace, externalId, sourceVersion, "test legacy mapping cleanup"),
                CancellationToken.None);
            Assert.Equal("legacy_mapping", mapping.RecordKind);
            Assert.False(mapping.WasLinked);

            var sourceLink = await database.DeleteAdminSourceLinkAsync(
                actorIdentity.UserId,
                actorIdentity.RoleCode,
                $"{marker}-source-link",
                sourceLinkId,
                new PlaceAdminSourceLinkDeleteRequestContract("test legacy source-link cleanup"),
                CancellationToken.None);
            Assert.Equal("source_link", sourceLink.RecordKind);
            Assert.False(sourceLink.WasLinked);

            Assert.Equal(0, await CountLegacyMappingAsync(connection, @namespace, externalId, sourceVersion));
            Assert.Equal(0, await CountSourceLinkAsync(connection, sourceLinkId));
            Assert.Equal(2, await CountAuditRowsByMarkerAsync(connection, marker));
        }
        finally
        {
            await CleanupLegacyFixtureAsync(connection, @namespace, externalId, sourceVersion, sourceLinkId);
        }
    }

    private static PlaceAdminPlaceMutationRequestContract Request(
        Guid placeId,
        string marker,
        string name,
        string status,
        long? expectedRevision = null,
        string sourceRevisionSuffix = "create") =>
        new(
            new PlaceSummaryContract(
                placeId.ToString("D"),
                $"{marker}-slug",
                name,
                new[] { new PlaceLocalizedNameContract("en", name, "canonical") },
                new PlaceCategoryContract("test", "Test", Array.Empty<PlaceLocalizedNameContract>()),
                status,
                new PlaceGeoPointContract(100.501, 13.701),
                new PlaceGeoPointContract(100.501, 13.701),
                "Test area",
                null,
                null,
                new PlaceVerificationContract("unverified", null, null, null),
                null,
                new PlaceCapabilitySummaryContract(
                    false,
                    false,
                    false,
                    false,
                    null,
                    new PlaceFreshnessContract("fresh", null, null, $"{marker}-{sourceRevisionSuffix}")),
                Array.Empty<PlaceAttributionContract>(),
                null),
            null,
            null,
            "derived",
            marker,
            $"integration fixture {marker}",
            $"{marker}-{sourceRevisionSuffix}",
            $"integration mutation {sourceRevisionSuffix}",
            expectedRevision,
            "places-public-v1");

    private static async Task<Actor?> ReadPrivilegedActorAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            select user_id, role_code
            from aevo_platform_roles
            where status = 'active'
              and lower(role_code) in ('super_admin', 'platform_owner', 'platform_admin')
            order by case lower(role_code) when 'super_admin' then 0 when 'platform_owner' then 1 else 2 end, user_id
            limit 1
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new Actor(reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    private static async Task<PlaceState> ReadPlaceStateAsync(NpgsqlConnection connection, Guid placeId)
    {
        var result = await TryReadPlaceStateAsync(connection, placeId);
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<PlaceState?> TryReadPlaceStateAsync(NpgsqlConnection connection, Guid placeId)
    {
        await using var command = new NpgsqlCommand(
            """
            select r.revision, r.status, r.source_revision, p.projection_status
            from aevo_place_registry r
            left join aevo_place_public_projections p on p.place_id = r.place_id
            where r.place_id = @place_id
            """,
            connection);
        command.Parameters.AddWithValue("place_id", placeId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new PlaceState(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static async Task CleanupPlaceFixtureAsync(
        CoreDataStore database,
        NpgsqlConnection connection,
        Actor actor,
        Guid placeId,
        string marker,
        Guid? replayRunId)
    {
        var state = await TryReadPlaceStateAsync(connection, placeId);
        if (state is not null && state.Status is not ("removed" or "merged"))
        {
            await database.SoftDeleteAdminPlaceAsync(
                actor.UserId,
                actor.RoleCode,
                $"{marker}-cleanup",
                placeId,
                new PlaceAdminDeleteRequestContract("remove isolated mutation fixture after test", state.Revision),
                CancellationToken.None);
        }

        await using (var command = new NpgsqlCommand(
            """
            delete from aevo_place_public_projections where place_id = @place_id;
            delete from aevo_audit_logs where request_id like @request_prefix;
            """,
            connection))
        {
            command.Parameters.AddWithValue("place_id", placeId);
            command.Parameters.AddWithValue("request_prefix", $"{marker}-%");
            await command.ExecuteNonQueryAsync();
        }

        if (replayRunId.HasValue)
        {
            await using var command = new NpgsqlCommand(
                """
                delete from aevo_place_projection_runs where run_id = @run_id;
                delete from aevo_audit_logs
                where target_type = 'place_projection_run' and target_id = @target_id;
                """,
                connection);
            command.Parameters.AddWithValue("run_id", replayRunId.Value);
            command.Parameters.AddWithValue("target_id", replayRunId.Value.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MarkProjectionFailedWithLastKnownValidAsync(NpgsqlConnection connection, Guid placeId)
    {
        await using var command = new NpgsqlCommand(
            """
            update aevo_place_public_projections
            set last_known_valid_projection_version = projection_version,
                last_known_valid_payload = payload,
                last_known_valid_at = now(),
                projection_status = 'failed',
                invalidation_reason = 'integration fallback check',
                invalidated_at = now()
            where place_id = @place_id
            """,
            connection);
        command.Parameters.AddWithValue("place_id", placeId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<int> CountRevisionsAsync(NpgsqlConnection connection, Guid placeId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_place_revisions where place_id = @place_id",
            connection);
        command.Parameters.AddWithValue("place_id", placeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountAuditRowsAsync(NpgsqlConnection connection, Guid placeId, string marker)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_audit_logs where target_type = 'place' and target_id = @target_id and request_id like @request_prefix",
            connection);
        command.Parameters.AddWithValue("target_id", placeId.ToString("D"));
        command.Parameters.AddWithValue("request_prefix", $"{marker}-%");
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountAuditRowsByMarkerAsync(NpgsqlConnection connection, string marker)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_audit_logs where request_id like @request_prefix",
            connection);
        command.Parameters.AddWithValue("request_prefix", $"{marker}-%");
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountCompletedProjectionRunsAsync(NpgsqlConnection connection, Guid runId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_place_projection_runs where run_id = @run_id and status = 'completed' and rows_published = 1",
            connection);
        command.Parameters.AddWithValue("run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountProjectionReplayAuditRowsAsync(NpgsqlConnection connection, Guid runId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_audit_logs where target_type = 'place_projection_run' and target_id = @target_id and action = 'PLACE_PROJECTION_REPLAY_APPLIED'",
            connection);
        command.Parameters.AddWithValue("target_id", runId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountLegacyMappingAsync(NpgsqlConnection connection, string @namespace, string externalId, string sourceVersion)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_place_legacy_mappings where namespace = @namespace and external_id = @external_id and source_version = @source_version",
            connection);
        command.Parameters.AddWithValue("namespace", @namespace);
        command.Parameters.AddWithValue("external_id", externalId);
        command.Parameters.AddWithValue("source_version", sourceVersion);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountSourceLinkAsync(NpgsqlConnection connection, Guid sourceLinkId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_place_source_links where source_link_id = @source_link_id",
            connection);
        command.Parameters.AddWithValue("source_link_id", sourceLinkId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task CleanupLegacyFixtureAsync(
        NpgsqlConnection connection,
        string @namespace,
        string externalId,
        string sourceVersion,
        Guid sourceLinkId)
    {
        await using var command = new NpgsqlCommand(
            """
            delete from aevo_place_legacy_mappings
            where namespace = @namespace and external_id = @external_id and source_version = @source_version;
            delete from aevo_place_source_links where source_link_id = @source_link_id;
            """,
            connection);
        command.Parameters.AddWithValue("namespace", @namespace);
        command.Parameters.AddWithValue("external_id", externalId);
        command.Parameters.AddWithValue("source_version", sourceVersion);
        command.Parameters.AddWithValue("source_link_id", sourceLinkId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var raw = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Database integration is not configured.");
        var connection = new NpgsqlConnection(NormalizeDatabaseConnectionString(raw));
        await connection.OpenAsync();
        return connection;
    }

    private static bool DatabaseConfigured() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVO_DATABASE_URL"));

    private static bool MutationIntegrationEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("AEVO_RUN_PLACE_DATABASE_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "postgres" and not "postgresql"))
        {
            throw new InvalidOperationException("The database integration URL is not PostgreSQL.");
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
            if (key == "sslmode")
            {
                builder.SslMode = queryValue.ToLowerInvariant() switch
                {
                    "disable" => SslMode.Disable,
                    "allow" => SslMode.Allow,
                    "prefer" => SslMode.Prefer,
                    "require" => SslMode.Require,
                    "verify-ca" => SslMode.VerifyCA,
                    "verify-full" => SslMode.VerifyFull,
                    _ => throw new InvalidOperationException("The database integration URL contains an unsupported sslmode.")
                };
            }
        }
        return builder.ConnectionString;
    }

    private sealed record Actor(Guid UserId, string RoleCode);
    private sealed record PlaceState(long Revision, string Status, string SourceRevision, string? ProjectionStatus);
}
