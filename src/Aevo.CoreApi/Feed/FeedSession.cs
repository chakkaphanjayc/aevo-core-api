using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Microsoft.AspNetCore.Http;

namespace Aevo.CoreApi.Feed;

public sealed record FeedPrincipal(
    string ApplicationCode,
    bool IsAuthenticated,
    Guid? UserId,
    Guid? SessionId,
    Guid? OrganizationId,
    Guid? StoreId,
    string StableKey,
    string CursorBinding);

public static class FeedPrincipalFactory
{
    public static FeedPrincipal FromGoSession(CoreSession session)
    {
        if (!string.Equals(session.AppCode, FeedApiContract.ApplicationCode, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Feed principal must be bound to the GO application.", nameof(session));
        }

        var stableKey = $"GO:user:{session.UserId:N}";
        var binding = Hash(
            $"GO|authenticated|session:{session.Id:N}|user:{session.UserId:N}|organization:{session.OrganizationId?.ToString("N") ?? "none"}|store:{session.StoreId?.ToString("N") ?? "none"}");
        return new FeedPrincipal(
            FeedApiContract.ApplicationCode,
            true,
            session.UserId,
            session.Id,
            session.OrganizationId,
            session.StoreId,
            stableKey,
            binding);
    }

    public static FeedPrincipal Anonymous(string anonymousKey)
    {
        if (!IsValidAnonymousKey(anonymousKey)) throw new ArgumentException("The anonymous key is invalid.", nameof(anonymousKey));

        return new FeedPrincipal(
            FeedApiContract.ApplicationCode,
            false,
            null,
            null,
            null,
            null,
            $"GO:anonymous:{anonymousKey}",
            Hash($"GO|anonymous|key:{anonymousKey}"));
    }

    public static string CreateAnonymousKey() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidAnonymousKey(string value) =>
        value.Length == 43 && value.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_');

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class FeedAnonymousCookie
{
    public const string Name = "aevo_go_anonymous";
    private const int MaxAgeSeconds = 60 * 60 * 24 * 180;

    public static FeedPrincipalResolution Resolve(HttpContext context, bool secure)
    {
        if (context.Request.Cookies.TryGetValue(Name, out var existing)
            && existing is not null
            && FeedPrincipalFactory.IsValidAnonymousKey(existing))
        {
            return new FeedPrincipalResolution(FeedPrincipalFactory.Anonymous(existing), false, null);
        }

        var key = FeedPrincipalFactory.CreateAnonymousKey();
        return new FeedPrincipalResolution(FeedPrincipalFactory.Anonymous(key), true, SetCookie(key, secure));
    }

    private static string SetCookie(string value, bool secure) =>
        $"{Name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Lax; Max-Age={MaxAgeSeconds}{(secure ? "; Secure" : string.Empty)}";
}

public sealed record FeedPrincipalResolution(
    FeedPrincipal Principal,
    bool ShouldSetCookie,
    string? SetCookieHeader);

public sealed record NormalizedFeedRequest(
    string Surface,
    string Tab,
    string? Query,
    string? Area,
    FeedCoarseLocationContract? CoarseLocation,
    string? Cursor,
    int Limit,
    FeedDiscoveryIntentContract? DiscoveryIntent = null)
{
    public string Fingerprint()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            surface = Surface,
            tab = Tab,
            query = Query,
            area = Area,
            coarseLocation = CoarseLocation,
            discoveryIntent = DiscoveryIntent,
            limit = Limit
        });
        return FeedPrincipalFactory.Hash(canonical);
    }
}

