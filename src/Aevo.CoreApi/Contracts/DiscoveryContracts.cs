namespace Aevo.CoreApi.Contracts;

/// <summary>
/// Discovery v1 freezes the product boundary used by later Feed phases.
/// It is deliberately separate from the compatibility Feed v1 vocabulary.
/// </summary>
public static class DiscoveryContract
{
    public const string Version = "v1";

    public static readonly IReadOnlySet<string> Vibes = new HashSet<string>(StringComparer.Ordinal)
    {
        "slow-bar", "quiet", "art", "work", "speakeasy", "match"
    };

    public static readonly IReadOnlySet<string> CandidateTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "TRACE", "PLACE"
    };

    public static readonly IReadOnlySet<string> EvidenceTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "RATING_SUMMARY",
        "SELECTED_REVIEW",
        "APPROVED_PHOTO",
        "VERIFIED_EXPERIENCE",
        "POPULAR_ASPECT",
        "USEFUL_TIP",
        "CREATOR_PROVENANCE",
        "TRACE_COMPLETION_SIGNAL"
    };

    public static readonly IReadOnlySet<string> ReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "BECAUSE_VIBE",
        "BECAUSE_CATEGORY",
        "NEAR_SELECTED_AREA",
        "NEAR_CURRENT_COARSE_AREA",
        "SIMILAR_TO_SAVED",
        "NEW_IN_AREA",
        "POPULAR_IN_AREA",
        "AVAILABLE_NOW",
        "BASED_ON_COMPLETED_TRACE",
        "BECAUSE_YOU_VISITED",
        "CONTINUE_YOUR_TRACE",
        "COMMUNITY_CONFIDENCE"
    };

    public static readonly IReadOnlySet<string> SignalNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "BOOKING_COMPLETED",
        "TRACE_COMPLETED",
        "SAVE_PLACE",
        "SAVE_TRACE",
        "DIRECTIONS_OPENED",
        "PLACE_OPEN",
        "TRACE_OPEN",
        "BOOKING_OPEN",
        "MAP_OPEN",
        "REVIEW_EXPAND",
        "TRACE_STARTED",
        "PHOTO_VIEW",
        "DISCOVERY_IMPRESSION",
        "HIDE",
        "NOT_INTERESTED",
        "BLOCK",
        "MUTE",
        "QUALIFIED_SKIP",
        "STALE_AVAILABILITY_REJECTION",
        "REPORT_SUBMITTED",
        "FAKE_ENGAGEMENT_ANOMALY",
        "DUPLICATE_DETECTED",
        "CONTENT_CORRECTION",
        "VERIFIED_VISIT"
    };

    public static readonly IReadOnlySet<string> CompatibilityEventNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "impression",
        "engaged_view",
        "open",
        "click",
        "save",
        "share",
        "trace_start",
        "trace_complete",
        "place_open",
        "booking_click"
    };

    public static readonly IReadOnlyDictionary<string, string> SignalClasses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BOOKING_COMPLETED"] = "STRONG_POSITIVE",
            ["TRACE_COMPLETED"] = "STRONG_POSITIVE",
            ["SAVE_PLACE"] = "STRONG_POSITIVE",
            ["SAVE_TRACE"] = "STRONG_POSITIVE",
            ["DIRECTIONS_OPENED"] = "STRONG_POSITIVE",
            ["PLACE_OPEN"] = "MEDIUM_POSITIVE",
            ["TRACE_OPEN"] = "MEDIUM_POSITIVE",
            ["BOOKING_OPEN"] = "MEDIUM_POSITIVE",
            ["MAP_OPEN"] = "MEDIUM_POSITIVE",
            ["REVIEW_EXPAND"] = "MEDIUM_POSITIVE",
            ["TRACE_STARTED"] = "MEDIUM_POSITIVE",
            ["PHOTO_VIEW"] = "WEAK_POSITIVE",
            ["DISCOVERY_IMPRESSION"] = "VERY_WEAK",
            ["HIDE"] = "STRONG_NEGATIVE",
            ["NOT_INTERESTED"] = "STRONG_NEGATIVE",
            ["BLOCK"] = "STRONG_NEGATIVE",
            ["MUTE"] = "STRONG_NEGATIVE",
            ["QUALIFIED_SKIP"] = "CAUTIOUS_NEGATIVE",
            ["STALE_AVAILABILITY_REJECTION"] = "CAUTIOUS_NEGATIVE",
            ["REPORT_SUBMITTED"] = "TRUST_QUALITY",
            ["FAKE_ENGAGEMENT_ANOMALY"] = "TRUST_QUALITY",
            ["DUPLICATE_DETECTED"] = "TRUST_QUALITY",
            ["CONTENT_CORRECTION"] = "TRUST_QUALITY",
            ["VERIFIED_VISIT"] = "TRUST_QUALITY"
        };

    public static readonly IReadOnlySet<string> OutcomeMetrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "QUALIFIED_PLACE_OPEN",
        "QUALIFIED_TRACE_OPEN",
        "SAVE_PLACE",
        "SAVE_TRACE",
        "MAP_OPEN",
        "DIRECTIONS_OPENED",
        "MENU_OPEN",
        "BOOKING_OPEN",
        "BOOKING_STARTED",
        "BOOKING_COMPLETED",
        "TRACE_STARTED",
        "TRACE_COMPLETED",
        "REVIEW_EXPAND",
        "REVIEW_HELPFUL",
        "VALID_RETURN"
    };

    public static readonly IReadOnlySet<string> GuardrailMetrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "UNSAFE_CONTENT_EXPOSURE",
        "REPORT_RATE",
        "HIDE_RATE",
        "NOT_INTERESTED_RATE",
        "DUPLICATE_CONTENT_RATE",
        "REPETITIVE_ENTITY_RATE",
        "REVIEW_MANIPULATION_ANOMALY_RATE",
        "FAKE_VISIT_ANOMALY_RATE",
        "SOURCE_CONCENTRATION",
        "REASON_PRIVACY_LEAK_RATE",
        "LATENCY_P95_MS",
        "DEGRADED_RESPONSE_RATE",
        "EMPTY_SAFE_INVENTORY_RATE"
    };

    public static readonly IReadOnlySet<string> OperationalMetrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "GENERATOR_LATENCY_MS",
        "GENERATOR_ERROR_RATE",
        "ELIGIBILITY_FRESHNESS_SECONDS",
        "FEATURE_FRESHNESS_SECONDS",
        "EVENT_ACCEPTANCE_RATE",
        "EVENT_DUPLICATE_RATE",
        "EVENT_DEAD_LETTER_RATE",
        "MODULE_FILL_RATE",
        "BOOKING_DEPENDENCY_HEALTH"
    };

    public static readonly IReadOnlySet<string> PublicEvidenceFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "aggregateRating",
        "ratingCount",
        "ratingConfidence",
        "verifiedExperienceRatio",
        "popularAspects",
        "selectedReview",
        "photoCount",
        "recentUsefulFeedbackAt",
        "evidenceVersion"
    };

    public static bool IsCandidateType(string? value) => value is not null && CandidateTypes.Contains(value);

    public static bool IsEvidenceType(string? value) => value is not null && EvidenceTypes.Contains(value);

    public static bool IsReasonCode(string? value) => value is not null && ReasonCodes.Contains(value);

    public static bool IsSignalName(string? value) => value is not null && SignalNames.Contains(value);

    public static string? MapLegacyFeedEventToSignal(string? eventName, string? itemType)
    {
        if (eventName is null || itemType is null || !CandidateTypes.Contains(itemType)) return null;
        return eventName switch
        {
            "impression" => "DISCOVERY_IMPRESSION",
            "open" => itemType == "PLACE" ? "PLACE_OPEN" : "TRACE_OPEN",
            "save" => itemType == "PLACE" ? "SAVE_PLACE" : "SAVE_TRACE",
            "trace_start" when itemType == "TRACE" => "TRACE_STARTED",
            "trace_complete" when itemType == "TRACE" => "TRACE_COMPLETED",
            "place_open" when itemType == "PLACE" => "PLACE_OPEN",
            "booking_click" when itemType == "PLACE" => "BOOKING_OPEN",
            _ => null
        };
    }

    public static bool IsMetric(string? value) => value is not null
        && (OutcomeMetrics.Contains(value)
            || GuardrailMetrics.Contains(value)
            || OperationalMetrics.Contains(value));
}

public sealed record DiscoveryCandidateBoundaryContract(
    string ItemType,
    string Id);

public sealed record DiscoverySafeReviewHighlightContract(
    string ReviewId,
    string Excerpt,
    IReadOnlyList<string> Aspects);

public sealed record DiscoveryEvidenceBlockContract(
    double? AggregateRating,
    int RatingCount,
    string RatingConfidence,
    double? VerifiedExperienceRatio,
    IReadOnlyList<string> PopularAspects,
    DiscoverySafeReviewHighlightContract? SelectedReview,
    int PhotoCount,
    DateTimeOffset? RecentUsefulFeedbackAt,
    string EvidenceVersion);

public sealed record DiscoveryModuleItemReferenceContract(
    string ItemType,
    string ItemId,
    string ItemToken);

public sealed record DiscoveryModuleContract(
    string ModuleId,
    string? ReasonCode,
    IReadOnlyList<DiscoveryModuleItemReferenceContract> Items,
    bool Degraded = false);
