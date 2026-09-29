using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

internal enum FeedProjectionReadMode
{
    Off,
    Shadow,
    Active
}

internal static class FeedServingProjection
{
    public const string Name = "feed-item-metrics";
    public const string Version = "feed-item-metrics-v1";
    public const int DefaultMaxAgeSeconds = 300;

    public static FeedProjectionReadMode ResolveMode(IConfiguration configuration)
    {
        return configuration["AEVO_FEED_PROJECTION_MODE"]?.Trim().ToLowerInvariant() switch
        {
            "active" => FeedProjectionReadMode.Active,
            "shadow" => FeedProjectionReadMode.Shadow,
            _ => FeedProjectionReadMode.Off
        };
    }

    public static string ToParameter(FeedProjectionReadMode mode) => mode switch
    {
        FeedProjectionReadMode.Active => "active",
        FeedProjectionReadMode.Shadow => "shadow",
        _ => "off"
    };
}

internal sealed record FeedProjectionRebuildResult(
    Guid RunId,
    string ProjectionName,
    string ProjectionVersion,
    string Status,
    DateTimeOffset SourceCutoffAt,
    int RowsPublished,
    DateTimeOffset CompletedAt,
    FeedProjectionReadMode ReadMode);

internal sealed record FeedProjectionHealth(
    string ProjectionName,
    string? ActiveRunId,
    string? PreviousRunId,
    string? ProjectionVersion,
    string? Status,
    DateTimeOffset? SourceCutoffAt,
    DateTimeOffset? CompletedAt,
    int RowsPublished,
    int MaxAgeSeconds,
    bool Fresh,
    FeedProjectionReadMode ReadMode,
    string? LastErrorCode);

public sealed partial class FeedCanonicalDataStore
{
    internal FeedProjectionReadMode ProjectionReadMode { get; }

    internal async Task<FeedProjectionRebuildResult> RebuildFeedServingProjectionAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sourceCutoffAt = DateTimeOffset.UtcNow;
        var runId = Guid.Empty;

