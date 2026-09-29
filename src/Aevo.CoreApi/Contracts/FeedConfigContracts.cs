using System.Text.Json;

namespace Aevo.CoreApi.Contracts;

public static class FeedConfigContract
{
    public const string SchemaVersion = "1";
    public const string ValidatorVersion = "feed-config-v1";
    public const string ScopeKey = "GO_PUBLIC_FEED";
    public const string DeterministicRankingVersion = "deterministic-v1";
}

public static class FeedConfigErrorCodes
{
    public const string Invalid = "FEED_CONFIG_INVALID";
    public const string NotFound = "FEED_CONFIG_NOT_FOUND";
    public const string VersionConflict = "FEED_CONFIG_VERSION_CONFLICT";
    public const string NotValidated = "FEED_CONFIG_NOT_VALIDATED";
    public const string IdempotencyConflict = "FEED_CONFIG_IDEMPOTENCY_CONFLICT";
    public const string Fallback = "FEED_CONFIG_FALLBACK";
    public const string PropagationDegraded = "FEED_CONFIG_PROPAGATION_DEGRADED";
    public const string UseLifecycle = "FEED_CONFIG_USE_LIFECYCLE";
}

public sealed record FeedCandidateSourceConfig(
    bool Enabled,
    int Budget,
    int Minimum);

public sealed record FeedCandidateSourcesConfig(
    FeedCandidateSourceConfig Trace,
    FeedCandidateSourceConfig Place);

public sealed record FeedRankingWeightsConfig(
    decimal Quality,
    decimal Freshness,
    decimal Proximity,
    decimal Taste);

public sealed record FeedRankingConfig(
    string Mode,
    string Version,
    int FreshnessWindowHours,
    FeedRankingWeightsConfig Weights);

public sealed record FeedDiversityConfig(
    int MaxConsecutiveSameSource,
    decimal MaxSourceRatio,
    decimal ExplorationQuota,
    int MaxItemsPerCategory = 3,
    int MaxItemsPerArea = 4,
    int MaxItemsPerBusiness = 1);

public sealed record FeedGeoConfig(
    int MaxCoarseRadiusMeters);

public sealed record FeedRolloutVariantConfig(
    string Key,
    int Percent);

public sealed record FeedRolloutConfig(
    int Percent,
    string? ExperimentId,
    string Salt,
    IReadOnlyList<FeedRolloutVariantConfig> Variants);

public sealed record FeedBudgetConfig(
    int PageSize,
    int CacheTtlSeconds);

public sealed record FeedSafetyConfig(
    bool GuardrailsEnabled,
    bool RequireModerationProjection,
    int MaxEligibilityAgeSeconds);

public sealed record FeedAnalyticsConfig(
    bool Enabled,
    int SamplePercent,
    int RetentionDays);

public sealed record FeedDiscoveryTasteConfig(
    int MinimumEvidence,
    decimal MinimumConfidence,
    int DecayHalfLifeDays,
    int MaxAgeDays);

public sealed record FeedDiscoveryModulesConfig(
    bool Enabled,
    int MinimumItems,
    int MaximumItems,
    IReadOnlyList<string> Order);

public sealed record FeedDiscoveryEvidenceConfig(
    bool Enabled,
    decimal MinimumConfidence,
    int MaxAgeDays);

public sealed record FeedDiscoverySafetyConfig(
    string PolicyVersion,
    bool PublicEvidenceEnabled,
    bool PublicMediaEnabled);

public sealed record FeedDiscoveryConfig(
    string IntentPrecedence,
    FeedDiscoveryTasteConfig Taste,
    FeedDiscoveryModulesConfig Modules,
    FeedDiscoveryEvidenceConfig Evidence,
    FeedDiscoverySafetyConfig Safety,
    decimal IntentMatchWeight = 0.35m);

public static class FeedDiscoveryConfigDefaults
{
    private static readonly IReadOnlyList<string> DefaultModuleOrder = new[] { "FOR_YOU" };

    public static FeedDiscoveryConfig Config => new(
        "EXPLICIT_FIRST",
        new FeedDiscoveryTasteConfig(2, 0.4m, 180, 365),
        new FeedDiscoveryModulesConfig(true, 1, 12, DefaultModuleOrder),
        new FeedDiscoveryEvidenceConfig(false, 0.7m, 30),
        new FeedDiscoverySafetyConfig("ugc-safe-v1", false, false),
        0.35m);
}

