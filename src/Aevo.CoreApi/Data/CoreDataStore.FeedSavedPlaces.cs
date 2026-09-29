using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record FeedSavedPlaceResult(
    Guid PlaceId,
    bool Saved,
    bool Changed,
    DateTimeOffset UpdatedAt,
    string? RequestId);

public sealed record FeedSavedPlaceRecord(
    Guid PlaceId,
    DateTimeOffset SavedAt);

public sealed class FeedSavedPlaceIdempotencyConflictException() : Exception(
    "The Feed canonical Place save idempotency key was already used for a different request.");

public sealed class FeedSavedPlaceNotPublicException(Guid placeId) : Exception(
    $"Canonical Place {placeId:D} is not currently public.")
{
    public Guid PlaceId { get; } = placeId;
}

public sealed partial class CoreDataStore
{
    private static readonly JsonSerializerOptions FeedSavedPlacesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public async Task<FeedSavedPlaceResult> SetFeedSavedPlaceAsync(
        string principalBinding,
        Guid actorId,
        Guid placeId,
        bool saved,
        string idempotencyKey,
        string requestHash,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetFeedRequestContextAsync(connection, transaction, principalBinding, actorId, cancellationToken);
        await SetFeedSavedPlaceAccessAsync(connection, transaction, cancellationToken);

        await AcquireFeedbackLockAsync(
            connection,
            transaction,
            $"saved-place-idempotency:{actorId:N}:{idempotencyKey}",
            cancellationToken);

        string? existingHash = null;
        string? existingResponseBody = null;
        await using (var existing = new NpgsqlCommand(
            "select request_hash, response_body::text from aevo_feed_saved_places_idempotency where actor_id = @actor_id and app_code = 'GO' and idempotency_key = @idempotency_key",
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("actor_id", actorId);
            existing.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingHash = reader.GetString(0);
                existingResponseBody = reader.GetString(1);
            }
        }

        if (existingHash is not null)
        {
            if (!string.Equals(existingHash, requestHash, StringComparison.Ordinal))
            {
                throw new FeedSavedPlaceIdempotencyConflictException();
            }

            var replay = JsonSerializer.Deserialize<FeedSavedPlaceResult>(
                existingResponseBody!,
                FeedSavedPlacesJsonOptions);
            if (replay is null) throw new CoreDatabaseException("Feed canonical Place save idempotency response is invalid.");
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        await AcquireFeedbackLockAsync(
            connection,
            transaction,
            $"saved-place:{actorId:N}:GO:{placeId:N}",
            cancellationToken);

        if (!await IsPublicCanonicalPlaceAsync(connection, transaction, placeId, cancellationToken))
        {
            throw new FeedSavedPlaceNotPublicException(placeId);
        }

        FeedSavedPlaceResult result;
        if (saved)
        {
            var changed = false;
            DateTimeOffset updatedAt;
            await using (var insert = new NpgsqlCommand(
                "insert into aevo_feed_saved_places (actor_id, app_code, place_id) values (@actor_id, 'GO', @place_id) on conflict (actor_id, app_code, place_id) do nothing returning updated_at",
                connection,
                transaction))
            {
                insert.Parameters.AddWithValue("actor_id", actorId);
                insert.Parameters.AddWithValue("place_id", placeId);
                await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    changed = true;
                    updatedAt = reader.GetFieldValue<DateTimeOffset>(0);
                }
                else
                {
                    updatedAt = default;
                }
            }

            if (!changed)
            {
                await using var existingSave = new NpgsqlCommand(
                    "select updated_at from aevo_feed_saved_places where actor_id = @actor_id and app_code = 'GO' and place_id = @place_id",
                    connection,
                    transaction);
                existingSave.Parameters.AddWithValue("actor_id", actorId);
                existingSave.Parameters.AddWithValue("place_id", placeId);
                await using var reader = await existingSave.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new CoreDatabaseException("Feed canonical Place save state disappeared during the mutation.");
                }
                updatedAt = reader.GetFieldValue<DateTimeOffset>(0);
            }

            result = new FeedSavedPlaceResult(placeId, true, changed, updatedAt, requestId);
        }
        else
        {
            await using var delete = new NpgsqlCommand(
                "delete from aevo_feed_saved_places where actor_id = @actor_id and app_code = 'GO' and place_id = @place_id",
                connection,
                transaction);
            delete.Parameters.AddWithValue("actor_id", actorId);
            delete.Parameters.AddWithValue("place_id", placeId);
            var changed = await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
            var updatedAt = await ReadTransactionNowAsync(connection, transaction, cancellationToken);
            result = new FeedSavedPlaceResult(placeId, false, changed, updatedAt, requestId);
        }

        var responseBodyJson = JsonSerializer.Serialize(result, FeedSavedPlacesJsonOptions);
        await using (var insertIdempotency = new NpgsqlCommand(
            "insert into aevo_feed_saved_places_idempotency (actor_id, app_code, idempotency_key, request_hash, response_body) values (@actor_id, 'GO', @idempotency_key, @request_hash, @response_body)",
            connection,
            transaction))
        {
            insertIdempotency.Parameters.AddWithValue("actor_id", actorId);
            insertIdempotency.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertIdempotency.Parameters.AddWithValue("request_hash", requestHash);
            insertIdempotency.Parameters.Add(new NpgsqlParameter("response_body", NpgsqlDbType.Jsonb)
            {
                Value = responseBodyJson
            });
            await insertIdempotency.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<FeedSavedPlaceRecord>> ListFeedSavedPlacesAsync(
        string principalBinding,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetFeedRequestContextAsync(connection, transaction, principalBinding, actorId, cancellationToken);

        await using var command = new NpgsqlCommand(
            "select place_id, created_at from aevo_feed_saved_places where actor_id = @actor_id and app_code = 'GO' order by created_at desc, place_id asc",
            connection,
            transaction);
        command.Parameters.AddWithValue("actor_id", actorId);

        var records = new List<FeedSavedPlaceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FeedSavedPlaceRecord(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1)));
        }

        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    private static async Task<bool> IsPublicCanonicalPlaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid placeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists (
              select 1
              from aevo_place_registry r
              join aevo_place_public_projections p on p.place_id = r.place_id
              where r.place_id = @place_id
                and r.status in ('visible', 'limited')
                and p.projection_status in ('active', 'stale', 'failed')
                and (p.projection_status <> 'failed' or p.last_known_valid_payload is not null)
            )
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<DateTimeOffset> ReadTransactionNowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select now()", connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new CoreDatabaseException("Database time could not be read.");
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new CoreDatabaseException("Database time has an unsupported type.")
        };
    }

    private static async Task SetFeedSavedPlaceAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('aevo.place_access', 'internal', true)",
            connection,
            transaction);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
