using System.Collections.Concurrent;
using System.Globalization;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed record FeedRateLimitDecision(
    bool Allowed,
    int Limit,
    int Remaining,
    DateTimeOffset ResetAt);

public sealed class FeedRateLimiter
{
    private readonly ConcurrentDictionary<string, FixedWindow> windows = new(StringComparer.Ordinal);
    private readonly int anonymousLimit;
    private readonly int authenticatedLimit;

    public FeedRateLimiter(IConfiguration configuration)
    {
        anonymousLimit = ReadLimit(configuration, "AEVO_FEED_ANONYMOUS_RATE_PER_MINUTE", 120);
        authenticatedLimit = ReadLimit(configuration, "AEVO_FEED_AUTHENTICATED_RATE_PER_MINUTE", 300);
    }

    public FeedRateLimitDecision Check(FeedPrincipal principal, DateTimeOffset now)
    {
        var windowStart = now.ToUnixTimeSeconds() / 60 * 60;
        var key = $"{principal.CursorBinding}:{windowStart.ToString(CultureInfo.InvariantCulture)}";
        var window = windows.GetOrAdd(key, _ => new FixedWindow(windowStart));
        var count = Interlocked.Increment(ref window.Count);
        var limit = principal.IsAuthenticated ? authenticatedLimit : anonymousLimit;
        var resetAt = DateTimeOffset.FromUnixTimeSeconds(windowStart + 60);

        if (windows.Count > 10_000)
        {
            foreach (var item in windows)
            {
                if (item.Value.WindowStart < windowStart - 60) windows.TryRemove(item.Key, out _);
            }
        }

        return new FeedRateLimitDecision(count <= limit, limit, Math.Max(0, limit - count), resetAt);
    }

    private static int ReadLimit(IConfiguration configuration, string key, int fallback)
    {
        var configured = int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
        return Math.Clamp(configured, 1, 10_000);
    }

    private sealed class FixedWindow(long windowStart)
    {
        public long WindowStart { get; } = windowStart;
        public int Count;
    }
}

public sealed record FeedPolicyDecision(
    bool Allowed,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    object? Details = null);

public static class FeedPolicy
{
    public static FeedPolicyDecision Evaluate(FeedPrincipal principal, NormalizedFeedRequest request)
    {
        if (!string.Equals(principal.ApplicationCode, FeedApiContract.ApplicationCode, StringComparison.Ordinal))
        {
            return Denied("The Feed application scope is invalid.");
        }

        if (request.Tab == "following" && !principal.IsAuthenticated)
        {
            return new FeedPolicyDecision(
                false,
                "FEED_POLICY_DENIED",
                "The following Feed requires an authenticated GO session.",
                new { reason = "AUTHENTICATION_REQUIRED", application = FeedApiContract.ApplicationCode });
        }

        return new FeedPolicyDecision(true);
    }

    private static FeedPolicyDecision Denied(string message) =>
        new(false, "FEED_POLICY_DENIED", message);
}

public sealed record FeedFacadeResult(
    FeedResponseContract? Response,
    FeedRateLimitDecision RateLimit,
    int? ErrorStatus = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    object? ErrorDetails = null);

