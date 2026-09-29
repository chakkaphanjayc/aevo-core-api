using Aevo.CoreApi.Feed;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record FeedEventIntakeRecord(
    string PrincipalBinding,
    Guid? ActorId,
    string EventId,
    string EventName,
    string FeedSessionId,
    string ItemToken,
    string ItemType,
    string ItemId,
    int? Position,
    string? Source,
    string SchemaVersion,
    DateTimeOffset OccurredAt,
    string ConfigVersion,
    string RankingVersion,
    string? ExperimentVariant,
    string MetadataJson,
    string EventHash,
    string Status = "ACCEPTED");

public sealed record FeedEventPersistenceResult(
    string EventId,
    string Status,
    string? ErrorCode = null);

public sealed record FeedEventConsumeResult(
    int Claimed,
    int Processed,
    int Duplicates,
    int Failed,
    int DeadLettered,
    string ConsumerName);

public sealed record FeedEventHealthData(
    long Pending,
    long Processing,
    long Failed,
    long DeadLetter,
    long AcceptedLast24Hours,
    long ProcessedLast24Hours,
    DateTimeOffset? LastProcessedAt,
    string ConsumerVersion);

public sealed partial class CoreDataStore
{
    private const int FeedEventMaxAttempts = 5;
    private const string FeedEventConsumerVersion = "feed-events-v1";

    public async Task<IReadOnlyList<FeedEventPersistenceResult>> PersistFeedEventsAsync(
        string principalBinding,
        Guid? actorId,
        IReadOnlyList<FeedEventIntakeRecord> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0) return Array.Empty<FeedEventPersistenceResult>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetFeedRequestContextAsync(connection, transaction, principalBinding, actorId, cancellationToken);

