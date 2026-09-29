using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedFacadeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private const string CursorSecret = "feed-cursor-test-secret-with-at-least-32-bytes";

    [Fact]
    public void NormalizationProducesStableFingerprintAndRejectsUntrustedQueryFields()
    {
        var first = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "EXPLORE",
            ["tab"] = "FOR_YOU",
            ["q"] = "  Café   ROUTE ",
            ["area"] = " Sukhumvit ",
            ["coarseLocation"] = "{\"geohash\":\"W4R\",\"radiusMeters\":2000}",
            ["limit"] = "24"
        }));
        var second = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "explore",
            ["tab"] = "for_you",
            ["q"] = "café route",
            ["area"] = "sukhumvit",
            ["coarseLocation"] = "{\"radiusMeters\":2000,\"geohash\":\"w4r\"}",
            ["limit"] = "24"
        }));
        var rejected = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "explore",
            ["organizationId"] = "attacker-controlled"
        }));

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.Request!.Fingerprint(), second.Request!.Fingerprint());
        Assert.Equal("café route", first.Request.Query);
        Assert.Equal("w4r", first.Request.CoarseLocation!.Geohash);
        Assert.False(rejected.IsValid);
        Assert.Equal("INVALID_FEED_REQUEST", rejected.ErrorCode);
    }

    [Fact]
    public void NormalizationKeepsExplicitDiscoveryIntentBoundedAndRequestLocal()
    {
        var normalized = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "explore",
            ["vibe"] = "ART",
            ["category"] = "Cafe",
            ["date"] = "2026-09-26",
            ["party"] = "2"
        }));
        var invalidVibe = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "explore",
            ["vibe"] = "unknown-vibe"
        }));
        var invalidDate = FeedRequestNormalizer.Normalize(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["surface"] = "explore",
            ["date"] = "26-09-2026"
        }));

        Assert.True(normalized.IsValid);
        Assert.Equal("art", normalized.Request!.DiscoveryIntent!.Vibe);
        Assert.Equal("cafe", normalized.Request.DiscoveryIntent.Category);
        Assert.Equal("2026-09-26", normalized.Request.DiscoveryIntent.Date);
        Assert.Equal(2, normalized.Request.DiscoveryIntent.PartySize);
        Assert.False(invalidVibe.IsValid);
        Assert.False(invalidDate.IsValid);
    }

    [Fact]
    public void CursorRejectsTamperWrongContextExpiryMalformedAndPageSizeChanges()
    {
        var signer = new FeedCursorSigner(CursorSecret);
        var factory = NewFactory(signer);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = ValidRequest();
        var runtime = RuntimeSnapshot();
        var created = factory.Create(principal, request, runtime, FixedNow);
        var context = created.Context!;
        var cursor = factory.CreateCursor(context);

        var resumed = factory.Create(principal, request with { Cursor = cursor }, runtime, FixedNow.AddSeconds(1));
        var tampered = factory.Create(principal, request with { Cursor = cursor[..^1] + (cursor[^1] == 'a' ? "b" : "a") }, runtime, FixedNow.AddSeconds(1));
        var wrongPrincipal = factory.Create(FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey()), request with { Cursor = cursor }, runtime, FixedNow.AddSeconds(1));
        var expired = factory.Create(principal, request with { Cursor = cursor }, runtime, FixedNow.AddMinutes(6));
        var malformed = factory.Create(principal, request with { Cursor = "not-a-cursor" }, runtime, FixedNow);
        var pageSizeChanged = factory.Create(principal, request with { Cursor = cursor, Limit = 25 }, runtime, FixedNow.AddSeconds(1));
        var intentChanged = factory.Create(
            principal,
            request with
            {
                Cursor = cursor,
                DiscoveryIntent = new FeedDiscoveryIntentContract("art", "cafe", "2026-09-26", 2)
            },
            runtime,
            FixedNow.AddSeconds(1));

        Assert.True(resumed.IsValid);
        Assert.Equal(context.FeedSessionId, resumed.Context!.FeedSessionId);
        Assert.Equal(context.ExperimentSeed, resumed.Context.ExperimentSeed);
        Assert.False(tampered.IsValid);
        Assert.False(wrongPrincipal.IsValid);
        Assert.False(expired.IsValid);
        Assert.False(malformed.IsValid);
        Assert.False(pageSizeChanged.IsValid);
        Assert.False(intentChanged.IsValid);
        Assert.Equal("FEED_CURSOR_INVALID", tampered.ErrorCode);
    }

    [Fact]
    public void SessionSeedAndOrderingRemainDeterministicWhenNewContentArrivesAfterCutoff()
    {
        var factory = NewFactory(new FeedCursorSigner(CursorSecret));
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var runtime = RuntimeSnapshot();
        var request = ValidRequest();
        var first = factory.Create(principal, request, runtime, FixedNow).Context!;
        var second = factory.Create(principal, request, runtime, FixedNow).Context!;

        var before = new[]
        {
            new FeedCandidateRecord("TRACE", "trace-a", "trace", FixedNow.AddMinutes(-1), "trace-a"),
            new FeedCandidateRecord("PLACE", "place-a", "place", FixedNow.AddMinutes(-2), "place-a")
        };
        var afterInsertion = before.Prepend(
            new FeedCandidateRecord("TRACE", "trace-new", "trace", FixedNow.AddSeconds(1), "trace-new"))
            .ToArray();

        var beforeOrder = FeedSessionOrdering.StableOrder(before, FixedNow).Select(candidate => candidate.EntityId).ToArray();
        var afterOrder = FeedSessionOrdering.StableOrder(afterInsertion, FixedNow).Select(candidate => candidate.EntityId).ToArray();

        Assert.Equal(first.ExperimentSeed, second.ExperimentSeed);
        Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(beforeOrder, afterOrder);
        Assert.DoesNotContain("trace-new", afterOrder);
    }

    [Fact]
    public void AnonymousAndAuthenticatedGoPrincipalsHaveSeparateBindingsAndIgnoreAdminCookie()
    {
        var anonymous = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var authenticated = FeedPrincipalFactory.FromGoSession(new CoreSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "GO",
            FixedNow.AddHours(1),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "user@example.test",
            "User",
            null,
            null));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "aevo_admin_session=admin-session-token-with-enough-length-1234567890";
        var sessions = new Aevo.CoreApi.Security.AppSessionReader(new ConfigurationBuilder().Build());

        Assert.False(anonymous.IsAuthenticated);
        Assert.True(authenticated.IsAuthenticated);
        Assert.NotEqual(anonymous.CursorBinding, authenticated.CursorBinding);
        Assert.Null(sessions.ReadSessionCookie(context, "GO"));
        Assert.Equal("GO", authenticated.ApplicationCode);
    }

    [Fact]
    public void FollowingTabIsDeniedForAnonymousButAllowedForAuthenticatedPrincipal()
    {
        var request = ValidRequest() with { Tab = "following" };
        var anonymous = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var authenticated = FeedPrincipalFactory.FromGoSession(new CoreSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "GO",
            FixedNow.AddHours(1),
            null,
            null,
            "user@example.test",
            null,
            null,
            null));

        var anonymousDecision = FeedPolicy.Evaluate(anonymous, request);
        var authenticatedDecision = FeedPolicy.Evaluate(authenticated, request);

        Assert.False(anonymousDecision.Allowed);
        Assert.Equal("FEED_POLICY_DENIED", anonymousDecision.ErrorCode);
        Assert.True(authenticatedDecision.Allowed);
    }

    [Fact]
    public async Task EmptyFacadeReturnsTypedDegradedPageWithoutFabricatedItems()
    {
        await using var database = new CoreDataStore(new ConfigurationBuilder().Build(), NullLogger<CoreDataStore>.Instance);
        var config = new FeedConfigService(database);
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var sessionFactory = NewFactory(cursorSigner);
        var limiter = new FeedRateLimiter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_ANONYMOUS_RATE_PER_MINUTE"] = "10"
        }).Build());
        var facade = new FeedFacadeService(config, sessionFactory, limiter, cursorSigner);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());

        var result = await facade.GetEmptyPageAsync(principal, ValidRequest(), "request-1", FixedNow, CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Response);
        Assert.Empty(result.Response!.Items);
        Assert.Null(result.Response.NextCursor);
        Assert.True(result.Response.Degraded);
        Assert.Equal("baseline", result.Response.ConfigVersion);
        Assert.Equal(FeedConfigContract.DeterministicRankingVersion, result.Response.RankingVersion);
        Assert.Equal("request-1", result.Response.RequestId);
    }

    [Fact]
    public async Task FacadeCarriesGeneratorFailureIntoAffectedModuleWithoutDroppingSafeItems()
    {
        await using var database = new CoreDataStore(new ConfigurationBuilder().Build(), NullLogger<CoreDataStore>.Instance);
        var config = new FeedConfigService(database);
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var sessionFactory = NewFactory(cursorSigner);
        var limiter = new FeedRateLimiter(new ConfigurationBuilder().Build());
        var dataPort = new FixedFeedDataPort(
            new FeedCandidatePage(
                new[] { new FeedCandidateRecord("PLACE", "place-1", "test", FixedNow.AddMinutes(-1), "place-1") },
                null,
                new[]
                {
                    new FeedGeneratorTelemetry("place-v1", "timeout", 0, 0, 0, 100, true, "FEED_GENERATOR_TIMEOUT")
                },
                true),
            new FeedPlaceItemContract("PLACE", "place-1", "raw-token", "place-1", "Place", "", "Sukhumvit", "Cafe", null, "POPULAR_PLACE"));
        var facade = new FeedFacadeService(config, sessionFactory, limiter, cursorSigner, dataPort);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());

        var result = await facade.GetPageAsync(principal, ValidRequest(), "request-degraded", FixedNow, CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Response);
        Assert.Single(result.Response!.Items);
        Assert.True(result.Response.Degraded);
        var module = Assert.Single(result.Response.Modules!);
        Assert.Equal("FOR_YOU", module.ModuleId);
        Assert.True(module.Degraded);
    }

    [Fact]
    public void RateLimiterReturnsConfiguredLimitAndRejectsTheNextRequest()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_ANONYMOUS_RATE_PER_MINUTE"] = "1"
        }).Build();
        var limiter = new FeedRateLimiter(configuration);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());

        var first = limiter.Check(principal, FixedNow);
        var second = limiter.Check(principal, FixedNow.AddSeconds(1));

        Assert.True(first.Allowed);
        Assert.Equal(1, first.Limit);
        Assert.False(second.Allowed);
        Assert.Equal(0, second.Remaining);
    }

    private static FeedSessionContextFactory NewFactory(FeedCursorSigner signer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
        }).Build();
        return new FeedSessionContextFactory(signer, configuration);
    }

    private static NormalizedFeedRequest ValidRequest() => new(
        "explore",
        "for_you",
        "coffee",
        "sukhumvit",
        new FeedCoarseLocationContract(null, "w4r", 2_000),
        null,
        24);

    private static FeedConfigRuntimeSnapshot RuntimeSnapshot()
    {
        return new FeedConfigRuntimeSnapshot(
            "ACTIVE_REVISION",
            "HEALTHY",
            Guid.NewGuid(),
            7,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            FixedNow);
    }

    private sealed class FixedFeedDataPort(
        FeedCandidatePage page,
        IFeedItemContract item) : IFeedDataPort
    {
        public Task<FeedCandidatePage> GetCandidatesAsync(FeedSessionContext session, CancellationToken cancellationToken) =>
            Task.FromResult(page);

        public Task<FeedHydrationResult> HydrateAsync(
            IReadOnlyList<FeedCandidateRecord> candidates,
            FeedSessionContext session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedHydrationResult(new[] { item }));

        public Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedDataPortHealth(true, "test"));
    }
}
