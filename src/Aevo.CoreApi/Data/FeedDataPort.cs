using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;

namespace Aevo.CoreApi.Data;

/// <summary>
/// Private server-side seam for the transitional TraceDee/public discovery
/// adapter. It is deliberately not registered as a browser-facing endpoint.
/// FEED-003 supplies the request/session orchestration and FEED-004 keeps the
/// canonical TraceDee/public-discovery reads behind this boundary.
/// </summary>
public interface IFeedDataPort
{
    /// <summary>
    /// Private candidate seam. Implementations receive the already-bound
    /// session context rather than browser input.
    /// </summary>
    Task<FeedCandidatePage> GetCandidatesAsync(FeedSessionContext session, CancellationToken cancellationToken);

    /// <summary>
    /// Private hydration seam. Implementations must return only public Feed
    /// contract fields; raw scores and source internals stay inside Core.
    /// </summary>
    Task<FeedHydrationResult> HydrateAsync(
        IReadOnlyList<FeedCandidateRecord> candidates,
        FeedSessionContext session,
        CancellationToken cancellationToken);

    Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Internal feature input for FEED-005. This record is kept on the private
/// Core data seam and is never mapped to the public Feed response.
/// </summary>
public sealed record FeedCandidateFeatureInput(
    string? CreatorKey = null,
    string? Area = null,
    IReadOnlyList<string>? TopicTags = null,
    double QualityScore = 0.5,
    double QualityConfidence = 0,
    double PopularitySignal = 0,
    double RelationshipSignal = 0,
    double AffinitySignal = 0,
    double GeographySignal = 0.5,
    double PenaltySignal = 0,
    string? LineageKey = null,
    bool IsExplorationCandidate = false,
    int AffinityEvidenceCount = 0,
    DateTimeOffset? AffinityCalculatedAt = null,
    string? CategoryKey = null,
    string? BusinessKey = null)
{
    public IReadOnlyList<string> EffectiveTopicTags => TopicTags ?? Array.Empty<string>();
}

public sealed record FeedCandidateRecord(
    string EntityType,
    string EntityId,
    string Source,
    DateTimeOffset PublishedAt,
    string StableKey,
    string? CanonicalIdentity = null,
    string? HydrationId = null,
    int SourcePriority = 0,
    FeedCandidateFeatureInput? Features = null,
    double? RankingScore = null,
    double? ShadowScore = null,
    string? RankingReasonCode = null)
{
    public string EffectiveCanonicalIdentity => string.IsNullOrWhiteSpace(CanonicalIdentity)
        ? $"{EntityType}:{EntityId}"
        : CanonicalIdentity;

    public string EffectiveHydrationId => string.IsNullOrWhiteSpace(HydrationId)
        ? EntityId
        : HydrationId;
}

public sealed record FeedGeneratorTelemetry(
    string Generator,
    string Status,
    int CandidateCount,
    int EligibleCount,
    int DedupeCount,
    long DurationMs,
    bool Exhausted,
    string? FailureCode = null);

public sealed record FeedCandidatePage(
    IReadOnlyList<FeedCandidateRecord> Candidates,
    string? NextAnchor,
    IReadOnlyList<FeedGeneratorTelemetry>? Generators = null,
    bool Degraded = false)
{
    public IReadOnlyList<FeedGeneratorTelemetry> GeneratorResults => Generators ?? Array.Empty<FeedGeneratorTelemetry>();
}

public sealed record FeedHydrationResult(
    IReadOnlyList<IFeedItemContract> Items,
    bool Degraded = false,
    string? FailureCode = null);

public sealed record FeedSourceHealth(
    string Source,
    string Status,
    bool Available,
    bool Fresh,
    int EligibleCount,
    DateTimeOffset? LatestEligibleAt,
    int FreshnessWindowSeconds,
    string? FailureCode = null);

public sealed record FeedDataPortHealth(
    bool Available,
    string Source,
    string? FailureCode = null,
    IReadOnlyList<FeedSourceHealth>? Sources = null,
    DateTimeOffset? CheckedAt = null)
{
    public IReadOnlyList<FeedSourceHealth> SourceHealth => Sources ?? Array.Empty<FeedSourceHealth>();
}

/// <summary>
/// A safe empty implementation remains useful for unit tests and for a
/// deliberately disabled data boundary. Production registers the canonical
/// FEED-004 implementation in Program.cs.
/// </summary>
public sealed class EmptyFeedDataPort : IFeedDataPort
{
    public Task<FeedCandidatePage> GetCandidatesAsync(FeedSessionContext session, CancellationToken cancellationToken) =>
        Task.FromResult(new FeedCandidatePage(Array.Empty<FeedCandidateRecord>(), null));

    public Task<FeedHydrationResult> HydrateAsync(
        IReadOnlyList<FeedCandidateRecord> candidates,
        FeedSessionContext session,
        CancellationToken cancellationToken) =>
        Task.FromResult(new FeedHydrationResult(Array.Empty<IFeedItemContract>()));

    public Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new FeedDataPortHealth(false, "empty-feed-data-port", "FEED_CANDIDATES_NOT_IMPLEMENTED"));
}