        try
        {
            await using (var start = new NpgsqlCommand(
                "insert into aevo_feed_projection_runs (projection_name, projection_version, source_cutoff_at, status) values (@projection_name, @projection_version, @source_cutoff_at, 'started') returning run_id",
                connection,
                transaction))
            {
                start.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
                start.Parameters.AddWithValue("projection_version", FeedServingProjection.Version);
                start.Parameters.AddWithValue("source_cutoff_at", sourceCutoffAt);
                runId = (Guid)(await start.ExecuteScalarAsync(cancellationToken)
                    ?? throw new FeedSourceDataException("FEED_PROJECTION_RUN_NOT_CREATED"));
            }

            await using (var insert = new NpgsqlCommand(
                """
                with latest_quality as (
                  select distinct on (entity_type, entity_id)
                    entity_type,
                    entity_id,
                    score,
                    confidence
                  from public.tracedee_content_quality_scores
                  order by entity_type, entity_id, score_version desc, calculated_at desc
                ),
                trace_stop_counts as (
                  select trace_id, count(*)::integer as stop_count
                  from public.tracedee_trace_stops
                  group by trace_id
                ),
                trace_save_counts as (
                  select trace_id, count(*)::bigint as save_count
                  from public.tracedee_trace_saves
                  group by trace_id
                ),
                trace_follow_counts as (
                  select trace_id, count(*)::bigint as follow_count
                  from public.tracedee_trace_follows
                  group by trace_id
                ),
                trace_completion_counts as (
                  select trace_id, count(*)::bigint as completion_count
                  from public.tracedee_completions
                  where verification_status <> 'REJECTED'
                  group by trace_id
                ),
                place_interactions as (
                  select
                    item_id,
                    count(*) filter (where interaction_type = 'OPENED')::bigint as open_count,
                    count(*) filter (where interaction_type = 'SAVED')::bigint as interaction_save_count,
                    count(*) filter (where interaction_type = 'SHARED')::bigint as share_count
                  from public.tracedee_feed_interactions
                  where item_type = 'PLACE'
                  group by item_id
                ),
                trace_items as (
                  select
                    t.id,
                    coalesce(stops.stop_count, 0) as stop_count,
                    coalesce(saves.save_count, 0) as save_count,
                    coalesce(follows.follow_count, 0) as follow_count,
                    coalesce(completions.completion_count, 0) as completion_count,
                    greatest(0::numeric, least(1::numeric, quality.score)) as quality_score,
                    quality.confidence as quality_confidence
                  from public.tracedee_traces t
                  left join trace_stop_counts stops on stops.trace_id = t.id
                  left join trace_save_counts saves on saves.trace_id = t.id
                  left join trace_follow_counts follows on follows.trace_id = t.id
                  left join trace_completion_counts completions on completions.trace_id = t.id
                  left join latest_quality quality
                    on quality.entity_type = 'TRACE'
                   and quality.entity_id = t.id
                  where coalesce(t.published_at, t.created_at) <= @source_cutoff_at
                ),
                place_items as (
                  select p.id as item_id
                  from public.tracedee_places p
                  where coalesce(p.created_at, timezone('utc', now())) <= @source_cutoff_at
                  union
                  select p.store_id as item_id
                  from public.customer_store_profiles p
                  where p.public_enabled = true
                    and coalesce(p.updated_at, p.created_at, timezone('utc', now())) <= @source_cutoff_at
                )
                insert into aevo_feed_item_metrics_projection (
                  run_id,
                  item_type,
                  item_id,
                  stop_count,
                  save_count,
                  follow_count,
                  completion_count,
                  open_count,
                  interaction_save_count,
                  share_count,
                  quality_score,
                  quality_confidence,
                  source_cutoff_at,
                  generated_at,
                  freshness_state
                )
                select
                  @run_id,
                  'TRACE',
                  trace.id,
                  trace.stop_count,
                  trace.save_count,
                  trace.follow_count,
                  trace.completion_count,
                  0,
                  0,
                  0,
                  trace.quality_score,
                  trace.quality_confidence,
                  @source_cutoff_at,
                  now(),
                  'fresh'
                from trace_items trace
                union all
                select
                  @run_id,
                  'PLACE',
                  place.item_id,
                  0,
                  0,
                  0,
                  0,
                  coalesce(interactions.open_count, 0),
                  coalesce(interactions.interaction_save_count, 0),
                  coalesce(interactions.share_count, 0),
                  greatest(0::numeric, least(1::numeric, quality.score)),
                  quality.confidence,
                  @source_cutoff_at,
                  now(),
                  'fresh'
                from place_items place
                left join place_interactions interactions on interactions.item_id = place.item_id
                left join latest_quality quality
                  on quality.entity_type = 'PLACE'
                 and quality.entity_id = place.item_id
                """,
                connection,
                transaction))
            {
                insert.Parameters.AddWithValue("run_id", runId);
                insert.Parameters.AddWithValue("source_cutoff_at", sourceCutoffAt);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            int rowsPublished;
            await using (var count = new NpgsqlCommand(
                "select count(*)::integer from aevo_feed_item_metrics_projection where run_id = @run_id",
                connection,
                transaction))
            {
                count.Parameters.AddWithValue("run_id", runId);
                rowsPublished = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            }

            await using (var complete = new NpgsqlCommand(
                "update aevo_feed_projection_runs set status = 'completed', rows_seen = @rows, rows_published = @rows, completed_at = now() where run_id = @run_id",
                connection,
                transaction))
            {
                complete.Parameters.AddWithValue("rows", rowsPublished);
                complete.Parameters.AddWithValue("run_id", runId);
                await complete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var activate = new NpgsqlCommand(
                """
                insert into aevo_feed_projection_control (
                  projection_name,
                  active_run_id,
                  previous_run_id,
                  max_age_seconds,
                  last_error_code,
                  last_error_message,
                  updated_at
                )
                values (
                  @projection_name,
                  @run_id,
                  (select active_run_id from aevo_feed_projection_control where projection_name = @projection_name),
                  @max_age_seconds,
                  null,
                  null,
                  now()
                )
                on conflict (projection_name) do update
                set previous_run_id = aevo_feed_projection_control.active_run_id,
                    active_run_id = excluded.active_run_id,
                    max_age_seconds = excluded.max_age_seconds,
                    last_error_code = null,
                    last_error_message = null,
                    updated_at = now()
                """,
                connection,
                transaction))
            {
                activate.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
                activate.Parameters.AddWithValue("run_id", runId);
                activate.Parameters.AddWithValue("max_age_seconds", FeedServingProjection.DefaultMaxAgeSeconds);
                await activate.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new FeedProjectionRebuildResult(
                runId,
                FeedServingProjection.Name,
                FeedServingProjection.Version,
                "completed",
                sourceCutoffAt,
                rowsPublished,
                DateTimeOffset.UtcNow,
                ProjectionReadMode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (runId != Guid.Empty)
            {
                await MarkProjectionRunFailedAsync(runId, sourceCutoffAt, cancellationToken);
            }
            throw new FeedSourceDataException("FEED_PROJECTION_REBUILD_FAILED", error);
        }
    }

    internal async Task<FeedProjectionHealth?> GetFeedProjectionHealthAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              control.projection_name,
              control.active_run_id,
              control.previous_run_id,
              run.projection_version,
              run.status,
              run.source_cutoff_at,
              run.completed_at,
              coalesce(run.rows_published, 0),
              control.max_age_seconds,
              coalesce(run.completed_at >= now() - make_interval(secs => control.max_age_seconds), false),
              control.last_error_code
            from aevo_feed_projection_control control
            left join aevo_feed_projection_runs run on run.run_id = control.active_run_id
            where control.projection_name = @projection_name
            """,
            connection);
        command.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new FeedProjectionHealth(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1).ToString("N"),
            reader.IsDBNull(2) ? null : reader.GetGuid(2).ToString("N"),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            ProjectionReadMode,
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    internal async Task<bool> RollbackFeedServingProjectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? activeRun;
        Guid? previousRun;
        await using (var read = new NpgsqlCommand(
            "select active_run_id, previous_run_id from aevo_feed_projection_control where projection_name = @projection_name for update",
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            activeRun = reader.IsDBNull(0) ? null : reader.GetGuid(0);
            previousRun = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }

        if (activeRun is null || previousRun is null) return false;

        await using (var mark = new NpgsqlCommand(
            "update aevo_feed_projection_runs set status = 'rolled_back' where run_id = @run_id and status = 'completed'",
            connection,
            transaction))
        {
            mark.Parameters.AddWithValue("run_id", activeRun.Value);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand(
            "update aevo_feed_projection_control set active_run_id = @previous_run_id, previous_run_id = @active_run_id, last_error_code = null, last_error_message = null, updated_at = now() where projection_name = @projection_name",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("previous_run_id", previousRun.Value);
            update.Parameters.AddWithValue("active_run_id", activeRun.Value);
            update.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task MarkProjectionRunFailedAsync(Guid runId, DateTimeOffset sourceCutoffAt, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                insert into aevo_feed_projection_runs (
                  run_id,
                  projection_name,
                  projection_version,
                  source_cutoff_at,
                  status,
                  error_code,
                  error_message,
                  completed_at
                )
                values (
                  @run_id,
                  @projection_name,
                  @projection_version,
                  @source_cutoff_at,
                  'failed',
                  'FEED_PROJECTION_REBUILD_FAILED',
                  'Feed projection rebuild failed.',
                  now()
                )
                on conflict (run_id) do update
                set status = 'failed',
                    error_code = 'FEED_PROJECTION_REBUILD_FAILED',
                    error_message = 'Feed projection rebuild failed.',
                    completed_at = now()
                """,
                connection);
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
            command.Parameters.AddWithValue("projection_version", FeedServingProjection.Version);
            command.Parameters.AddWithValue("source_cutoff_at", sourceCutoffAt);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var control = new NpgsqlCommand(
                "update aevo_feed_projection_control set last_error_code = 'FEED_PROJECTION_REBUILD_FAILED', last_error_message = 'Feed projection rebuild failed.', updated_at = now() where projection_name = @projection_name",
                connection);
            control.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
            await control.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original rebuild failure; the next health read can
            // still observe the previous active run.
        }
    }
}
