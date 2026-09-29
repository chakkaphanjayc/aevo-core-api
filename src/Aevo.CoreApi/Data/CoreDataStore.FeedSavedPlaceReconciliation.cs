using System.Data;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed class FeedSavedPlaceReconciliationIdempotencyConflictException() : Exception(
    "The legacy favorite reconciliation idempotency key was already used for a different request.");

public sealed class LegacyCustomerFavoritesSourceUnavailableException() : Exception(
    "The legacy Customer favorites source is not available in this environment.");

public sealed partial class CoreDataStore
{
    private static readonly JsonSerializerOptions FeedSavedPlaceReconciliationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public async Task<FeedSavedPlaceReconciliationResponseContract> ReconcileLegacyCustomerFavoritesAsync(
        Guid actorId,
        string? platformRole,
        FeedSavedPlaceReconciliationRequestContract request,
        string requestHash,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);
        await AcquireReconciliationLockAsync(connection, transaction, actorId, request.IdempotencyKey!, cancellationToken);

        var existing = await ReadExistingReconciliationAsync(
            connection,
            transaction,
            actorId,
            request.IdempotencyKey!,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new FeedSavedPlaceReconciliationIdempotencyConflictException();
            }

            await transaction.CommitAsync(cancellationToken);
            return existing.Value.Response;
        }

        if (!await LegacyFavoritesSourceExistsAsync(connection, transaction, cancellationToken))
        {
            throw new LegacyCustomerFavoritesSourceUnavailableException();
        }

        var candidates = await ReadLegacyFavoriteCandidatesAsync(
            connection,
            transaction,
            request,
            cancellationToken);
        var limit = request.Limit!.Value;
        var work = candidates.Take(limit).ToArray();
        var result = new ReconciliationAccumulator(work, candidates.Count > limit);

        var inserted = 0;
        var deleted = 0;
        if (!string.Equals(request.Mode, "DRY_RUN", StringComparison.Ordinal))
        {
            foreach (var candidate in work.Where(candidate => candidate.IsEligible))
            {
                if (await InsertCanonicalSavedPlaceAsync(connection, transaction, candidate, cancellationToken))
                {
                    inserted++;
                }

                if (string.Equals(request.Mode, "MIGRATE_AND_DELETE", StringComparison.Ordinal)
                    && await DeleteLegacyFavoriteAsync(connection, transaction, candidate, cancellationToken))
                {
                    deleted++;
                }
            }
        }

        var response = new FeedSavedPlaceReconciliationResponseContract(
            Guid.NewGuid(),
            request.Mode!,
            "COMPLETED",
            request.CustomerId,
            request.StoreId,
            limit,
            result.Scanned,
            result.Mapped,
            result.Unmapped,
            result.Ambiguous,
            result.IdentityMissing,
            result.NotPublic,
            result.AlreadySaved,
            inserted,
            deleted,
            result.Truncated,
            request.Reason!,
            requestId);
        var responseBody = JsonSerializer.SerializeToElement(response, FeedSavedPlaceReconciliationJsonOptions);

        await InsertReconciliationRunAsync(
            connection,
            transaction,
            actorId,
            request,
            requestHash,
            response,
            responseBody,
            requestId,
            cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            actorId,
            "ADMIN",
            string.Equals(request.Mode, "MIGRATE_AND_DELETE", StringComparison.Ordinal)
                ? "FEED_LEGACY_FAVORITES_MIGRATED_AND_DELETED"
                : string.Equals(request.Mode, "MIGRATE", StringComparison.Ordinal)
                    ? "FEED_LEGACY_FAVORITES_MIGRATED"
                    : "FEED_LEGACY_FAVORITES_RECONCILIATION_DRY_RUN",
            "feed_saved_place_legacy_reconciliation",
            response.RunId.ToString("D"),
            request.Reason!,
            null,
            responseBody,
            requestId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private static async Task AcquireReconciliationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", $"feed-legacy-favorites:{actorId:N}:{idempotencyKey}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(string RequestHash, FeedSavedPlaceReconciliationResponseContract Response)?> ReadExistingReconciliationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select request_hash, response_body::text from aevo_feed_saved_place_reconciliation_runs where actor_id = @actor_id and idempotency_key = @idempotency_key",
            connection,
            transaction);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var response = JsonSerializer.Deserialize<FeedSavedPlaceReconciliationResponseContract>(
            reader.GetString(1),
            FeedSavedPlaceReconciliationJsonOptions)
            ?? throw new CoreDatabaseException("The legacy favorite reconciliation response is invalid.");
        return (reader.GetString(0), response);
    }

    private static async Task<bool> LegacyFavoritesSourceExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select to_regclass('public.customer_favorites') is not null",
            connection,
            transaction);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<LegacyFavoriteCandidate>> ReadLegacyFavoriteCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FeedSavedPlaceReconciliationRequestContract request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            with source_rows as materialized (
              select customer_id, store_id, created_at
              from public.customer_favorites
              where (@customer_id is null or customer_id = @customer_id)
                and (@store_id is null or store_id = @store_id)
              order by customer_id asc, store_id asc
              limit @limit_plus_one
              for update
            ),
            mappings as (
              select
                source_rows.customer_id,
                source_rows.store_id,
                source_rows.created_at,
                coalesce(
                  array_agg(distinct relationship.place_id order by relationship.place_id)
                    filter (where relationship.place_id is not null),
                  '{}'::uuid[]
                ) as place_ids
              from source_rows
              left join aevo_place_relationships relationship
                on relationship.store_id = source_rows.store_id
               and relationship.relationship_type = 'store'
               and relationship.status = 'active'
               and relationship.verification_status = 'verified'
               and relationship.is_primary
               and relationship.revoked_at is null
              group by source_rows.customer_id, source_rows.store_id, source_rows.created_at
            )
            select
              mappings.customer_id,
              mappings.store_id,
              mappings.created_at,
              mappings.place_ids,
              identity_user.id is not null as identity_exists,
              case
                when cardinality(mappings.place_ids) = 1
                 and registry.status in ('visible', 'limited')
                 and exists (
                   select 1
                   from aevo_place_public_projections projection
                   where projection.place_id = registry.place_id
                     and projection.projection_status in ('active', 'stale', 'failed')
                     and (projection.projection_status <> 'failed' or projection.last_known_valid_payload is not null)
                 )
                then true
                else false
              end as place_is_public,
              case
                when cardinality(mappings.place_ids) = 1
                 and exists (
                   select 1
                   from aevo_feed_saved_places saved
                   where saved.actor_id = mappings.customer_id
                     and saved.app_code = 'GO'
                     and saved.place_id = mappings.place_ids[1]
                 )
                then true
                else false
              end as already_saved
            from mappings
            left join aevo_identity_users identity_user on identity_user.id = mappings.customer_id
            left join aevo_place_registry registry
              on cardinality(mappings.place_ids) = 1
             and registry.place_id = mappings.place_ids[1]
            order by mappings.customer_id asc, mappings.store_id asc
            """,
            connection,
            transaction)
        {
            CommandTimeout = 10
        };
        command.Parameters.Add(new NpgsqlParameter("customer_id", NpgsqlDbType.Uuid) { Value = (object?)request.CustomerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)request.StoreId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit_plus_one", request.Limit!.Value + 1);

        var result = new List<LegacyFavoriteCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var placeIds = reader.GetFieldValue<Guid[]>(3);
            result.Add(new LegacyFavoriteCandidate(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                placeIds,
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6)));
        }
        return result;
    }

    private static async Task<bool> InsertCanonicalSavedPlaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyFavoriteCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "insert into aevo_feed_saved_places (actor_id, app_code, place_id, created_at, updated_at) values (@actor_id, 'GO', @place_id, @created_at, @created_at) on conflict (actor_id, app_code, place_id) do nothing",
            connection,
            transaction);
        command.Parameters.AddWithValue("actor_id", candidate.CustomerId);
        command.Parameters.AddWithValue("place_id", candidate.PlaceId);
        command.Parameters.AddWithValue("created_at", candidate.CreatedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> DeleteLegacyFavoriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LegacyFavoriteCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from public.customer_favorites where customer_id = @customer_id and store_id = @store_id and created_at = @created_at",
            connection,
            transaction);
        command.Parameters.AddWithValue("customer_id", candidate.CustomerId);
        command.Parameters.AddWithValue("store_id", candidate.StoreId);
        command.Parameters.AddWithValue("created_at", candidate.CreatedAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertReconciliationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        FeedSavedPlaceReconciliationRequestContract request,
        string requestHash,
        FeedSavedPlaceReconciliationResponseContract response,
        JsonElement responseBody,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_feed_saved_place_reconciliation_runs
              (run_id, actor_id, mode, customer_id, store_id, requested_limit,
               scanned_count, mapped_count, unmapped_count, ambiguous_count,
               identity_missing_count, not_public_count, already_saved_count,
               inserted_count, deleted_legacy_count, truncated, status, reason,
               request_id, idempotency_key, request_hash, response_body)
            values
              (@run_id, @actor_id, @mode, @customer_id, @store_id, @requested_limit,
               @scanned, @mapped, @unmapped, @ambiguous, @identity_missing,
               @not_public, @already_saved, @inserted, @deleted_legacy, @truncated,
               'COMPLETED', @reason, @request_id, @idempotency_key, @request_hash,
               @response_body)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", response.RunId);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("mode", response.Mode);
        command.Parameters.Add(new NpgsqlParameter("customer_id", NpgsqlDbType.Uuid) { Value = (object?)response.CustomerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("store_id", NpgsqlDbType.Uuid) { Value = (object?)response.StoreId ?? DBNull.Value });
        command.Parameters.AddWithValue("requested_limit", response.RequestedLimit);
        command.Parameters.AddWithValue("scanned", response.Scanned);
        command.Parameters.AddWithValue("mapped", response.Mapped);
        command.Parameters.AddWithValue("unmapped", response.Unmapped);
        command.Parameters.AddWithValue("ambiguous", response.Ambiguous);
        command.Parameters.AddWithValue("identity_missing", response.IdentityMissing);
        command.Parameters.AddWithValue("not_public", response.NotPublic);
        command.Parameters.AddWithValue("already_saved", response.AlreadySaved);
        command.Parameters.AddWithValue("inserted", response.Inserted);
        command.Parameters.AddWithValue("deleted_legacy", response.DeletedLegacy);
        command.Parameters.AddWithValue("truncated", response.Truncated);
        command.Parameters.AddWithValue("reason", response.Reason);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey!);
        command.Parameters.AddWithValue("request_hash", requestHash);
        command.Parameters.Add(new NpgsqlParameter("response_body", NpgsqlDbType.Jsonb) { Value = responseBody.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record LegacyFavoriteCandidate(
        Guid CustomerId,
        Guid StoreId,
        DateTimeOffset CreatedAt,
        Guid[] PlaceIds,
        bool IdentityExists,
        bool PlaceIsPublic,
        bool AlreadySaved)
    {
        public int MappingCount => PlaceIds.Length;
        public Guid PlaceId => PlaceIds[0];
        public bool IsEligible => MappingCount == 1 && IdentityExists && PlaceIsPublic;
    }

    private sealed class ReconciliationAccumulator
    {
        public ReconciliationAccumulator(IReadOnlyList<LegacyFavoriteCandidate> candidates, bool truncated)
        {
            Scanned = candidates.Count;
            Truncated = truncated;
            Mapped = candidates.Count(candidate => candidate.MappingCount == 1);
            Unmapped = candidates.Count(candidate => candidate.MappingCount == 0);
            Ambiguous = candidates.Count(candidate => candidate.MappingCount > 1);
            IdentityMissing = candidates.Count(candidate => candidate.MappingCount == 1 && !candidate.IdentityExists);
            NotPublic = candidates.Count(candidate => candidate.MappingCount == 1 && candidate.IdentityExists && !candidate.PlaceIsPublic);
            AlreadySaved = candidates.Count(candidate => candidate.IsEligible && candidate.AlreadySaved);
        }

        public int Scanned { get; }
        public int Mapped { get; }
        public int Unmapped { get; }
        public int Ambiguous { get; }
        public int IdentityMissing { get; }
        public int NotPublic { get; }
        public int AlreadySaved { get; }
        public bool Truncated { get; }
    }
}