public sealed record FeedOrderingAnchor(
    string EntityType,
    string StableKey,
    string EntityId,
    long PublishedAtUtcTicks,
    double? RankingScore = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Encode(FeedCandidateRecord candidate)
    {
        var anchor = new FeedOrderingAnchor(
            candidate.EntityType,
            candidate.StableKey,
            candidate.EntityId,
            candidate.PublishedAt.UtcTicks,
            candidate.RankingScore);
        return Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(anchor, SerializerOptions)));
    }

    public static bool TryDecode(string? encoded, out FeedOrderingAnchor? anchor)
    {
        anchor = null;
        if (encoded is null) return true;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 512) return false;

        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            var decoded = JsonSerializer.Deserialize<FeedOrderingAnchor>(bytes, SerializerOptions);
            if (decoded is null
                || decoded.EntityType is not ("TRACE" or "PLACE")
                || string.IsNullOrWhiteSpace(decoded.StableKey)
                || string.IsNullOrWhiteSpace(decoded.EntityId)
                || decoded.PublishedAtUtcTicks <= 0
                || (decoded.RankingScore is double rankingScore && !double.IsFinite(rankingScore)))
            {
                return false;
            }

            anchor = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record FeedSessionContext(
    string FeedSessionId,
    string ApplicationCode,
    string PrincipalBinding,
    bool IsAuthenticated,
    string Surface,
    string Tab,
    string? Query,
    string? Area,
    FeedCoarseLocationContract? CoarseLocation,
    string RequestFingerprint,
    DateTimeOffset StartedAt,
    DateTimeOffset CandidateCutoffAt,
    string RankingEpoch,
    string ConfigVersion,
    string RankingVersion,
    string PolicyVersion,
    string ExperimentSeed,
    int PageSize,
    DateTimeOffset ExpiresAt,
    FeedRuntimeConfig RuntimeConfig,
    Guid? UserId,
    FeedOrderingAnchor? OrderingAnchor,
    FeedDiscoveryIntentContract? DiscoveryIntent = null,
    IReadOnlySet<string>? SeenEntityKeys = null);

public sealed record FeedSessionResolution(
    FeedSessionContext? Context,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsValid => Context is not null;

    public static FeedSessionResolution Invalid(string code, string message) => new(null, code, message);
}

