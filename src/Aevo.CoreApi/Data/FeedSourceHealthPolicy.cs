namespace Aevo.CoreApi.Data;

internal static class FeedSourceHealthPolicy
{
    public const string TraceSource = "tracedee-trace";
    public const string TraceDeePlaceSource = "tracedee-place";
    public const string StoreProfileSource = "store-profile";
    public const int DefaultFreshnessWindowSeconds = 7 * 24 * 60 * 60;
    public const int MinimumFreshnessWindowSeconds = 60;
    public const int MaximumFreshnessWindowSeconds = 30 * 24 * 60 * 60;

    public static FeedSourceHealth Evaluate(
        string source,
        int eligibleCount,
        DateTimeOffset? latestEligibleAt,
        DateTimeOffset checkedAt,
        int freshnessWindowSeconds)
    {
        var normalizedWindow = Math.Clamp(
            freshnessWindowSeconds,
            MinimumFreshnessWindowSeconds,
            MaximumFreshnessWindowSeconds);
        var normalizedCount = Math.Max(0, eligibleCount);

        if (normalizedCount == 0)
        {
            return new FeedSourceHealth(
                source,
                "EMPTY",
                true,
                true,
                0,
                null,
                normalizedWindow);
        }

        var fresh = latestEligibleAt is not null
            && latestEligibleAt.Value >= checkedAt.AddSeconds(-normalizedWindow);
        return new FeedSourceHealth(
            source,
            fresh ? "HEALTHY" : "STALE",
            true,
            fresh,
            normalizedCount,
            latestEligibleAt,
            normalizedWindow,
            fresh ? null : "FEED_SOURCE_STALE");
    }

    public static FeedSourceHealth Unavailable(
        string source,
        string failureCode,
        int freshnessWindowSeconds,
        DateTimeOffset checkedAt) =>
        new(
            source,
            "UNAVAILABLE",
            false,
            false,
            0,
            null,
            Math.Clamp(
                freshnessWindowSeconds,
                MinimumFreshnessWindowSeconds,
                MaximumFreshnessWindowSeconds),
            failureCode);

    public static IReadOnlyList<FeedSourceHealth> SourceNames(
        int freshnessWindowSeconds,
        string failureCode,
        DateTimeOffset checkedAt) =>
        new[]
        {
            Unavailable(TraceSource, failureCode, freshnessWindowSeconds, checkedAt),
            Unavailable(TraceDeePlaceSource, failureCode, freshnessWindowSeconds, checkedAt),
            Unavailable(StoreProfileSource, failureCode, freshnessWindowSeconds, checkedAt)
        };
}