public sealed class FeedFacadeService(
    FeedConfigService configService,
    FeedSessionContextFactory sessionFactory,
    FeedRateLimiter rateLimiter,
    FeedCursorSigner cursorSigner,
    IFeedDataPort? dataPort = null)
{
    private readonly IFeedDataPort candidatePort = dataPort ?? new EmptyFeedDataPort();

    public Task<FeedFacadeResult> GetEmptyPageAsync(
        FeedPrincipal principal,
        NormalizedFeedRequest request,
        string requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        GetPageAsync(principal, request, requestId, now, cancellationToken);

    public async Task<FeedFacadeResult> GetPageAsync(
        FeedPrincipal principal,
        NormalizedFeedRequest request,
        string requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = FeedPolicy.Evaluate(principal, request);
        var policyRate = new FeedRateLimitDecision(true, 0, 0, now.AddMinutes(1));
        if (!policy.Allowed)
        {
            return new FeedFacadeResult(null, policyRate, StatusCodes.Status403Forbidden, policy.ErrorCode, policy.ErrorMessage, policy.Details);
        }

        var rate = rateLimiter.Check(principal, now);
        if (!rate.Allowed)
        {
            return new FeedFacadeResult(
                null,
                rate,
                StatusCodes.Status429TooManyRequests,
                "RATE_LIMITED",
                "The Feed request rate limit has been reached.",
                new { retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((rate.ResetAt - now).TotalSeconds)) });
        }

        if (!sessionFactory.CursorSigningAvailable)
        {
            return new FeedFacadeResult(
                null,
                rate,
                StatusCodes.Status503ServiceUnavailable,
                "FEED_UNAVAILABLE",
                "The Feed cursor signing boundary is not configured.");
        }

        var runtime = await configService.GetRuntimeSnapshotAsync(cancellationToken);
        var session = sessionFactory.Create(principal, request, runtime, now);
        if (!session.IsValid)
        {
            return new FeedFacadeResult(
                null,
                rate,
                StatusCodes.Status400BadRequest,
                session.ErrorCode,
                session.ErrorMessage);
        }

        var context = session.Context!;
        if (!context.RuntimeConfig.Enabled)
        {
            return new FeedFacadeResult(
                new FeedResponseContract(
                    context.FeedSessionId,
                    context.ConfigVersion,
                    context.RankingVersion,
                    Array.Empty<IFeedItemContract>(),
                    null,
                    runtime.Degraded,
                    requestId),
                rate);
        }

        var candidatePage = await candidatePort.GetCandidatesAsync(context, cancellationToken);
        var rankedCandidates = FeedRankingPipeline.Rank(
            FeedSessionOrdering
                .Deduplicate(candidatePage.Candidates)
                .Where(candidate => DiscoveryContract.IsCandidateType(candidate.EntityType)
                    && (context.SeenEntityKeys is null
                        || !context.SeenEntityKeys.Contains(ItemKey(candidate.EntityType, candidate.EntityId))))
                .ToArray(),
            context);
        var orderedCandidates = FeedSessionOrdering.StableOrder(
                rankedCandidates,
                context.CandidateCutoffAt,
                context.OrderingAnchor)
            .ToArray();
        var selectionLimit = Math.Min(
            160,
            Math.Max(context.PageSize, context.PageSize * 4));
        orderedCandidates = FeedDiversitySelector.Select(
                orderedCandidates,
                context.RuntimeConfig,
                selectionLimit)
            .ToArray();
        var hydration = await candidatePort.HydrateAsync(orderedCandidates, context, cancellationToken);
        var candidateByKey = orderedCandidates
            .GroupBy(candidate => ItemKey(candidate.EntityType, candidate.EntityId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var hydratedByKey = hydration.Items
            .Where(item => candidateByKey.TryGetValue(ItemKey(item.ItemType, item.Id), out var candidate)
                && IsSafeHydrationPair(item, candidate))
            .GroupBy(item => ItemKey(item.ItemType, item.Id), StringComparer.Ordinal)
            .ToDictionary(
            group => group.Key,
            group => group.First(),
            StringComparer.Ordinal);
        var hydrationBoundaryDegraded = hydration.Items.Count != hydratedByKey.Count;
        var servedItems = new List<IFeedItemContract>(context.PageSize);
        var servedCandidates = new List<FeedCandidateRecord>(context.PageSize);
        var lastServedIndex = -1;
        for (var index = 0; index < orderedCandidates.Length && servedItems.Count < context.PageSize; index++)
        {
            var candidate = orderedCandidates[index];
            if (!hydratedByKey.TryGetValue(ItemKey(candidate.EntityType, candidate.EntityId), out var item)) continue;
            var publicItem = RebindPublicItemToken(item, candidate, context, servedItems.Count);
            if (publicItem is null)
            {
                hydrationBoundaryDegraded = true;
                continue;
            }
            servedCandidates.Add(candidate);
            servedItems.Add(publicItem);
            lastServedIndex = index;
        }

        var hasMore = lastServedIndex >= 0
            && orderedCandidates
                .Skip(lastServedIndex + 1)
                .Any(candidate => hydratedByKey.ContainsKey(ItemKey(candidate.EntityType, candidate.EntityId)));
        var nextCursor = hasMore
            ? sessionFactory.CreateCursor(context, servedCandidates[^1], servedCandidates)
            : null;
        var response = new FeedResponseContract(
            context.FeedSessionId,
            context.ConfigVersion,
            context.RankingVersion,
            servedItems,
            nextCursor,
            runtime.Degraded || candidatePage.Degraded || hydration.Degraded || hydrationBoundaryDegraded,
            requestId,
            DiscoveryModuleSelector.Select(
                servedItems,
                context.Area,
                context.DiscoveryIntent,
                context.RuntimeConfig.Discovery?.Modules,
                candidatePage.GeneratorResults));
        return new FeedFacadeResult(response, rate);
    }

    private static string ItemKey(string itemType, string itemId) =>
        string.Concat(itemType, ":", itemId);

    private static bool IsSafeHydrationPair(IFeedItemContract item, FeedCandidateRecord candidate) =>
        string.Equals(item.ItemType, candidate.EntityType, StringComparison.Ordinal)
        && string.Equals(item.Id, candidate.EntityId, StringComparison.Ordinal)
        && (candidate.EntityType, item) switch
        {
            ("TRACE", FeedTraceItemContract trace) => string.Equals(trace.ItemType, "TRACE", StringComparison.Ordinal),
            ("PLACE", FeedPlaceItemContract place) => string.Equals(place.ItemType, "PLACE", StringComparison.Ordinal),
            _ => false
        };

    private IFeedItemContract? RebindPublicItemToken(
        IFeedItemContract item,
        FeedCandidateRecord candidate,
        FeedSessionContext context,
        int position)
    {
        try
        {
            var itemToken = cursorSigner.CreateItemToken(context, candidate.EntityType, candidate.EntityId, position);
            return item switch
            {
                FeedTraceItemContract trace => trace with { ItemToken = itemToken },
                FeedPlaceItemContract place => place with { ItemToken = itemToken },
                _ => null
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static void ApplyNoStoreHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Vary = "Cookie, x-aevo-app";
    }

    public static void ApplyRateLimitHeaders(HttpResponse response, FeedRateLimitDecision decision)
    {
        if (decision.Limit <= 0) return;
        response.Headers["x-ratelimit-limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        response.Headers["x-ratelimit-remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
        response.Headers["x-ratelimit-reset"] = decision.ResetAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (!decision.Allowed)
        {
            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((decision.ResetAt - DateTimeOffset.UtcNow).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
    }

    public bool CursorSigningAvailable => sessionFactory.CursorSigningAvailable;
}