        var results = new List<FeedEventPersistenceResult>(events.Count);
        foreach (var item in events)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into aevo_feed_event_intake
                  (principal_binding, event_id, app_code, actor_id, schema_version, event_name,
                   feed_session_id, item_token, item_type, item_id, item_position, source,
                   config_version, ranking_version, experiment_variant, metadata, event_hash,
                   occurred_at, status)
                values
                  (@principal_binding, @event_id, 'GO', @actor_id, @schema_version, @event_name,
                   @feed_session_id, @item_token, @item_type, @item_id, @item_position, @source,
                   @config_version, @ranking_version, @experiment_variant, @metadata, @event_hash,
                   @occurred_at, @status)
                on conflict (principal_binding, event_id) do nothing
                returning event_id
                """, connection, transaction);
            AddFeedEventParameters(insert, principalBinding, item);
            var inserted = await insert.ExecuteScalarAsync(cancellationToken);
            if (inserted is string)
            {
                if (item.Status == "ACCEPTED")
                {
                    await using var outbox = new NpgsqlCommand(
                        "insert into aevo_feed_event_outbox (principal_binding, event_id) values (@principal_binding, @event_id)",
                        connection,
                        transaction);
                    outbox.Parameters.AddWithValue("principal_binding", principalBinding);
                    outbox.Parameters.AddWithValue("event_id", item.EventId);
                    await outbox.ExecuteNonQueryAsync(cancellationToken);
                }

                results.Add(new FeedEventPersistenceResult(item.EventId, item.Status));
                continue;
            }

            await using var existing = new NpgsqlCommand(
                "select event_hash from aevo_feed_event_intake where principal_binding = @principal_binding and event_id = @event_id",
                connection,
                transaction);
            existing.Parameters.AddWithValue("principal_binding", principalBinding);
            existing.Parameters.AddWithValue("event_id", item.EventId);
            var existingHash = await existing.ExecuteScalarAsync(cancellationToken) as string;
            if (existingHash is null)
            {
                throw new CoreDatabaseException("Feed event idempotency lookup returned no row.");
            }

            results.Add(string.Equals(existingHash, item.EventHash, StringComparison.Ordinal)
                ? new FeedEventPersistenceResult(item.EventId, "DUPLICATE")
                : new FeedEventPersistenceResult(item.EventId, "REJECTED", "EVENT_IDEMPOTENCY_CONFLICT"));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<FeedEventConsumeResult> ConsumeFeedEventsAsync(
        string consumerName,
        int batchSize,
        CancellationToken cancellationToken,
        string? eventId = null)
    {
        var normalizedConsumer = NormalizeConsumerName(consumerName);
        var boundedBatchSize = Math.Clamp(batchSize, 1, 100);
        var claimed = await ClaimFeedEventsAsync(normalizedConsumer, boundedBatchSize, eventId, cancellationToken);
        var processed = 0;
        var duplicates = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var item in claimed)
        {
            try
            {
                var duplicate = await ProcessFeedEventAsync(normalizedConsumer, item, cancellationToken);
                if (duplicate) duplicates++;
                else processed++;
            }
            catch (Exception)
            {
                var deadLetter = await MarkFeedEventFailedAsync(item.OutboxId, cancellationToken);
                if (deadLetter) deadLettered++;
                else failed++;
            }
        }

        return new FeedEventConsumeResult(
            claimed.Count,
            processed,
            duplicates,
            failed,
            deadLettered,
            normalizedConsumer);
    }

    public async Task<FeedEventHealthData> GetFeedEventHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              count(*) filter (where status = 'PENDING' and available_at <= now()),
              count(*) filter (where status = 'PROCESSING'),
              count(*) filter (where status = 'FAILED'),
              count(*) filter (where status = 'DEAD_LETTER'),
              (select count(*) from aevo_feed_event_intake where status = 'ACCEPTED' and received_at >= now() - interval '24 hours'),
              (select count(*) from aevo_feed_event_inbox where processed_at >= now() - interval '24 hours'),
              (select max(processed_at) from aevo_feed_event_inbox)
            from aevo_feed_event_outbox
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Feed event health returned no row.");

        return new FeedEventHealthData(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            FeedEventConsumerVersion);
    }

    public async Task<int> ReplayFeedEventDeadLettersAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with candidates as (
              select outbox_id
              from aevo_feed_event_outbox
              where status = 'DEAD_LETTER'
              order by updated_at, outbox_id
              limit @limit
              for update skip locked
            )
            update aevo_feed_event_outbox outbox
            set status = 'PENDING', available_at = now(), locked_until = null,
                last_error = null, updated_at = now()
            from candidates
            where outbox.outbox_id = candidates.outbox_id
            """, connection);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> PurgeFeedEventsAsync(int retentionDays, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(retentionDays, 7, 365);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var inbox = new NpgsqlCommand(
            """
            delete from aevo_feed_event_inbox inbox
            using aevo_feed_event_intake intake
            where inbox.principal_binding = intake.principal_binding
              and inbox.event_id = intake.event_id
              and intake.received_at < now() - make_interval(days => @retention_days)
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue("retention_days", days);
            await inbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var outbox = new NpgsqlCommand(
            """
            delete from aevo_feed_event_outbox outbox
            using aevo_feed_event_intake intake
            where outbox.principal_binding = intake.principal_binding
              and outbox.event_id = intake.event_id
              and intake.received_at < now() - make_interval(days => @retention_days)
              and outbox.status in ('PROCESSED', 'DEAD_LETTER')
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("retention_days", days);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var intake = new NpgsqlCommand(
            """
            delete from aevo_feed_event_intake intake
            where intake.received_at < now() - make_interval(days => @retention_days)
              and not exists (
                select 1 from aevo_feed_event_outbox outbox
                where outbox.principal_binding = intake.principal_binding
                  and outbox.event_id = intake.event_id
              )
            """, connection, transaction);
        intake.Parameters.AddWithValue("retention_days", days);
        var deleted = await intake.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<IReadOnlyList<FeedEventOutboxRow>> ClaimFeedEventsAsync(
        string consumerName,
        int batchSize,
        string? eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with candidates as (
              select outbox_id
              from aevo_feed_event_outbox
              where (status = 'PENDING' or status = 'FAILED')
                and available_at <= now()
                and (locked_until is null or locked_until < now())
                and attempts < @max_attempts
                and (@event_id is null or event_id = @event_id)
              order by available_at, created_at, outbox_id
              limit @limit
              for update skip locked
            )
            update aevo_feed_event_outbox outbox
            set status = 'PROCESSING', attempts = attempts + 1,
                locked_until = now() + make_interval(secs => @lock_seconds),
                updated_at = now()
            from candidates
            where outbox.outbox_id = candidates.outbox_id
            returning outbox.outbox_id, outbox.principal_binding, outbox.event_id
            """, connection);
        command.Parameters.AddWithValue("max_attempts", FeedEventMaxAttempts);
        command.Parameters.AddWithValue("limit", batchSize);
        command.Parameters.AddWithValue("lock_seconds", 60);
        command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = (object?)eventId ?? DBNull.Value });

        var rows = new List<FeedEventOutboxRow>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FeedEventOutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }
        return rows;
    }

    private async Task<bool> ProcessFeedEventAsync(
        string consumerName,
        FeedEventOutboxRow outbox,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var read = new NpgsqlCommand(
            """
            select intake.event_name, intake.item_type, intake.item_id, intake.occurred_at
            from aevo_feed_event_outbox outbox
            join aevo_feed_event_intake intake
              on intake.principal_binding = outbox.principal_binding
             and intake.event_id = outbox.event_id
            where outbox.outbox_id = @outbox_id
            for update of outbox
            """, connection, transaction);
        read.Parameters.AddWithValue("outbox_id", outbox.OutboxId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Feed event outbox row is missing.");
        var eventName = reader.GetString(0);
        var itemType = reader.GetString(1);
        var itemId = reader.GetString(2);
        var occurredAt = reader.GetFieldValue<DateTimeOffset>(3);
        await reader.DisposeAsync();

        await using var inbox = new NpgsqlCommand(
            """
            insert into aevo_feed_event_inbox
              (consumer_name, principal_binding, event_id, status)
            values
              (@consumer_name, @principal_binding, @event_id, 'PROCESSED')
            on conflict (consumer_name, principal_binding, event_id) do nothing
            """, connection, transaction);
        inbox.Parameters.AddWithValue("consumer_name", consumerName);
        inbox.Parameters.AddWithValue("principal_binding", outbox.PrincipalBinding);
        inbox.Parameters.AddWithValue("event_id", outbox.EventId);
        var inserted = await inbox.ExecuteNonQueryAsync(cancellationToken) == 1;

        if (inserted)
        {
            await using var stats = new NpgsqlCommand(
                """
                insert into aevo_feed_event_stats_daily
                  (bucket_date, item_type, item_id, event_name, event_count, last_occurred_at)
                values
                  ((@occurred_at at time zone 'utc')::date, @item_type, @item_id, @event_name, 1, @occurred_at)
                on conflict (bucket_date, item_type, item_id, event_name) do update
                set event_count = aevo_feed_event_stats_daily.event_count + 1,
                    last_occurred_at = greatest(aevo_feed_event_stats_daily.last_occurred_at, excluded.last_occurred_at),
                    updated_at = now()
                """, connection, transaction);
            stats.Parameters.AddWithValue("occurred_at", occurredAt);
            stats.Parameters.AddWithValue("item_type", itemType);
            stats.Parameters.AddWithValue("item_id", itemId);
            stats.Parameters.AddWithValue("event_name", eventName);
            await stats.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var complete = new NpgsqlCommand(
            """
            update aevo_feed_event_outbox
            set status = 'PROCESSED', locked_until = null, processed_at = now(), updated_at = now(), last_error = null
            where outbox_id = @outbox_id
            """, connection, transaction);
        complete.Parameters.AddWithValue("outbox_id", outbox.OutboxId);
        await complete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return !inserted;
    }

    private async Task<bool> MarkFeedEventFailedAsync(Guid outboxId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update aevo_feed_event_outbox
            set status = case when attempts >= @max_attempts then 'DEAD_LETTER' else 'FAILED' end,
                available_at = case when attempts >= @max_attempts then available_at else now() + interval '30 seconds' end,
                locked_until = null,
                last_error = 'CONSUMER_PROCESSING_FAILED',
                updated_at = now()
            where outbox_id = @outbox_id
            returning status
            """, connection);
        command.Parameters.AddWithValue("max_attempts", FeedEventMaxAttempts);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.Equals(status, "DEAD_LETTER", StringComparison.Ordinal);
    }

    private static async Task SetFeedRequestContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalBinding,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('aevo.app_code', 'GO', true), set_config('aevo.user_id', @user_id, true), set_config('aevo.feed_principal_binding', @principal_binding, true)",
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", actorId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("principal_binding", principalBinding);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFeedEventParameters(
        NpgsqlCommand command,
        string principalBinding,
        FeedEventIntakeRecord item)
    {
        command.Parameters.AddWithValue("principal_binding", principalBinding);
        command.Parameters.AddWithValue("event_id", item.EventId);
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Uuid) { Value = (object?)item.ActorId ?? DBNull.Value });
        command.Parameters.AddWithValue("schema_version", item.SchemaVersion);
        command.Parameters.AddWithValue("event_name", item.EventName);
        command.Parameters.AddWithValue("feed_session_id", item.FeedSessionId);
        command.Parameters.AddWithValue("item_token", item.ItemToken);
        command.Parameters.AddWithValue("item_type", item.ItemType);
        command.Parameters.AddWithValue("item_id", item.ItemId);
        command.Parameters.Add(new NpgsqlParameter("item_position", NpgsqlDbType.Integer) { Value = (object?)item.Position ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("source", NpgsqlDbType.Text) { Value = (object?)item.Source ?? DBNull.Value });
        command.Parameters.AddWithValue("config_version", item.ConfigVersion);
        command.Parameters.AddWithValue("ranking_version", item.RankingVersion);
        command.Parameters.Add(new NpgsqlParameter("experiment_variant", NpgsqlDbType.Text) { Value = (object?)item.ExperimentVariant ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = item.MetadataJson });
        command.Parameters.AddWithValue("event_hash", item.EventHash);
        command.Parameters.AddWithValue("occurred_at", item.OccurredAt);
        command.Parameters.AddWithValue("status", item.Status);
    }

    private static string NormalizeConsumerName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 80 || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The Feed event consumer name is invalid.", nameof(value));
        }
        return normalized;
    }

    private sealed record FeedEventOutboxRow(Guid OutboxId, string PrincipalBinding, string EventId);
}
