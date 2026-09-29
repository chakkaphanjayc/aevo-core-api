using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

internal sealed class FeedSourceDataException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

internal sealed record TraceCandidateRow(
    Guid Id,
    DateTimeOffset PublishedAt,
    FeedCandidateFeatureInput Features);

internal sealed record PlaceCandidateRow(
    Guid EntityId,
    Guid HydrationId,
    string Source,
    int SourcePriority,
    string CanonicalIdentity,
    DateTimeOffset PublishedAt,
    FeedCandidateFeatureInput Features);

internal sealed record TraceHydrationRow(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string CreatorName,
    string Area,
    IReadOnlyList<string> TopicTags,
    int StopCount,
    long SaveCount,
    long FollowCount,
    DateTimeOffset PublishedAt);

internal sealed record TraceDeePlaceHydrationRow(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Area,
    string Category,
    string? ImageUrl);

internal sealed record StoreProfileHydrationRow(
    Guid StoreId,
    string Slug,
    string Name,
    string Description,
    string Area,
    string Category,
    string? ImageUrl);

internal sealed record FeedPlaceQuery(
    string Tab,
    string? Query,
    string? Area,
    bool Nearby,
    bool SpatialNearby,
    bool AreaNearbyFallback,
    double? Latitude,
    double? Longitude,
    int RadiusMeters,
    DateTimeOffset CandidateCutoffAt,
    int Limit,
    Guid? ActorId = null,
    string? Vibe = null,
    string? Category = null);

internal sealed record FeedSourceSnapshot(
    string Source,
    int EligibleCount,
    DateTimeOffset? LatestEligibleAt);

