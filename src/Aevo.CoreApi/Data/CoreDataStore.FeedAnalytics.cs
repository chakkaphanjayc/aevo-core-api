using Npgsql;

namespace Aevo.CoreApi.Data;

public sealed record FeedDiscoveryEvaluationData(
    long Impressions,
    long Opens,
    long PlaceOpens,
    long Saves,
    long TraceStarts,
    long TraceCompletes,
    long BookingClicks,
    long CurrentActiveHides,
    long HideTransitions,
    long EventDeadLetters);

public sealed partial class CoreDataStore
{
    public async Task<FeedDiscoveryEvaluationData> GetFeedDiscoveryEvaluationAsync(
        int requestedDays,
        CancellationToken cancellationToken)
    {
        var days = Math.Clamp(requestedDays, 1, 90);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with event_counts as (
              select
                count(*) filter (where event_name = 'impression') as impressions,
                count(*) filter (where event_name = 'open') as opens,
                count(*) filter (where event_name = 'place_open') as place_opens,
                count(*) filter (where event_name = 'save') as saves,
                count(*) filter (where event_name = 'trace_start') as trace_starts,
                count(*) filter (where event_name = 'trace_complete') as trace_completes,
                count(*) filter (where event_name = 'booking_click') as booking_clicks
              from aevo_feed_event_intake
              where status = 'ACCEPTED'
                and occurred_at >= now() - make_interval(days => @days)
            )
            select
              event_counts.impressions,
              event_counts.opens,
              event_counts.place_opens,
              event_counts.saves,
              event_counts.trace_starts,
              event_counts.trace_completes,
              event_counts.booking_clicks,
              (select count(*) from aevo_feed_negative_feedback where app_code = 'GO' and active = true),
              (select count(*) from aevo_feed_negative_feedback_history
                where app_code = 'GO'
                  and created_at >= now() - make_interval(days => @days)),
              (select count(*) from aevo_feed_event_outbox where status = 'DEAD_LETTER')
            from event_counts
            """, connection);
        command.Parameters.AddWithValue("days", days);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CoreDatabaseException("Feed discovery evaluation returned no row.");
        }

        return new FeedDiscoveryEvaluationData(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));
    }
}