/// <summary>
/// The only JSON document that may be persisted as a Feed runtime revision.
/// Infrastructure, credential, SQL, provider, and arbitrary expression fields
/// are intentionally absent and rejected by the server validator.
/// </summary>
public sealed record FeedRuntimeConfig(
    string SchemaVersion,
    bool Enabled,
    bool KillSwitch,
    FeedCandidateSourcesConfig CandidateSources,
    FeedRankingConfig Ranking,
    FeedDiversityConfig Diversity,
    FeedGeoConfig Geo,
    FeedRolloutConfig Rollout,
    FeedBudgetConfig Budgets,
    FeedSafetyConfig Safety,
    FeedAnalyticsConfig Analytics,
    FeedDiscoveryConfig? Discovery = null);

public sealed record FeedConfigValidationIssue(
    string Code,
    string Path,
    string Message);

public sealed record FeedConfigValidationResult(
    bool Valid,
    string ValidatorVersion,
    IReadOnlyList<FeedConfigValidationIssue> Errors,
    IReadOnlyList<FeedConfigValidationIssue> Warnings,
    FeedRuntimeConfig? Config);

public sealed record FeedConfigDraftRequest(
    JsonElement Config,
    string Reason,
    string? IdempotencyKey = null);

public sealed record FeedConfigActionRequest(
    string Reason,
    string? IdempotencyKey = null);

public sealed record FeedConfigPublishRequest(
    string Reason,
    long? ExpectedActiveVersion = null,
    string? IdempotencyKey = null);

public sealed record FeedConfigRollbackRequest(
    string Reason,
    long? ExpectedActiveVersion = null,
    string? IdempotencyKey = null);

public sealed record FeedConfigValidationSummary(
    bool Valid,
    string ValidatorVersion,
    IReadOnlyList<FeedConfigValidationIssue> Errors,
    IReadOnlyList<FeedConfigValidationIssue> Warnings,
    DateTimeOffset? ValidatedAt);

public sealed record FeedConfigRevisionResponse(
    string RevisionId,
    long Version,
    string SchemaVersion,
    string Status,
    string ContentHash,
    JsonElement Config,
    FeedConfigValidationSummary Validation,
    string? SourceRevisionId,
    Guid? AuthorId,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record FeedConfigActiveResponse(
    string Scope,
    string Source,
    string Status,
    string? RevisionId,
    long? Version,
    long PointerVersion,
    string SchemaVersion,
    JsonElement Config,
    FeedConfigValidationSummary Validation,
    bool Degraded,
    string? ErrorCode,
    DateTimeOffset ObservedAt,
    string? RequestId = null);

public sealed record FeedConfigGuardrailsResponse(
    string Scope,
    string Status,
    bool Enabled,
    bool KillSwitch,
    bool GuardrailsEnabled,
    int MaxEligibilityAgeSeconds,
    int MaxCoarseRadiusMeters,
    int EventSamplePercent,
    bool Degraded,
    string? ErrorCode,
    string? RequestId = null);

public sealed record FeedConfigPropagationResponse(
    string Scope,
    string ActiveRevisionId,
    long ActiveVersion,
    long PointerVersion,
    IReadOnlyList<FeedConfigRuntimePropagation> Runtimes,
    FeedConfigLegacyCompatibility LegacyCompatibility,
    string? RequestId = null);

public sealed record FeedConfigRuntimePropagation(
    string RuntimeName,
    string Status,
    string? ObservedRevisionId,
    long? ObservedVersion,
    DateTimeOffset? LastCheckedAt,
    int? LatencyMs,
    string? LastErrorCode);

public sealed record FeedConfigLegacyCompatibility(
    bool ReadOnly,
    bool MixedFeedMatches,
    bool TasteRankingMatches,
    IReadOnlyList<string> MismatchedFlags,
    DateTimeOffset CheckedAt);

public sealed record FeedConfigLegacyFlagObservation(
    string FlagKey,
    bool Enabled,
    int RolloutPercent);