public sealed record FeedSessionOptions(int LifetimeSeconds)
{
    public static FeedSessionOptions From(IConfiguration configuration)
    {
        var configured = int.TryParse(configuration["AEVO_FEED_SESSION_TTL_SECONDS"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 300;
        return new FeedSessionOptions(Math.Clamp(configured, 60, 900));
    }
}

public sealed class FeedSessionContextFactory
{
    public const string PolicyVersion = "feed-policy-v1";

    private readonly FeedCursorSigner cursorSigner;
    private readonly FeedSessionOptions options;

    public FeedSessionContextFactory(FeedCursorSigner cursorSigner, IConfiguration configuration)
    {
        this.cursorSigner = cursorSigner;
        options = FeedSessionOptions.From(configuration);
    }

    public bool CursorSigningAvailable => cursorSigner.IsConfigured;

    public FeedSessionResolution Create(
        FeedPrincipal principal,
        NormalizedFeedRequest request,
        FeedConfigRuntimeSnapshot runtime,
        DateTimeOffset now)
    {
        var configVersion = runtime.Version?.ToString(CultureInfo.InvariantCulture) ?? "baseline";
        var rankingVersion = runtime.Config.Ranking.Version;
        var requestFingerprint = request.Fingerprint();

        if (request.Cursor is not null)
        {
            var verification = cursorSigner.Verify(request.Cursor, now);
            if (!verification.IsValid || verification.Payload is null)
            {
                return FeedSessionResolution.Invalid("FEED_CURSOR_INVALID", "The Feed cursor is invalid or expired.");
            }

            var payload = verification.Payload;
            if (!string.Equals(payload.ApplicationCode, principal.ApplicationCode, StringComparison.Ordinal)
                || !string.Equals(payload.PrincipalBinding, principal.CursorBinding, StringComparison.Ordinal)
                || !string.Equals(payload.Surface, request.Surface, StringComparison.Ordinal)
                || !string.Equals(payload.Tab, request.Tab, StringComparison.Ordinal)
                || !string.Equals(payload.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                || !string.Equals(payload.ConfigVersion, configVersion, StringComparison.Ordinal)
                || !string.Equals(payload.RankingVersion, rankingVersion, StringComparison.Ordinal)
                || !string.Equals(payload.PolicyVersion, PolicyVersion, StringComparison.Ordinal)
                || payload.PageSize != request.Limit)
            {
                return FeedSessionResolution.Invalid("FEED_CURSOR_INVALID", "The Feed cursor does not match this request context.");
            }

            try
            {
                var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.StartedAtUnixMilliseconds);
                var candidateCutoffAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.CandidateCutoffAtUnixMilliseconds);
                var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresAtUnixMilliseconds);
                if (startedAt > candidateCutoffAt || expiresAt <= now || candidateCutoffAt > expiresAt)
                {
                    return FeedSessionResolution.Invalid("FEED_CURSOR_INVALID", "The Feed cursor is invalid or expired.");
                }

                if (!FeedOrderingAnchor.TryDecode(payload.OrderingAnchorKey, out var orderingAnchor))
                {
                    return FeedSessionResolution.Invalid("FEED_CURSOR_INVALID", "The Feed cursor ordering anchor is invalid.");
                }

                var seenEntityKeys = FeedSeenKeyPolicy.Normalize(payload.SeenEntityKeys);

                return new FeedSessionResolution(new FeedSessionContext(
                    payload.FeedSessionId,
                    payload.ApplicationCode,
                    payload.PrincipalBinding,
                    principal.IsAuthenticated,
                    payload.Surface,
                    payload.Tab,
                    request.Query,
                    request.Area,
                    request.CoarseLocation,
                    payload.RequestFingerprint,
                    startedAt,
                    candidateCutoffAt,
                    payload.RankingEpoch,
                    payload.ConfigVersion,
                    payload.RankingVersion,
                    payload.PolicyVersion,
                    payload.ExperimentSeed,
                    payload.PageSize,
                    expiresAt,
                    runtime.Config,
                    principal.UserId,
                    orderingAnchor,
                    request.DiscoveryIntent,
                    seenEntityKeys));
            }
            catch (ArgumentOutOfRangeException)
            {
                return FeedSessionResolution.Invalid("FEED_CURSOR_INVALID", "The Feed cursor is invalid or expired.");
            }
        }

        var sessionId = Guid.NewGuid().ToString("N");
        // Cursor payloads persist Unix milliseconds. Start the session at the
        // same precision so freshness/ranking values cannot drift by
        // sub-millisecond ticks between page one and page two.
        var sessionStart = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var expires = sessionStart.AddSeconds(options.LifetimeSeconds);
        var experimentSeed = FeedPrincipalFactory.Hash(string.Join(
            "|",
            principal.StableKey,
            configVersion,
            rankingVersion,
            runtime.Config.Rollout.ExperimentId ?? string.Empty,
            runtime.Config.Rollout.Salt));
        var rankingEpoch = sessionStart.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        return new FeedSessionResolution(new FeedSessionContext(
            sessionId,
            principal.ApplicationCode,
            principal.CursorBinding,
            principal.IsAuthenticated,
            request.Surface,
            request.Tab,
            request.Query,
            request.Area,
            request.CoarseLocation,
            requestFingerprint,
            sessionStart,
            sessionStart,
            rankingEpoch,
            configVersion,
            rankingVersion,
            PolicyVersion,
            experimentSeed,
            request.Limit,
            expires,
            runtime.Config,
            principal.UserId,
            null,
            request.DiscoveryIntent));
    }

