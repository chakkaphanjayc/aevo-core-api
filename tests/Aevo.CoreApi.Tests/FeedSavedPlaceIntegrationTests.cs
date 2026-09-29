using Aevo.CoreApi.Data;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[Collection("Feed database integration")]
public sealed class FeedSavedPlaceIntegrationTests
{
    [Fact]
    public async Task CanonicalPlaceSaveIsRlsBoundIdempotentAndReversible()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection);
        if (actorId is null) return;

        var placeId = Guid.NewGuid();
        var idempotencyKey = $"feed-save-{Guid.NewGuid():N}";
        var conflictKey = $"feed-save-conflict-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        try
        {
            await AssertRlsSchemaAsync(connection);
            await InsertPublicPlaceAsync(connection, placeId, now);

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
            var service = new FeedSavedPlaceService(database);
            var principal = FeedPrincipalFactory.FromGoSession(new CoreSession(
                Guid.NewGuid(),
                actorId.Value,
                "GO",
                now.AddHours(1),
                null,
                null,
                "feed-save@example.test",
                "Feed Save Test",
                null,
                null));

            var first = await service.SetAsync(
                principal,
                placeId,
                true,
                idempotencyKey,
                "save-request-1",
                CancellationToken.None);
            Assert.Equal(placeId, first.PlaceId);
            Assert.True(first.Saved);
            Assert.True(first.Changed);
            Assert.Equal("save-request-1", first.RequestId);
            Assert.Equal(1L, await CountSavedRowsAsync(connection, actorId.Value, placeId));

            var replay = await service.SetAsync(
                principal,
                placeId,
                true,
                idempotencyKey,
                "save-request-retry",
                CancellationToken.None);
            Assert.Equal(first, replay);
            Assert.Equal(1L, await CountSavedRowsAsync(connection, actorId.Value, placeId));

            await Assert.ThrowsAsync<FeedSavedPlaceIdempotencyConflictException>(() =>
                service.SetAsync(
                    principal,
                    placeId,
                    false,
                    idempotencyKey,
                    "save-request-conflict",
                    CancellationToken.None));

            var savedPlaces = await service.ListAsync(principal, "saved-list-request", CancellationToken.None);
            var saved = Assert.Single(savedPlaces.SavedPlaces);
            Assert.Equal(placeId, saved.PlaceId);

            var removed = await service.SetAsync(
                principal,
                placeId,
                false,
                conflictKey,
                "unsave-request-1",
                CancellationToken.None);
            Assert.Equal(placeId, removed.PlaceId);
            Assert.False(removed.Saved);
            Assert.True(removed.Changed);
            Assert.Equal(0L, await CountSavedRowsAsync(connection, actorId.Value, placeId));

            var removedAgain = await service.SetAsync(
                principal,
                placeId,
                false,
                $"feed-unsave-{Guid.NewGuid():N}",
                "unsave-request-2",
                CancellationToken.None);
            Assert.False(removedAgain.Saved);
            Assert.False(removedAgain.Changed);

            await SetPlaceStatusAsync(connection, placeId, "removed");
            await Assert.ThrowsAsync<FeedSavedPlaceNotPublicException>(() =>
                service.SetAsync(
                    principal,
                    placeId,
                    true,
                    $"feed-save-removed-{Guid.NewGuid():N}",
                    "save-request-removed",
                    CancellationToken.None));
        }
        finally
        {
            await CleanupPlaceAsync(connection, actorId.Value, placeId, idempotencyKey, conflictKey);
        }
    }

    [Fact]
    public async Task LegacyCustomerFavoriteReconciliationRequiresExplicitMappingAndIsExactlyReversible()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection);
        var store = await ReadReconciliationStoreAsync(connection, actorId);
        if (actorId is null || store is null) return;

        var placeId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();
        var legacyCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var dryRunKey = $"legacy-dry-run-{Guid.NewGuid():N}";
        var migrateKey = $"legacy-migrate-{Guid.NewGuid():N}";
        var deleteKey = $"legacy-delete-{Guid.NewGuid():N}";
        var requestId = $"legacy-reconcile-{Guid.NewGuid():N}";
        Guid? dryRunId = null;
        Guid? migrateId = null;
        Guid? deleteId = null;
        try
        {
            await InsertReconciliationFixtureAsync(
                connection,
                actorId.Value,
                store.Value.StoreId,
                store.Value.OrganizationId,
                placeId,
                relationshipId,
                legacyCreatedAt);

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
            var reconciliation = new FeedSavedPlaceReconciliationService(database);
            var adminSession = new CoreSession(
                Guid.NewGuid(),
                actorId.Value,
                "ADMIN",
                DateTimeOffset.UtcNow.AddHours(1),
                null,
                null,
                "legacy-reconcile@example.test",
                "Legacy Reconciliation Test",
                "PLATFORM_ADMIN",
                null);

            var dryRun = await reconciliation.RunAsync(
                adminSession,
                new FeedSavedPlaceReconciliationRequestContract(
                    Mode: "DRY_RUN",
                    CustomerId: actorId,
                    StoreId: store.Value.StoreId,
                    Limit: 10,
                    IdempotencyKey: dryRunKey,
                    Reason: "controlled reconciliation dry run"),
                requestId,
                CancellationToken.None);
            dryRunId = dryRun.RunId;
            Assert.Equal(1, dryRun.Scanned);
            Assert.Equal(1, dryRun.Mapped);
            Assert.Equal(1, dryRun.Mapped + dryRun.Unmapped + dryRun.Ambiguous);
            Assert.Equal(0, dryRun.Inserted);
            Assert.Equal(0, dryRun.DeletedLegacy);
            Assert.Equal(1L, await CountLegacyFavoriteAsync(connection, actorId.Value, store.Value.StoreId));

            var migrated = await reconciliation.RunAsync(
                adminSession,
                new FeedSavedPlaceReconciliationRequestContract(
                    Mode: "MIGRATE",
                    CustomerId: actorId,
                    StoreId: store.Value.StoreId,
                    Limit: 10,
                    IdempotencyKey: migrateKey,
                    Reason: "controlled reconciliation migration"),
                requestId,
                CancellationToken.None);
            migrateId = migrated.RunId;
            Assert.Equal(1, migrated.Inserted);
            Assert.Equal(0, migrated.DeletedLegacy);
            Assert.Equal(1L, await CountSavedRowsAsync(connection, actorId.Value, placeId));
            Assert.Equal(1L, await CountLegacyFavoriteAsync(connection, actorId.Value, store.Value.StoreId));

            var replay = await reconciliation.RunAsync(
                adminSession,
                new FeedSavedPlaceReconciliationRequestContract(
                    Mode: "MIGRATE",
                    CustomerId: actorId,
                    StoreId: store.Value.StoreId,
                    Limit: 10,
                    IdempotencyKey: migrateKey,
                    Reason: "controlled reconciliation migration"),
                "different-replay-request-id",
                CancellationToken.None);
            Assert.Equal(migrated, replay);
            Assert.Equal(1L, await CountSavedRowsAsync(connection, actorId.Value, placeId));

            var migratedAndDeleted = await reconciliation.RunAsync(
                adminSession,
                new FeedSavedPlaceReconciliationRequestContract(
                    Mode: "MIGRATE_AND_DELETE",
                    CustomerId: actorId,
                    StoreId: store.Value.StoreId,
                    Limit: 10,
                    ConfirmLegacyDelete: true,
                    IdempotencyKey: deleteKey,
                    Reason: "controlled reconciliation delete"),
                requestId,
                CancellationToken.None);
            deleteId = migratedAndDeleted.RunId;
            Assert.Equal(0, migratedAndDeleted.Inserted);
            Assert.Equal(1, migratedAndDeleted.DeletedLegacy);
            Assert.Equal(0L, await CountLegacyFavoriteAsync(connection, actorId.Value, store.Value.StoreId));
            Assert.Equal(1L, await CountSavedRowsAsync(connection, actorId.Value, placeId));
        }
        finally
        {
            await CleanupReconciliationFixtureAsync(
                connection,
                actorId.Value,
                store.Value.StoreId,
                placeId,
                relationshipId,
                dryRunKey,
                migrateKey,
                deleteKey,
                dryRunId,
                migrateId,
                deleteId);
        }
    }

    private static async Task InsertPublicPlaceAsync(
        NpgsqlConnection connection,
        Guid placeId,
        DateTimeOffset observedAt)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_registry
              (place_id, slug, name, category, status, source_revision, published_at)
            values
              (@place_id, @slug, 'Feed saved Place fixture', '{"id":"test","label":"Test"}'::jsonb, 'visible', @source_revision, @published_at);
            insert into aevo_place_public_projections
              (place_id, projection_version, source_revision, freshness_state, observed_at, expires_at, payload, projection_status, generated_at)
            values
              (@place_id, 'places-public-v1', @source_revision, 'fresh', @published_at, @expires_at, '{"name":"Feed saved Place fixture"}'::jsonb, 'active', @published_at);
            """,
            connection);
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = $"feed-saved-place-{placeId:N}";
        command.Parameters.Add("source_revision", NpgsqlDbType.Text).Value = $"feed-saved-place-{placeId:N}";
        command.Parameters.Add("published_at", NpgsqlDbType.TimestampTz).Value = observedAt;
        command.Parameters.Add("expires_at", NpgsqlDbType.TimestampTz).Value = observedAt.AddMinutes(5);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertReconciliationFixtureAsync(
        NpgsqlConnection connection,
        Guid actorId,
        Guid storeId,
        Guid organizationId,
        Guid placeId,
        Guid relationshipId,
        DateTimeOffset legacyCreatedAt)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_registry
              (place_id, slug, name, category, status, source_revision, published_at)
            values
              (@place_id, @slug, 'Legacy reconciliation Place fixture', '{"id":"test","label":"Test"}'::jsonb, 'visible', @source_revision, @published_at);
            insert into aevo_place_public_projections
              (place_id, projection_version, source_revision, freshness_state, observed_at, expires_at, payload, projection_status, generated_at)
            values
              (@place_id, 'places-public-v1', @source_revision, 'fresh', @published_at, @expires_at, '{"name":"Legacy reconciliation Place fixture"}'::jsonb, 'active', @published_at);
            insert into aevo_place_relationships
              (relationship_id, place_id, relationship_type, organization_id, store_id, is_primary, status, verification_status, source_kind, source_id, created_by, approved_by)
            values
              (@relationship_id, @place_id, 'store', @organization_id, @store_id, true, 'active', 'verified', 'aevo_admin', @source_id, @actor_id, @actor_id);
            insert into public.customer_favorites (customer_id, store_id, created_at)
            values (@actor_id, @store_id, @legacy_created_at);
            """,
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("store_id", NpgsqlDbType.Uuid).Value = storeId;
        command.Parameters.Add("organization_id", NpgsqlDbType.Uuid).Value = organizationId;
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        command.Parameters.Add("relationship_id", NpgsqlDbType.Uuid).Value = relationshipId;
        command.Parameters.Add("source_id", NpgsqlDbType.Text).Value = $"legacy-reconcile-{placeId:N}";
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = $"legacy-reconcile-{placeId:N}";
        command.Parameters.Add("source_revision", NpgsqlDbType.Text).Value = $"legacy-reconcile-{placeId:N}";
        command.Parameters.Add("published_at", NpgsqlDbType.TimestampTz).Value = legacyCreatedAt;
        command.Parameters.Add("expires_at", NpgsqlDbType.TimestampTz).Value = legacyCreatedAt.AddMinutes(5);
        command.Parameters.Add("legacy_created_at", NpgsqlDbType.TimestampTz).Value = legacyCreatedAt;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetPlaceStatusAsync(NpgsqlConnection connection, Guid placeId, string status)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_place_registry set status = @status where place_id = @place_id",
            connection);
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = status;
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountSavedRowsAsync(NpgsqlConnection connection, Guid actorId, Guid placeId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from aevo_feed_saved_places where actor_id = @actor_id and place_id = @place_id",
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountLegacyFavoriteAsync(NpgsqlConnection connection, Guid actorId, Guid storeId)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from public.customer_favorites where customer_id = @actor_id and store_id = @store_id",
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("store_id", NpgsqlDbType.Uuid).Value = storeId;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<(Guid StoreId, Guid OrganizationId)?> ReadReconciliationStoreAsync(
        NpgsqlConnection connection,
        Guid? actorId)
    {
        if (actorId is null) return null;
        await using var command = new NpgsqlCommand(
            """
            select s.id, s.organization_id
            from public.stores s
            where not exists (
                select 1
                from public.customer_favorites favorite
                where favorite.customer_id = @actor_id and favorite.store_id = s.id
            )
              and not exists (
                select 1
                from aevo_place_relationships relationship
                where relationship.store_id = s.id
                  and relationship.relationship_type = 'store'
                  and relationship.status = 'active'
                  and relationship.is_primary
            )
            order by s.id
            limit 1
            """,
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId.Value;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    private static async Task CleanupReconciliationFixtureAsync(
        NpgsqlConnection connection,
        Guid actorId,
        Guid storeId,
        Guid placeId,
        Guid relationshipId,
        string dryRunKey,
        string migrateKey,
        string deleteKey,
        Guid? dryRunId,
        Guid? migrateId,
        Guid? deleteId)
    {
        await using var command = new NpgsqlCommand(
            """
            delete from public.customer_favorites where customer_id = @actor_id and store_id = @store_id;
            delete from aevo_feed_saved_place_reconciliation_runs where actor_id = @actor_id and idempotency_key in (@dry_run_key, @migrate_key, @delete_key);
            delete from aevo_feed_saved_places where actor_id = @actor_id and place_id = @place_id;
            delete from aevo_audit_logs where target_type = 'feed_saved_place_legacy_reconciliation' and target_id = any(@run_ids);
            delete from aevo_place_relationships where relationship_id = @relationship_id;
            delete from aevo_place_public_projections where place_id = @place_id;
            delete from aevo_place_registry where place_id = @place_id;
            """,
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("store_id", NpgsqlDbType.Uuid).Value = storeId;
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        command.Parameters.Add("relationship_id", NpgsqlDbType.Uuid).Value = relationshipId;
        command.Parameters.Add("dry_run_key", NpgsqlDbType.Text).Value = dryRunKey;
        command.Parameters.Add("migrate_key", NpgsqlDbType.Text).Value = migrateKey;
        command.Parameters.Add("delete_key", NpgsqlDbType.Text).Value = deleteKey;
        command.Parameters.Add(new NpgsqlParameter("run_ids", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = new[] { dryRunId, migrateId, deleteId }
                .Where(id => id.HasValue)
                .Select(id => id!.Value.ToString("D"))
                .ToArray()
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupPlaceAsync(
        NpgsqlConnection connection,
        Guid actorId,
        Guid placeId,
        string idempotencyKey,
        string conflictKey)
    {
        await using var command = new NpgsqlCommand(
            "delete from aevo_feed_saved_places_idempotency where actor_id = @actor_id and idempotency_key in (@idempotency_key, @conflict_key); delete from aevo_feed_saved_places where actor_id = @actor_id and place_id = @place_id; delete from aevo_place_public_projections where place_id = @place_id; delete from aevo_place_registry where place_id = @place_id;",
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = placeId;
        command.Parameters.Add("idempotency_key", NpgsqlDbType.Text).Value = idempotencyKey;
        command.Parameters.Add("conflict_key", NpgsqlDbType.Text).Value = conflictKey;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRlsSchemaAsync(NpgsqlConnection connection)
    {
        await using var table = new NpgsqlCommand(
            "select relrowsecurity from pg_class where oid = 'aevo_feed_saved_places'::regclass",
            connection);
        Assert.True(Convert.ToBoolean(await table.ExecuteScalarAsync()));

        await using var idempotencyTable = new NpgsqlCommand(
            "select relrowsecurity from pg_class where oid = 'aevo_feed_saved_places_idempotency'::regclass",
            connection);
        Assert.True(Convert.ToBoolean(await idempotencyTable.ExecuteScalarAsync()));

        await using var policies = new NpgsqlCommand(
            "select count(*) from pg_policies where schemaname = 'public' and tablename = 'aevo_feed_saved_places' and policyname in ('aevo_feed_saved_places_owner_read_policy', 'aevo_feed_saved_places_owner_insert_policy', 'aevo_feed_saved_places_owner_delete_policy')",
            connection);
        Assert.Equal(3L, Convert.ToInt64(await policies.ExecuteScalarAsync()));
    }

    private static async Task<Guid?> ReadActiveUserAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select u.id from auth.users u join public.user_profiles p on p.id = u.id and p.status = 'ACTIVE' order by u.created_at asc, u.id asc limit 1",
            connection);
        return await command.ExecuteScalarAsync() as Guid?;
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

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not "postgres" and not "postgresql")
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
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), "sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = Uri.UnescapeDataString(pair[1]).ToLowerInvariant() switch
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
}
