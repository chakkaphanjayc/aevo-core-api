using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record FeedNegativeFeedbackResult(
    string ItemType,
    string ItemId,
    string Action,
    bool Active,
    DateTimeOffset UpdatedAt,
    string? RequestId);

public sealed record FeedNegativeFeedbackHistoryRecord(
    string ItemType,
    string ItemId,
    string Action,
    string? ReasonCode,
    bool Active,
    DateTimeOffset CreatedAt);

public sealed class FeedNegativeFeedbackIdempotencyConflictException() : Exception(
    "The Feed negative-feedback idempotency key was already used for a different request.");

public sealed partial class CoreDataStore
{
    private static readonly JsonSerializerOptions FeedFeedbackJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<FeedNegativeFeedbackResult> UpsertFeedNegativeFeedbackAsync(
        string principalBinding,
        Guid actorId,
        Guid feedSessionId,
        string itemType,
        string itemId,
        string itemTokenHash,
        string action,
        string? reasonCode,
        bool active,
        string idempotencyKey,
        string requestHash,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetFeedRequestContextAsync(connection, transaction, principalBinding, actorId, cancellationToken);

        await AcquireFeedbackLockAsync(
            connection,
            transaction,
            $"idempotency:{actorId:N}:{idempotencyKey}",
            cancellationToken);

        await using (var existing = new NpgsqlCommand(
            "select request_hash, response_body::text from aevo_feed_negative_feedback_idempotency where actor_id = @actor_id and app_code = 'GO' and idempotency_key = @idempotency_key",
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("actor_id", actorId);
            existing.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingHash = reader.GetString(0);
                var responseBody = reader.GetString(1);
                await reader.DisposeAsync();
                if (!string.Equals(existingHash, requestHash, StringComparison.Ordinal))
                {
                    throw new FeedNegativeFeedbackIdempotencyConflictException();
                }

                var replay = JsonSerializer.Deserialize<FeedNegativeFeedbackResult>(responseBody, FeedFeedbackJsonOptions);
                if (replay is null) throw new CoreDatabaseException("Feed negative-feedback idempotency response is invalid.");
                await transaction.CommitAsync(cancellationToken);
                return replay;
            }
        }

        await AcquireFeedbackLockAsync(
            connection,
            transaction,
            $"item:{actorId:N}:GO:{itemType}:{itemId}",
            cancellationToken);

        FeedNegativeFeedbackResult result;
        await using (var upsert = new NpgsqlCommand(
            """
            insert into aevo_feed_negative_feedback
              (actor_id, app_code, item_type, item_id, feed_session_id, item_token_hash, action, reason_code, active)
            values
              (@actor_id, 'GO', @item_type, @item_id, @feed_session_id, @item_token_hash, @action, @reason_code, @active)
            on conflict (actor_id, app_code, item_type, item_id) do update
            set feed_session_id = excluded.feed_session_id,
                item_token_hash = excluded.item_token_hash,
                action = excluded.action,
                reason_code = excluded.reason_code,
                active = @active,
                updated_at = now()
            returning item_type, item_id, action, active, updated_at
            """,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue("actor_id", actorId);
            upsert.Parameters.AddWithValue("item_type", itemType);
            upsert.Parameters.AddWithValue("item_id", itemId);
            upsert.Parameters.AddWithValue("feed_session_id", feedSessionId);
            upsert.Parameters.AddWithValue("item_token_hash", itemTokenHash);
            upsert.Parameters.AddWithValue("action", action);
            upsert.Parameters.AddWithValue("active", active);
            upsert.Parameters.Add(new NpgsqlParameter("reason_code", NpgsqlDbType.Text)
            {
                Value = (object?)reasonCode ?? DBNull.Value
            });

            await using var reader = await upsert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new CoreDatabaseException("Feed negative feedback could not be stored.");
            }

            result = new FeedNegativeFeedbackResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                requestId);
        }

        await using (var history = new NpgsqlCommand(
            "insert into aevo_feed_negative_feedback_history (actor_id, app_code, item_type, item_id, feed_session_id, item_token_hash, action, reason_code, active, idempotency_key, request_hash, request_id) values (@actor_id, 'GO', @item_type, @item_id, @feed_session_id, @item_token_hash, @action, @reason_code, @active, @idempotency_key, @request_hash, @request_id)",
            connection,
            transaction))
        {
            history.Parameters.AddWithValue("actor_id", actorId);
            history.Parameters.AddWithValue("item_type", itemType);
            history.Parameters.AddWithValue("item_id", itemId);
            history.Parameters.AddWithValue("feed_session_id", feedSessionId);
            history.Parameters.AddWithValue("item_token_hash", itemTokenHash);
            history.Parameters.AddWithValue("action", action);
            history.Parameters.Add("reason_code", NpgsqlDbType.Text).Value = (object?)reasonCode ?? DBNull.Value;
            history.Parameters.AddWithValue("active", active);
            history.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            history.Parameters.AddWithValue("request_hash", requestHash);
            history.Parameters.AddWithValue("request_id", requestId);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        var responseBodyJson = JsonSerializer.Serialize(result, FeedFeedbackJsonOptions);
        await using (var insertIdempotency = new NpgsqlCommand(
            "insert into aevo_feed_negative_feedback_idempotency (actor_id, app_code, idempotency_key, request_hash, response_body) values (@actor_id, 'GO', @idempotency_key, @request_hash, @response_body)",
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

    public async Task<IReadOnlyList<FeedNegativeFeedbackHistoryRecord>> ListFeedNegativeFeedbackHistoryAsync(
        string principalBinding,
        Guid actorId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetFeedRequestContextAsync(connection, transaction, principalBinding, actorId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            select item_type, item_id, action, reason_code, active, created_at
            from aevo_feed_negative_feedback_history
            where actor_id = @actor_id
              and app_code = 'GO'
            order by created_at desc, history_id desc
            limit @limit
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, FeedApiContract.MaxFeedbackHistoryLimit));

        var records = new List<FeedNegativeFeedbackHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new FeedNegativeFeedbackHistoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return records;
    }

    private static async Task AcquireFeedbackLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