/// <summary>
/// Server-only read adapter for the existing TraceDee/public discovery
/// PostgreSQL data plane. It is intentionally not exposed through an HTTP or
/// Supabase client boundary.
/// </summary>
public sealed partial class FeedCanonicalDataStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource? dataSource;
    private readonly string? configurationError;

    public FeedCanonicalDataStore(IConfiguration configuration)
    {
        ProjectionReadMode = FeedServingProjection.ResolveMode(configuration);
        SourceFreshnessWindowSeconds = ReadBoundedInt(
            configuration["AEVO_FEED_SOURCE_FRESHNESS_SECONDS"],
            FeedSourceHealthPolicy.DefaultFreshnessWindowSeconds,
            FeedSourceHealthPolicy.MinimumFreshnessWindowSeconds,
            FeedSourceHealthPolicy.MaximumFreshnessWindowSeconds);
        var rawConnectionString = configuration["AEVO_DATABASE_URL"]?.Trim();
        if (string.IsNullOrWhiteSpace(rawConnectionString)) return;

        try
        {
            dataSource = NpgsqlDataSource.Create(NormalizeDatabaseConnectionString(rawConnectionString));
        }
        catch (Exception)
        {
            configurationError = "The Feed canonical data connection is invalid.";
        }
    }

    public bool IsConfigured => dataSource is not null;

    internal int SourceFreshnessWindowSeconds { get; }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select 1", connection)
        {
            CommandTimeout = 2
        };
        await command.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    internal async Task<IReadOnlyList<FeedSourceSnapshot>> ReadFeedSourceHealthAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              'tracedee-trace'::text as source,
              count(*)::integer as eligible_count,
              max(coalesce(t.published_at, t.created_at)) as latest_eligible_at
            from public.tracedee_traces t
            join public.user_profiles creator
              on creator.id = t.creator_id
             and creator.status = 'ACTIVE'
            where t.status = 'PUBLISHED'
              and t.visibility = 'PUBLIC'
              and t.moderation_status in ('VISIBLE', 'LIMITED')
            union all
            select
              'tracedee-place'::text,
              count(*)::integer,
              max(p.created_at)
            from public.tracedee_places p
            left join public.stores s on s.id = p.store_id
            left join public.organizations o on o.id = p.organization_id
            where p.moderation_status in ('VISIBLE', 'LIMITED')
              and length(trim(p.category)) > 0
              and (
                p.store_id is null
                or (s.status = 'ACTIVE' and o.status = 'ACTIVE')
              )
            union all
            select
              'store-profile'::text,
              count(*)::integer,
              max(coalesce(p.updated_at, p.created_at))
            from public.customer_store_profiles p
            join public.stores s
              on s.id = p.store_id
             and s.organization_id = p.organization_id
             and s.status = 'ACTIVE'
            join public.organizations o
              on o.id = p.organization_id
             and o.status = 'ACTIVE'
            where p.public_enabled = true
              and length(trim(p.category)) > 0
            order by source
            """,
            connection)
        {
            CommandTimeout = 2
        };

        var result = new List<FeedSourceSnapshot>(3);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FeedSourceSnapshot(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    internal async Task<IReadOnlyList<TraceCandidateRow>> ReadTraceCandidatesAsync(
        FeedSessionContext session,
        string? query,
        string? area,
        string? vibe,
        bool following,
        int limit,
        CancellationToken cancellationToken,
        string? category = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              t.id,
              coalesce(t.published_at, t.created_at) as published_at,
              t.creator_id,
              t.area,
              t.topic_tags,
              coalesce(
                case when @projection_mode = 'active' then metrics.quality_score end,
                quality.score,
                0.5
              )::double precision as quality_score,
              coalesce(
                case when @projection_mode = 'active' then metrics.quality_confidence end,
                quality.confidence,
                0
              )::double precision as quality_confidence,
              case when @projection_mode = 'active' and metrics.item_id is not null
                then coalesce(metrics.save_count, 0) + coalesce(metrics.follow_count, 0) + coalesce(metrics.completion_count, 0)
                else (
                  coalesce((select count(*) from public.tracedee_trace_saves saves where saves.trace_id = t.id), 0)
                  + coalesce((select count(*) from public.tracedee_trace_follows follows where follows.trace_id = t.id), 0)
                  + coalesce((select count(*) from public.tracedee_completions completions
                              where completions.trace_id = t.id
                                and completions.verification_status <> 'REJECTED'), 0)
                )
              end::double precision as popularity_signal,
              case when @actor_id is not null and exists (
                select 1
                from public.tracedee_tracer_follows followed
                where followed.follower_id = @actor_id
                  and followed.tracer_id = t.creator_id
              ) then 1 else 0 end::double precision as relationship_signal,
              coalesce(taste.affinity_signal, 0)::double precision as affinity_signal,
              0.5::double precision as geography_signal,
              coalesce(penalties.penalty_signal, 0)::double precision as penalty_signal,
              coalesce(t.root_trace_id, t.source_trace_id) as lineage_id,
              case when @projection_mode = 'active' and metrics.item_id is not null
                then metrics.quality_score is null
                else quality.entity_id is null
              end as is_exploration_candidate
              ,coalesce(taste.evidence_count, 0)::integer as affinity_evidence_count
              ,taste.calculated_at as affinity_calculated_at
            from public.tracedee_traces t
            join public.user_profiles creator
              on creator.id = t.creator_id
             and creator.status = 'ACTIVE'
            left join aevo_feed_projection_control projection_control
              on projection_control.projection_name = 'feed-item-metrics'
             and @projection_mode in ('active', 'shadow')
            left join aevo_feed_item_metrics_projection metrics
              on metrics.run_id = projection_control.active_run_id
             and metrics.item_type = 'TRACE'
             and metrics.item_id = t.id
             and metrics.freshness_state = 'fresh'
             and metrics.generated_at >= now() - make_interval(secs => projection_control.max_age_seconds)
            left join lateral (
              select
                q.entity_id,
                q.score,
                q.confidence
              from public.tracedee_content_quality_scores q
              where q.entity_type = 'TRACE'
                and q.entity_id = t.id
              order by q.score_version desc
              limit 1
            ) quality on @projection_mode <> 'active' or metrics.item_id is null
            left join lateral (
              select
                coalesce(sum(latest.affinity), 0)::double precision as affinity_signal,
                coalesce(sum(greatest(latest.evidence_count, 0)), 0)::integer as evidence_count,
                max(latest.calculated_at) as calculated_at
              from (
                select distinct on (ta.dimension_type, ta.dimension_key)
                  ta.affinity,
                  ta.evidence_count,
                  ta.calculated_at
                from public.tracedee_taste_affinities ta
                where ta.profile_id = @actor_id
                  and ta.calculated_at <= @candidate_cutoff_at
                  and (
                    (ta.dimension_type = 'AREA' and lower(ta.dimension_key) = lower(t.area))
                    or (ta.dimension_type = 'TOPIC' and exists (
                      select 1
                      from unnest(t.topic_tags) topic
                      where lower(topic) = lower(ta.dimension_key)
                    ))
                  )
                order by ta.dimension_type, ta.dimension_key, ta.score_version desc
              ) latest
            ) taste on true
            left join lateral (
              select count(*) filter (where interaction_type = 'DISMISSED')::double precision as penalty_signal
              from public.tracedee_feed_interactions interactions
              where interactions.actor_id = @actor_id
                and interactions.item_type = 'TRACE'
                and interactions.item_id = t.id
            ) penalties on true
            where t.status = 'PUBLISHED'
              and t.visibility = 'PUBLIC'
              and t.moderation_status in ('VISIBLE', 'LIMITED')
              and coalesce(t.published_at, t.created_at) <= @candidate_cutoff_at
              and (@query is null or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%' || @query || '%')
              and (@area is null or lower(t.area) = @area)
            and (
              @category is null
              or position(lower(@category) in lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' ')))) > 0
            )
              and (
                @vibe is null
                or @vibe = 'match'
                or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%' || @vibe || '%'
                or (@vibe = 'slow-bar' and (
                  lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%กาแฟ%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%coffee%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%cafe%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%slow%'))
                or (@vibe = 'quiet' and (
                  lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%เงียบ%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%quiet%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%wellness%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%พักใจ%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%slow%'))
                or (@vibe = 'art' and (
                  lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%ศิลป%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%งานออกแบบ%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%ถ่ายรูป%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%gallery%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%art%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%design%'))
                or (@vibe = 'work' and (
                  lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%กาแฟ%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%work%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%studio%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%design%'))
                or (@vibe = 'speakeasy' and (
                  lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%bar%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%บาร์%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%dining%'
                  or lower(concat_ws(' ', t.title, t.description, t.area, array_to_string(t.topic_tags, ' '))) like '%มื้อเย็น%'))
              )
              and (
                @following = false
                or (
                  @actor_id is not null
                  and exists (
                    select 1
                    from public.tracedee_tracer_follows followed
                    where followed.follower_id = @actor_id
                      and followed.tracer_id = t.creator_id
                  )
                )
              )
              and (
                @actor_id is null
                or not exists (
                  select 1
                  from public.tracedee_user_blocks blocked
                  where blocked.blocker_id = @actor_id
                    and blocked.blocked_id = t.creator_id
                )
                and not exists (
                  select 1
                  from public.tracedee_user_mutes muted
                  where muted.muter_id = @actor_id
                    and muted.muted_id = t.creator_id
                )
              )
              and (
                @actor_id is null
                or not exists (
                  select 1
                  from aevo_feed_negative_feedback feedback
                  where feedback.actor_id = @actor_id
                    and feedback.app_code = 'GO'
                    and feedback.active = true
                    and feedback.item_type = 'TRACE'
                    and feedback.item_id = t.id::text
                )
              )
            order by coalesce(t.published_at, t.created_at) desc, t.id desc
            limit @limit
            """,
            connection)
        {
            CommandTimeout = 2
        };
        AddNullableText(command, "query", query);
        AddNullableText(command, "area", area);
        AddNullableText(command, "vibe", vibe);
        AddNullableText(command, "category", category);
        AddNullableGuid(command, "actor_id", session.UserId);
        command.Parameters.Add("following", NpgsqlDbType.Boolean).Value = following;
        command.Parameters.Add("candidate_cutoff_at", NpgsqlDbType.TimestampTz).Value = session.CandidateCutoffAt;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;
        command.Parameters.Add("projection_mode", NpgsqlDbType.Text).Value = FeedServingProjection.ToParameter(ProjectionReadMode);

        var result = new List<TraceCandidateRow>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TraceCandidateRow(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                new FeedCandidateFeatureInput(
                    reader.GetGuid(2).ToString("N"),
                    reader.GetString(3),
                    reader.GetFieldValue<string[]>(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9),
                    reader.GetDouble(10),
                    reader.GetDouble(11),
                    reader.IsDBNull(12) ? null : reader.GetGuid(12).ToString("N"),
                    reader.GetBoolean(13),
                    reader.GetInt32(14),
                    reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15))));
        }
        return result;
    }

    internal async Task<IReadOnlyList<PlaceCandidateRow>> ReadPlaceCandidatesAsync(
        FeedPlaceQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with candidates as (
              select
                coalesce(p.store_id, p.id) as entity_id,
                p.id as hydration_id,
                'tracedee-place'::text as source,
                case when p.store_id is null then 10 else 20 end as source_priority,
                case
                  when p.store_id is null then 'place:' || p.id::text
                  else 'store:' || p.store_id::text
                end as canonical_identity,
                p.slug,
                p.name,
                p.description,
                p.area,
                p.category,
                p.image_url,
                p.location,
                coalesce(p.created_at, timezone('utc', now())) as published_at
              from public.tracedee_places p
              left join public.stores s
                on s.id = p.store_id
              left join public.organizations o
                on o.id = p.organization_id
              where p.moderation_status in ('VISIBLE', 'LIMITED')
                and length(trim(p.category)) > 0
                and coalesce(p.created_at, timezone('utc', now())) <= @candidate_cutoff_at
                and (
                  p.store_id is null
                  or (s.status = 'ACTIVE' and o.status = 'ACTIVE')
                )
                and (@query is null or lower(concat_ws(' ', p.name, p.area, p.category, p.description)) like '%' || @query || '%')
                and (@area is null or lower(p.area) = @area)
              union all
              select
                p.store_id as entity_id,
                p.store_id as hydration_id,
                'store-profile'::text as source,
                30 as source_priority,
                'store:' || p.store_id::text as canonical_identity,
                p.public_slug as slug,
                s.name,
                coalesce(p.description, ''),
                p.area,
                p.category,
                p.image_url,
                p.location,
                coalesce(p.updated_at, p.created_at, timezone('utc', now())) as published_at
              from public.customer_store_profiles p
              join public.stores s
                on s.id = p.store_id
               and s.organization_id = p.organization_id
               and s.status = 'ACTIVE'
              join public.organizations o
                on o.id = p.organization_id
               and o.status = 'ACTIVE'
              where p.public_enabled = true
                and length(trim(p.category)) > 0
                and coalesce(p.updated_at, p.created_at, timezone('utc', now())) <= @candidate_cutoff_at
                and (@query is null or lower(concat_ws(' ', s.name, p.area, p.category, p.description)) like '%' || @query || '%')
                and (@area is null or lower(p.area) = @area)
            )
            select
              c.entity_id,
              c.hydration_id,
              c.source,
              c.source_priority,
              c.canonical_identity,
              c.published_at,
              c.area,
              c.category,
              coalesce(
                case when @projection_mode = 'active' then entity_metrics.quality_score end,
                case when @projection_mode = 'active' then hydration_metrics.quality_score end,
                quality.score,
                0.5
              )::double precision as quality_score,
              coalesce(
                case when @projection_mode = 'active' then entity_metrics.quality_confidence end,
                case when @projection_mode = 'active' then hydration_metrics.quality_confidence end,
                quality.confidence,
                0
              )::double precision as quality_confidence,
              case when @projection_mode = 'active'
                    and coalesce(entity_metrics.item_id, hydration_metrics.item_id) is not null
                then coalesce(entity_metrics.open_count, 0)
                   + coalesce(entity_metrics.interaction_save_count, 0)
                   + coalesce(entity_metrics.share_count, 0)
                   + case when c.hydration_id <> c.entity_id then coalesce(hydration_metrics.open_count, 0)
                                                                  + coalesce(hydration_metrics.interaction_save_count, 0)
                                                                  + coalesce(hydration_metrics.share_count, 0)
                          else 0 end
                else coalesce(popularity.popularity_signal, 0)
              end::double precision as popularity_signal,
              0::double precision as relationship_signal,
              coalesce(taste.affinity_signal, 0)::double precision as affinity_signal,
              case
                when @nearby = true then 1
                when @area is not null and lower(c.area) = @area then 0.75
                else 0.5
              end::double precision as geography_signal,
              coalesce(penalties.penalty_signal, 0)::double precision as penalty_signal,
              null::uuid as lineage_id,
              case when @projection_mode = 'active' and coalesce(entity_metrics.item_id, hydration_metrics.item_id) is not null
                then coalesce(entity_metrics.quality_score, hydration_metrics.quality_score) is null
                else quality.entity_id is null
              end as is_exploration_candidate
              ,coalesce(taste.evidence_count, 0)::integer as affinity_evidence_count
              ,taste.calculated_at as affinity_calculated_at
            from candidates c
            left join aevo_feed_projection_control projection_control
              on projection_control.projection_name = 'feed-item-metrics'
             and @projection_mode in ('active', 'shadow')
            left join aevo_feed_item_metrics_projection entity_metrics
              on entity_metrics.run_id = projection_control.active_run_id
             and entity_metrics.item_type = 'PLACE'
             and entity_metrics.item_id = c.entity_id
             and entity_metrics.freshness_state = 'fresh'
             and entity_metrics.generated_at >= now() - make_interval(secs => projection_control.max_age_seconds)
            left join aevo_feed_item_metrics_projection hydration_metrics
              on hydration_metrics.run_id = projection_control.active_run_id
             and hydration_metrics.item_type = 'PLACE'
             and hydration_metrics.item_id = c.hydration_id
             and c.hydration_id <> c.entity_id
             and hydration_metrics.freshness_state = 'fresh'
             and hydration_metrics.generated_at >= now() - make_interval(secs => projection_control.max_age_seconds)
            left join lateral (
              select
                q.entity_id,
                q.score,
                q.confidence
              from public.tracedee_content_quality_scores q
              where q.entity_type = 'PLACE'
                and q.entity_id in (c.hydration_id, c.entity_id)
              order by
                case when q.entity_id = c.entity_id then 0 else 1 end,
                q.score_version desc
              limit 1
            ) quality on @projection_mode <> 'active' or coalesce(entity_metrics.item_id, hydration_metrics.item_id) is null
            left join lateral (
              select count(*)::double precision as popularity_signal
              from public.tracedee_feed_interactions interactions
              where interactions.item_type = 'PLACE'
                and interactions.item_id in (c.entity_id, c.hydration_id)
                and interactions.interaction_type in ('OPENED', 'SAVED', 'SHARED')
            ) popularity on @projection_mode <> 'active' or coalesce(entity_metrics.item_id, hydration_metrics.item_id) is null
            left join lateral (
              select
                coalesce(sum(latest.affinity), 0)::double precision as affinity_signal,
                coalesce(sum(greatest(latest.evidence_count, 0)), 0)::integer as evidence_count,
                max(latest.calculated_at) as calculated_at
              from (
                select distinct on (ta.dimension_type, ta.dimension_key)
                  ta.affinity,
                  ta.evidence_count,
                  ta.calculated_at
                from public.tracedee_taste_affinities ta
                where ta.profile_id = @actor_id
                  and ta.calculated_at <= @candidate_cutoff_at
                  and (
                    (ta.dimension_type = 'AREA' and lower(ta.dimension_key) = lower(c.area))
                    or (ta.dimension_type = 'CATEGORY' and lower(ta.dimension_key) = lower(c.category))
                  )
                order by ta.dimension_type, ta.dimension_key, ta.score_version desc
              ) latest
            ) taste on true
            left join lateral (
              select count(*) filter (where interaction_type = 'DISMISSED')::double precision as penalty_signal
              from public.tracedee_feed_interactions interactions
              where interactions.actor_id = @actor_id
                and interactions.item_type = 'PLACE'
                and interactions.item_id in (c.entity_id, c.hydration_id)
            ) penalties on true
            where (
              @nearby = false
              or (
                @spatial_nearby = true
                and c.location is not null
                and st_dwithin(
                  c.location,
                  st_setsrid(st_makepoint(@longitude, @latitude), 4326)::public.geography,
                  @radius_meters
                )
              )
              or (
                @area_nearby_fallback = true
                and @area is not null
                and lower(c.area) = @area
              )
            )
            and (@category is null or lower(c.category) = @category)
            and (
              @vibe is null
              or @vibe = 'match'
              or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%' || @vibe || '%'
              or (@vibe = 'slow-bar' and (
                lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%กาแฟ%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%coffee%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%cafe%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%slow%'))
              or (@vibe = 'quiet' and (
                lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%เงียบ%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%quiet%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%wellness%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%พักใจ%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%slow%'))
              or (@vibe = 'art' and (
                lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%ศิลป%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%งานออกแบบ%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%ถ่ายรูป%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%gallery%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%art%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%design%'))
              or (@vibe = 'work' and (
                lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%กาแฟ%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%work%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%studio%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%design%'))
              or (@vibe = 'speakeasy' and (
                lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%bar%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%บาร์%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%dining%'
                or lower(concat_ws(' ', c.name, c.area, c.category, c.description)) like '%มื้อเย็น%'))
            )
            and (
              @actor_id is null
              or not exists (
                select 1
                from aevo_feed_negative_feedback feedback
                where feedback.actor_id = @actor_id
                  and feedback.app_code = 'GO'
                  and feedback.active = true
                  and feedback.item_type = 'PLACE'
                  and feedback.item_id = c.entity_id::text
              )
            )
            order by c.published_at desc, c.source_priority desc, c.entity_id asc
            limit @limit
            """,
            connection)
        {
            CommandTimeout = 2
        };
        AddNullableText(command, "query", query.Query);
        AddNullableText(command, "area", query.Area);
        AddNullableText(command, "vibe", query.Vibe);
        AddNullableText(command, "category", query.Category);
        AddNullableGuid(command, "actor_id", query.ActorId);
        command.Parameters.Add("candidate_cutoff_at", NpgsqlDbType.TimestampTz).Value = query.CandidateCutoffAt;
        command.Parameters.Add("nearby", NpgsqlDbType.Boolean).Value = query.Nearby;
        command.Parameters.Add("spatial_nearby", NpgsqlDbType.Boolean).Value = query.SpatialNearby;
        command.Parameters.Add("area_nearby_fallback", NpgsqlDbType.Boolean).Value = query.AreaNearbyFallback;
        command.Parameters.Add("latitude", NpgsqlDbType.Double).Value = (object?)query.Latitude ?? DBNull.Value;
        command.Parameters.Add("longitude", NpgsqlDbType.Double).Value = (object?)query.Longitude ?? DBNull.Value;
        command.Parameters.Add("radius_meters", NpgsqlDbType.Integer).Value = query.RadiusMeters;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = query.Limit;
        command.Parameters.Add("projection_mode", NpgsqlDbType.Text).Value = FeedServingProjection.ToParameter(ProjectionReadMode);

        var result = new List<PlaceCandidateRow>(query.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PlaceCandidateRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                new FeedCandidateFeatureInput(
                    CreatorKey: reader.GetGuid(0).ToString("N"),
                    Area: reader.GetString(6),
                    TopicTags: new[] { reader.GetString(7) },
                    QualityScore: reader.GetDouble(8),
                    QualityConfidence: reader.GetDouble(9),
                    PopularitySignal: reader.GetDouble(10),
                    RelationshipSignal: reader.GetDouble(11),
                    AffinitySignal: reader.GetDouble(12),
                    GeographySignal: reader.GetDouble(13),
                    PenaltySignal: reader.GetDouble(14),
                    LineageKey: null,
                    IsExplorationCandidate: reader.GetBoolean(16),
                    AffinityEvidenceCount: reader.GetInt32(17),
                    AffinityCalculatedAt: reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
                    CategoryKey: reader.GetString(7),
                    BusinessKey: reader.GetString(4))));
        }
        return result;
    }

    internal async Task<IReadOnlyList<TraceHydrationRow>> HydrateTracesAsync(
        IReadOnlyCollection<Guid> ids,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Array.Empty<TraceHydrationRow>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with stop_counts as (
              select trace_id, count(*)::integer as stop_count
              from public.tracedee_trace_stops
              where trace_id = any(@ids)
              group by trace_id
            ),
            save_counts as (
              select trace_id, count(*)::bigint as save_count
              from public.tracedee_trace_saves
              where trace_id = any(@ids)
              group by trace_id
            ),
            follow_counts as (
              select trace_id, count(*)::bigint as follow_count
              from public.tracedee_trace_follows
              where trace_id = any(@ids)
              group by trace_id
            )
            select
              t.id,
              t.slug,
              t.title,
              t.description,
              coalesce(nullif(creator.display_name, ''), 'Aevo member') as creator_name,
              t.area,
              t.topic_tags,
              coalesce(stop_counts.stop_count, 0),
              coalesce(save_counts.save_count, 0),
              coalesce(follow_counts.follow_count, 0),
              coalesce(t.published_at, t.created_at) as published_at
            from public.tracedee_traces t
            join public.user_profiles creator
              on creator.id = t.creator_id
             and creator.status = 'ACTIVE'
            left join stop_counts on stop_counts.trace_id = t.id
            left join save_counts on save_counts.trace_id = t.id
            left join follow_counts on follow_counts.trace_id = t.id
            where t.id = any(@ids)
              and t.status = 'PUBLISHED'
              and t.visibility = 'PUBLIC'
              and t.moderation_status in ('VISIBLE', 'LIMITED')
              and coalesce(t.published_at, t.created_at) <= @candidate_cutoff_at
              and (
                @actor_id is null
                or not exists (
                  select 1 from public.tracedee_user_blocks blocked
                  where blocked.blocker_id = @actor_id
                    and blocked.blocked_id = t.creator_id
                )
                and not exists (
                  select 1 from public.tracedee_user_mutes muted
                  where muted.muter_id = @actor_id
                    and muted.muted_id = t.creator_id
                )
              )
              and (
                @actor_id is null
                or not exists (
                  select 1 from aevo_feed_negative_feedback feedback
                  where feedback.actor_id = @actor_id
                    and feedback.app_code = 'GO'
                    and feedback.active = true
                    and feedback.item_type = 'TRACE'
                    and feedback.item_id = t.id::text
                )
              )
            """,
            connection)
        {
            CommandTimeout = 2
        };
        AddUuidArray(command, "ids", ids);
        AddNullableGuid(command, "actor_id", session.UserId);
        command.Parameters.Add("candidate_cutoff_at", NpgsqlDbType.TimestampTz).Value = session.CandidateCutoffAt;

        var result = new List<TraceHydrationRow>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TraceHydrationRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                reader.GetInt32(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }
        return result;
    }

    internal async Task<IReadOnlyList<TraceDeePlaceHydrationRow>> HydrateTraceDeePlacesAsync(
        IReadOnlyCollection<Guid> ids,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Array.Empty<TraceDeePlaceHydrationRow>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              p.id,
              p.slug,
              p.name,
              p.description,
              p.area,
              p.category,
              p.image_url
            from public.tracedee_places p
            left join public.stores s on s.id = p.store_id
            left join public.organizations o on o.id = p.organization_id
            where p.id = any(@ids)
              and p.moderation_status in ('VISIBLE', 'LIMITED')
              and length(trim(p.category)) > 0
              and (
                p.store_id is null
                or (s.status = 'ACTIVE' and o.status = 'ACTIVE')
              )
              and (
                @actor_id is null
                or not exists (
                  select 1 from aevo_feed_negative_feedback feedback
                  where feedback.actor_id = @actor_id
                    and feedback.app_code = 'GO'
                    and feedback.active = true
                    and feedback.item_type = 'PLACE'
                    and feedback.item_id in (p.id::text, coalesce(p.store_id, p.id)::text)
                )
              )
            """,
            connection)
        {
            CommandTimeout = 2
        };
        AddUuidArray(command, "ids", ids);
        AddNullableGuid(command, "actor_id", actorId);

        var result = new List<TraceDeePlaceHydrationRow>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TraceDeePlaceHydrationRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                NullableString(reader, 6)));
        }
        return result;
    }

    internal async Task<IReadOnlyList<StoreProfileHydrationRow>> HydrateStoreProfilesAsync(
        IReadOnlyCollection<Guid> storeIds,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        if (storeIds.Count == 0) return Array.Empty<StoreProfileHydrationRow>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              p.store_id,
              p.public_slug,
              s.name,
              coalesce(p.description, ''),
              p.area,
              p.category,
              p.image_url
            from public.customer_store_profiles p
            join public.stores s
              on s.id = p.store_id
             and s.organization_id = p.organization_id
             and s.status = 'ACTIVE'
            join public.organizations o
              on o.id = p.organization_id
             and o.status = 'ACTIVE'
            where p.store_id = any(@store_ids)
              and p.public_enabled = true
              and length(trim(p.category)) > 0
              and (
                @actor_id is null
                or not exists (
                  select 1 from aevo_feed_negative_feedback feedback
                  where feedback.actor_id = @actor_id
                    and feedback.app_code = 'GO'
                    and feedback.active = true
                    and feedback.item_type = 'PLACE'
                    and feedback.item_id = p.store_id::text
                )
              )
            """,
            connection)
        {
            CommandTimeout = 2
        };
        AddUuidArray(command, "store_ids", storeIds);
        AddNullableGuid(command, "actor_id", actorId);

        var result = new List<StoreProfileHydrationRow>(storeIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoreProfileHydrationRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                NullableString(reader, 6)));
        }
        return result;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (dataSource is null)
        {
            throw new FeedSourceDataException(configurationError ?? "FEED_SOURCE_DATABASE_NOT_CONFIGURED");
        }

        try
        {
            return await dataSource.OpenConnectionAsync(cancellationToken);
        }
        catch (Exception error)
        {
            throw new FeedSourceDataException("FEED_SOURCE_DATABASE_UNAVAILABLE", error);
        }
    }

    private static void AddNullableGuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = (object?)value ?? DBNull.Value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = (object?)value ?? DBNull.Value;

    private static void AddUuidArray(NpgsqlCommand command, string name, IReadOnlyCollection<Guid> values) =>
        command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = values.ToArray();

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int ReadBoundedInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "postgres" and not "postgresql"))
        {
            throw new InvalidOperationException("The Feed canonical data connection must be PostgreSQL.");
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
            if (!string.Equals(Uri.UnescapeDataString(pair[0]).Trim(), "sslmode", StringComparison.OrdinalIgnoreCase)) continue;
            builder.SslMode = Uri.UnescapeDataString(pair[1]).Trim().ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("The Feed canonical data connection has an unsupported sslmode.")
            };
        }
        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (dataSource is not null) await dataSource.DisposeAsync();
    }
}
