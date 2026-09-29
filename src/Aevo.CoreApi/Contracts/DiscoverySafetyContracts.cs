namespace Aevo.CoreApi.Contracts;

/// <summary>
/// Public UGC/media eligibility is fail-closed. This contract does not create
/// an upload route or choose a storage/moderation owner.
/// </summary>
public static class DiscoverySafetyContract
{
    public static readonly IReadOnlySet<string> PublicStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "APPROVED"
    };

    public static readonly IReadOnlySet<string> UGCStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "PRIVATE",
        "QUARANTINED",
        "APPROVED",
        "NEEDS_REVIEW",
        "REJECTED",
        "REMOVED"
    };

    public static readonly IReadOnlySet<string> UGCKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "REVIEW",
        "PHOTO",
        "COMMENT",
        "POST",
        "TIP",
        "QUESTION",
        "ANSWER"
    };

    public static readonly IReadOnlySet<string> SafetyReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "SEXUAL_OR_EXPLICIT",
        "NUDITY",
        "GRAPHIC_VIOLENCE",
        "HATE_OR_HARASSMENT",
        "PERSONAL_INFORMATION",
        "ILLEGAL_OR_DANGEROUS",
        "SPAM",
        "FAKE_ENGAGEMENT",
        "OFF_TOPIC",
        "DUPLICATE",
        "PROMOTIONAL_ABUSE",
        "MODERATION_UNAVAILABLE",
        "NOT_APPROVED"
    };

    public static bool IsPublicEligible(
        string? kind,
        string? status,
        bool publicVariantAvailable,
        string? reasonCode = null) =>
        kind is not null
        && UGCKinds.Contains(kind)
        && status is not null
        && PublicStatuses.Contains(status)
        && publicVariantAvailable
        && reasonCode is null;
}

public sealed record DiscoveryUgcEligibilityContract(
    string Kind,
    string Status,
    bool PublicVariantAvailable,
    string? ReasonCode = null);