    public string CreateCursor(
        FeedSessionContext context,
        FeedCandidateRecord? orderingAnchor = null,
        IReadOnlyCollection<FeedCandidateRecord>? servedCandidates = null)
    {
        var orderingAnchorKey = orderingAnchor is null ? null : FeedOrderingAnchor.Encode(orderingAnchor);
        var orderingAnchorHash = orderingAnchorKey is null ? null : FeedPrincipalFactory.Hash(orderingAnchorKey);
        var existingSeenEntityKeys = context.SeenEntityKeys?.AsEnumerable() ?? Enumerable.Empty<string>();
        var seenEntityKeys = existingSeenEntityKeys
            .Concat(servedCandidates?.Select(candidate => string.Concat(candidate.EntityType, ":", candidate.EntityId)) ?? Array.Empty<string>())
            .Where(FeedSeenKeyPolicy.IsValidKey)
            .Distinct(StringComparer.Ordinal)
            .TakeLast(FeedSeenKeyPolicy.MaximumKeys)
            .ToArray();
        return cursorSigner.Sign(new FeedCursorPayload(
            FeedCursorSigner.Version,
            context.FeedSessionId,
            context.ApplicationCode,
            context.PrincipalBinding,
            context.Surface,
            context.Tab,
            context.RequestFingerprint,
            context.ConfigVersion,
            context.RankingVersion,
            context.PolicyVersion,
            context.RankingEpoch,
            context.ExperimentSeed,
            context.PageSize,
            context.StartedAt.ToUnixTimeMilliseconds(),
            context.CandidateCutoffAt.ToUnixTimeMilliseconds(),
            context.ExpiresAt.ToUnixTimeMilliseconds(),
            orderingAnchorHash,
            orderingAnchorKey,
            seenEntityKeys.Length == 0 ? null : seenEntityKeys));
    }
}

public static class FeedSessionOrdering
{
    public static IReadOnlyList<FeedCandidateRecord> StableOrder(
        IEnumerable<FeedCandidateRecord> candidates,
        DateTimeOffset candidateCutoffAt,
        FeedOrderingAnchor? orderingAnchor = null)
    {
        var eligible = candidates
            .Where(candidate => candidate.PublishedAt <= candidateCutoffAt)
            .ToArray();
        var hasRanking = eligible.Any(candidate => candidate.RankingScore.HasValue);
        var ordered = hasRanking
            ? eligible
                .OrderByDescending(candidate => candidate.RankingScore ?? double.MinValue)
                .ThenByDescending(candidate => candidate.PublishedAt)
                .ThenBy(candidate => candidate.EntityType, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.EntityId, StringComparer.Ordinal)
            : eligible
                .OrderByDescending(candidate => candidate.PublishedAt)
                .ThenBy(candidate => candidate.EntityType, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.EntityId, StringComparer.Ordinal);

        return ordered
            .Where(candidate => orderingAnchor is null || Compare(candidate, orderingAnchor) > 0)
            .ToArray();
    }

    public static IReadOnlyList<FeedCandidateRecord> Deduplicate(IEnumerable<FeedCandidateRecord> candidates)
    {
        return candidates
            .GroupBy(candidate => string.Concat(candidate.EntityType, ":", candidate.EffectiveCanonicalIdentity), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.SourcePriority)
                .ThenByDescending(candidate => candidate.PublishedAt)
                .ThenBy(candidate => candidate.Source, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.EntityId, StringComparer.Ordinal)
                .First())
            .ToArray();
    }

    public static int Compare(FeedCandidateRecord candidate, FeedOrderingAnchor anchor)
    {
        if (anchor.RankingScore is double anchorScore)
        {
            var candidateScore = candidate.RankingScore ?? double.MinValue;
            var rankingComparison = candidateScore.CompareTo(anchorScore);
            if (rankingComparison != 0) return -rankingComparison;
        }

        var publishedComparison = candidate.PublishedAt.UtcTicks.CompareTo(anchor.PublishedAtUtcTicks);
        if (publishedComparison != 0) return -publishedComparison;

        var typeComparison = string.Compare(candidate.EntityType, anchor.EntityType, StringComparison.Ordinal);
        if (typeComparison != 0) return typeComparison;

        var stableComparison = string.Compare(candidate.StableKey, anchor.StableKey, StringComparison.Ordinal);
        if (stableComparison != 0) return stableComparison;

        return string.Compare(candidate.EntityId, anchor.EntityId, StringComparison.Ordinal);
    }
}
