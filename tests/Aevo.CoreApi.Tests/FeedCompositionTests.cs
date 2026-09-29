using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedCompositionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 5, 0, 0, TimeSpan.Zero);
    private const string CursorSecret = "feed-composition-test-cursor-secret-0123456789";

    [Fact]
    public void CanonicalDedupePrefersTheHighestPriorityPublicProjection()
    {
        var storeIdentity = "store:00000000-0000-0000-0000-000000000001";
        var tracedee = new FeedCandidateRecord(
            "PLACE",
            "00000000-0000-0000-0000-000000000010",
            "tracedee-place",
            FixedNow.AddMinutes(-2),
            storeIdentity,
            storeIdentity,
            "00000000-0000-0000-0000-000000000010",
            20);
        var publicProjection = new FeedCandidateRecord(
            "PLACE",
            "00000000-0000-0000-0000-000000000001",
            "store-profile",
            FixedNow.AddMinutes(-3),
            storeIdentity,
            storeIdentity,
            "00000000-0000-0000-0000-000000000001",
            30);

        var result = FeedSessionOrdering.Deduplicate(new[] { tracedee, publicProjection });

        var winner = Assert.Single(result);
        Assert.Equal("store-profile", winner.Source);
        Assert.Equal(publicProjection.EntityId, winner.EntityId);
    }

    [Fact]
    public void StableOrderUsesTheSignedAnchorAndIgnoresContentInsertedAfterSessionCutoff()
    {
        var first = new FeedCandidateRecord("TRACE", "trace-a", "tracedee-trace", FixedNow.AddMinutes(-1), "trace-a");
        var second = new FeedCandidateRecord("PLACE", "place-a", "tracedee-place", FixedNow.AddMinutes(-2), "place-a");
        var insertedAfterSessionStart = new FeedCandidateRecord("TRACE", "trace-new", "tracedee-trace", FixedNow.AddSeconds(1), "trace-new");

        Assert.True(FeedOrderingAnchor.TryDecode(FeedOrderingAnchor.Encode(first), out var anchor));
        var resumed = FeedSessionOrdering.StableOrder(
            new[] { insertedAfterSessionStart, first, second },
            FixedNow,
            anchor);

        var candidate = Assert.Single(resumed);
        Assert.Equal(second.EntityId, candidate.EntityId);
        Assert.DoesNotContain(resumed, item => item.EntityId == insertedAfterSessionStart.EntityId);
    }

    [Fact]
    public void NewSessionCutoffUsesTheSameMillisecondPrecisionAsTheCursor()
    {
        var now = FixedNow.AddTicks(1234);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            now);
        var factory = new FeedSessionContextFactory(
            new FeedCursorSigner(CursorSecret),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());

        var context = factory.Create(principal, request, runtime, now).Context!;
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());

        Assert.Equal(expected, context.StartedAt);
        Assert.Equal(expected, context.CandidateCutoffAt);
    }

    [Fact]
    public void CursorCarriesAValidatedSeenKeyWindowAcrossPages()
    {
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            FixedNow);
        var factory = new FeedSessionContextFactory(
            new FeedCursorSigner(CursorSecret),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        var context = factory.Create(principal, request, runtime, FixedNow).Context!;
        var served = new FeedCandidateRecord("TRACE", "trace-seen", "test", FixedNow.AddMinutes(-1), "trace-seen");

        var cursor = factory.CreateCursor(context, served, new[] { served });
        var resumed = factory.Create(principal, request with { Cursor = cursor }, runtime, FixedNow.AddSeconds(1));

        Assert.True(resumed.IsValid);
        Assert.Contains("TRACE:trace-seen", resumed.Context!.SeenEntityKeys!);
    }

    [Fact]
    public void MaximumPageSeenWindowRemainsVerifiable()
    {
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 50);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            FixedNow);
        var factory = new FeedSessionContextFactory(
            new FeedCursorSigner(CursorSecret),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        var context = factory.Create(principal, request, runtime, FixedNow).Context!;
        var served = Enumerable.Range(0, FeedSeenKeyPolicy.MaximumKeys)
            .Select(index => new FeedCandidateRecord(
                "TRACE",
                $"{index:x32}",
                "test",
                FixedNow.AddMinutes(-index - 1),
                $"trace:{index:x32}"))
            .ToArray();

        var cursor = factory.CreateCursor(context, served[^1], served);
        var resumed = factory.Create(principal, request with { Cursor = cursor }, runtime, FixedNow.AddSeconds(1));

        Assert.True(resumed.IsValid);
        Assert.Equal(FeedSeenKeyPolicy.MaximumKeys, resumed.Context!.SeenEntityKeys!.Count);
    }

    [Fact]
    public async Task CandidateGenerationIsolatesOneFailedGeneratorAndKeepsTheSuccessfulSource()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "100"
        }).Build();
        await using var dataStore = new FeedCanonicalDataStore(configuration);
        var place = new FeedCandidateRecord(
            "PLACE",
            "00000000-0000-0000-0000-000000000001",
            "test-place",
            FixedNow.AddMinutes(-1),
            "place:00000000-0000-0000-0000-000000000001");
        var port = new CanonicalFeedDataPort(
            dataStore,
            new IFeedCandidateGenerator[]
            {
                new ThrowingGenerator("trace-v1"),
                new FixedGenerator("place-v1", place)
            },
            new FeedCursorSigner(CursorSecret),
            configuration,
            NullLogger<CanonicalFeedDataPort>.Instance);

        var session = CreateContext();
        var result = await port.GetCandidatesAsync(session, CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(place.EntityId, candidate.EntityId);
        Assert.True(result.Degraded);
        Assert.Contains(result.GeneratorResults, item => item.Generator == "trace-v1" && item.Status == "failed");
        Assert.Contains(result.GeneratorResults, item => item.Generator == "place-v1" && item.Status == "succeeded");
    }

    [Fact]
    public async Task CandidateGenerationIsolatesAPlaceFailureAndKeepsTraceCandidates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "100"
        }).Build();
        await using var dataStore = new FeedCanonicalDataStore(configuration);
        var trace = new FeedCandidateRecord(
            "TRACE",
            "00000000-0000-0000-0000-000000000002",
            "test-trace",
            FixedNow.AddMinutes(-1),
            "trace:00000000-0000-0000-0000-000000000002");
        var port = new CanonicalFeedDataPort(
            dataStore,
            new IFeedCandidateGenerator[]
            {
                new FixedGenerator("trace-v1", trace),
                new ThrowingGenerator("place-v1")
            },
            new FeedCursorSigner(CursorSecret),
            configuration,
            NullLogger<CanonicalFeedDataPort>.Instance);

        var result = await port.GetCandidatesAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(trace.EntityId, Assert.Single(result.Candidates).EntityId);
        Assert.True(result.Degraded);
        Assert.Contains(result.GeneratorResults, item => item.Generator == "place-v1" && item.Status == "failed");
    }

    [Fact]
    public async Task CandidateGenerationTurnsASlowGeneratorIntoAnIndependentTimeout()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "50"
        }).Build();
        await using var dataStore = new FeedCanonicalDataStore(configuration);
        var place = new FeedCandidateRecord(
            "PLACE",
            "00000000-0000-0000-0000-000000000003",
            "test-place",
            FixedNow.AddMinutes(-1),
            "place:00000000-0000-0000-0000-000000000003");
        var port = new CanonicalFeedDataPort(
            dataStore,
            new IFeedCandidateGenerator[]
            {
                new SlowGenerator("trace-v1", 250),
                new FixedGenerator("place-v1", place)
            },
            new FeedCursorSigner(CursorSecret),
            configuration,
            NullLogger<CanonicalFeedDataPort>.Instance);

        var result = await port.GetCandidatesAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(place.EntityId, Assert.Single(result.Candidates).EntityId);
        Assert.True(result.Degraded);
        Assert.Contains(result.GeneratorResults, item => item.Generator == "trace-v1" && item.Status == "timeout");
    }

    [Fact]
    public async Task DisabledSourcesReturnATypedSafeEmptyPageWithoutRunningGenerators()
    {
        var configuration = new ConfigurationBuilder().Build();
        await using var dataStore = new FeedCanonicalDataStore(configuration);
        var port = new CanonicalFeedDataPort(
            dataStore,
            new IFeedCandidateGenerator[]
            {
                new ThrowingGenerator("trace-v1"),
                new ThrowingGenerator("place-v1")
            },
            new FeedCursorSigner(CursorSecret),
            configuration,
            NullLogger<CanonicalFeedDataPort>.Instance);
        var context = CreateContext() with
        {
            RuntimeConfig = FeedConfigDefaults.Config with
            {
                CandidateSources = FeedConfigDefaults.Config.CandidateSources with
                {
                    Trace = new FeedCandidateSourceConfig(false, 0, 0),
                    Place = new FeedCandidateSourceConfig(false, 0, 0)
                }
            }
        };

        var result = await port.GetCandidatesAsync(context, CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.False(result.Degraded);
        Assert.All(result.GeneratorResults, item => Assert.Equal("disabled", item.Status));
    }

    [Fact]
    public async Task CandidateBudgetsAreClampedPerSourceAndAcrossTheMixedPool()
    {
        var configuration = new ConfigurationBuilder().Build();
        await using var dataStore = new FeedCanonicalDataStore(configuration);
        var traceGenerator = new BudgetRecordingGenerator("trace-v1");
        var placeGenerator = new BudgetRecordingGenerator("place-v1");
        var port = new CanonicalFeedDataPort(
            dataStore,
            new IFeedCandidateGenerator[] { traceGenerator, placeGenerator },
            new FeedCursorSigner(CursorSecret),
            configuration,
            NullLogger<CanonicalFeedDataPort>.Instance);

        var result = await port.GetCandidatesAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(96, traceGenerator.ObservedBudget);
        Assert.Equal(72, placeGenerator.ObservedBudget);
        Assert.Equal(160, result.Candidates.Count);
    }

    [Fact]
    public async Task FacadeReturnsAStableNextPageWithoutRepeatingThePreviousCandidate()
    {
        await using var database = new CoreDataStore(
            new ConfigurationBuilder().Build(),
            NullLogger<CoreDataStore>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
        }).Build();
        var config = new FeedConfigService(database);
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var sessionFactory = new FeedSessionContextFactory(cursorSigner, configuration);
        var rateLimiter = new FeedRateLimiter(configuration);
        var first = new FeedCandidateRecord("TRACE", "trace-a", "test", FixedNow.AddMinutes(-1), "trace-a");
        var second = new FeedCandidateRecord("PLACE", "place-a", "test", FixedNow.AddMinutes(-2), "place-a");
        var insertedAfterStart = new FeedCandidateRecord("TRACE", "trace-new", "test", FixedNow.AddSeconds(1), "trace-new");
        var dataPort = new FixedFeedDataPort(new[] { first, second, insertedAfterStart });
        var facade = new FeedFacadeService(config, sessionFactory, rateLimiter, cursorSigner, dataPort);
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 1);

        var pageOne = await facade.GetPageAsync(principal, request, "request-1", FixedNow, CancellationToken.None);

        Assert.NotNull(pageOne.Response);
        Assert.Equal(first.EntityId, Assert.Single(pageOne.Response!.Items).Id);
        Assert.NotNull(pageOne.Response.NextCursor);

        var pageTwo = await facade.GetPageAsync(
            principal,
            request with { Cursor = pageOne.Response.NextCursor },
            "request-2",
            FixedNow.AddSeconds(1),
            CancellationToken.None);

        Assert.NotNull(pageTwo.Response);
        Assert.Equal(second.EntityId, Assert.Single(pageTwo.Response!.Items).Id);
        Assert.Null(pageTwo.Response.NextCursor);
    }

    [Fact]
    public async Task FacadeSuppressesASeenCandidateWhenItsScoreMovesBehindTheCursor()
    {
        await using var database = new CoreDataStore(
            new ConfigurationBuilder().Build(),
            NullLogger<CoreDataStore>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
        }).Build();
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var facade = new FeedFacadeService(
            new FeedConfigService(database),
            new FeedSessionContextFactory(cursorSigner, configuration),
            new FeedRateLimiter(configuration),
            cursorSigner,
            new ReorderingSeenFeedDataPort());
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 1);

        var pageOne = await facade.GetPageAsync(principal, request, "request-seen-1", FixedNow, CancellationToken.None);
        var pageTwo = await facade.GetPageAsync(
            principal,
            request with { Cursor = pageOne.Response!.NextCursor },
            "request-seen-2",
            FixedNow.AddSeconds(1),
            CancellationToken.None);

        Assert.NotNull(pageOne.Response);
        Assert.Equal("seen", Assert.Single(pageOne.Response!.Items).Id);
        Assert.NotNull(pageOne.Response.NextCursor);
        Assert.NotNull(pageTwo.Response);
        Assert.Empty(pageTwo.Response!.Items);
        Assert.Null(pageTwo.Response.NextCursor);
    }

    [Fact]
    public async Task FacadeDoesNotServePostOrTracerAsDiscoveryCandidates()
    {
        await using var database = new CoreDataStore(
            new ConfigurationBuilder().Build(),
            NullLogger<CoreDataStore>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
        }).Build();
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var facade = new FeedFacadeService(
            new FeedConfigService(database),
            new FeedSessionContextFactory(cursorSigner, configuration),
            new FeedRateLimiter(configuration),
            cursorSigner,
            new FixedFeedDataPort(new[]
            {
                new FeedCandidateRecord("POST", "post-1", "legacy-post", FixedNow.AddMinutes(-1), "post-1"),
                new FeedCandidateRecord("TRACER", "tracer-1", "legacy-tracer", FixedNow.AddMinutes(-2), "tracer-1"),
                new FeedCandidateRecord("PLACE", "place-1", "place", FixedNow.AddMinutes(-3), "place-1")
            }));
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());

        var result = await facade.GetPageAsync(
            principal,
            new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 3),
            "request-entity-only",
            FixedNow,
            CancellationToken.None);

        Assert.NotNull(result.Response);
        var item = Assert.Single(result.Response!.Items);
        Assert.Equal("PLACE", item.ItemType);
    }

    [Fact]
    public async Task FacadeFailsClosedOnMismatchedHydrationAndRebindsThePublicItemToken()
    {
        await using var database = new CoreDataStore(
            new ConfigurationBuilder().Build(),
            NullLogger<CoreDataStore>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
        }).Build();
        var cursorSigner = new FeedCursorSigner(CursorSecret);
        var facade = new FeedFacadeService(
            new FeedConfigService(database),
            new FeedSessionContextFactory(cursorSigner, configuration),
            new FeedRateLimiter(configuration),
            cursorSigner,
            new UnsafeHydrationFeedDataPort());
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());

        var result = await facade.GetPageAsync(
            principal,
            new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 2),
            "request-hydration-boundary",
            FixedNow,
            CancellationToken.None);

        var item = Assert.Single(result.Response!.Items);
        Assert.Equal("PLACE", item.ItemType);
        Assert.True(result.Response.Degraded);
        var token = cursorSigner.VerifyItemToken(item.ItemToken, FixedNow);
        Assert.True(token.IsValid);
        Assert.False(token.IsLegacy);
        Assert.Equal("PLACE", token.Payload!.EntityType);
        Assert.Equal("place-safe", token.Payload.EntityId);
    }

    private static FeedSessionContext CreateContext()
    {
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            FixedNow);
        var factory = new FeedSessionContextFactory(
            new FeedCursorSigner(CursorSecret),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        return factory.Create(principal, request, runtime, FixedNow).Context!;
    }

    private sealed class FixedGenerator(string name, FeedCandidateRecord candidate) : IFeedCandidateGenerator
    {
        public string Name => name;

        public Task<FeedGeneratorOutput> GenerateAsync(
            FeedGeneratorContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedGeneratorOutput(Name, new[] { candidate }, 1, 1, 0, true));
    }

    private sealed class ThrowingGenerator(string name) : IFeedCandidateGenerator
    {
        public string Name => name;

        public Task<FeedGeneratorOutput> GenerateAsync(
            FeedGeneratorContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("test generator failure");
    }

    private sealed class SlowGenerator(string name, int delayMilliseconds) : IFeedCandidateGenerator
    {
        public string Name => name;

        public async Task<FeedGeneratorOutput> GenerateAsync(
            FeedGeneratorContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
            return new FeedGeneratorOutput(Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true);
        }
    }

    private sealed class BudgetRecordingGenerator(string name) : IFeedCandidateGenerator
    {
        public string Name => name;
        public int ObservedBudget { get; private set; }

        public Task<FeedGeneratorOutput> GenerateAsync(
            FeedGeneratorContext context,
            CancellationToken cancellationToken)
        {
            ObservedBudget = context.Budget;
            var candidates = Enumerable.Range(0, 200)
                .Select(index => new FeedCandidateRecord(
                    Name == "trace-v1" ? "TRACE" : "PLACE",
                    $"{Name}-{index}",
                    Name,
                    FixedNow.AddMinutes(-index - 1),
                    $"{Name}:{index}"))
                .ToArray();
            return Task.FromResult(new FeedGeneratorOutput(Name, candidates, candidates.Length, candidates.Length, 0, false));
        }
    }

    private static IFeedItemContract PublicItemFor(FeedCandidateRecord candidate) =>
        candidate.EntityType == "TRACE"
            ? new FeedTraceItemContract(
                "TRACE",
                candidate.EntityId,
                "test-item-token",
                candidate.EntityId,
                candidate.EntityId,
                string.Empty,
                "Test creator",
                string.Empty,
                Array.Empty<string>(),
                1,
                "NEW_TRACE",
                candidate.PublishedAt)
            : new FeedPlaceItemContract(
                "PLACE",
                candidate.EntityId,
                "test-item-token",
                candidate.EntityId,
                candidate.EntityId,
                string.Empty,
                candidate.EntityId,
                "test",
                null,
                "POPULAR_PLACE");

    private sealed class FixedFeedDataPort(IReadOnlyList<FeedCandidateRecord> candidates) : IFeedDataPort
    {
        public Task<FeedCandidatePage> GetCandidatesAsync(
            FeedSessionContext session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCandidatePage(candidates, null));

        public Task<FeedHydrationResult> HydrateAsync(
            IReadOnlyList<FeedCandidateRecord> requested,
            FeedSessionContext session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedHydrationResult(requested.Select(PublicItemFor).ToArray()));

        public Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedDataPortHealth(true, "test"));
    }

    private sealed class UnsafeHydrationFeedDataPort : IFeedDataPort
    {
        private static readonly IReadOnlyList<FeedCandidateRecord> Candidates = new[]
        {
            new FeedCandidateRecord("TRACE", "trace-unsafe", "test", FixedNow.AddMinutes(-1), "trace-unsafe"),
            new FeedCandidateRecord("PLACE", "place-safe", "test", FixedNow.AddMinutes(-2), "place-safe")
        };

        public Task<FeedCandidatePage> GetCandidatesAsync(
            FeedSessionContext session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedCandidatePage(Candidates, null));

        public Task<FeedHydrationResult> HydrateAsync(
            IReadOnlyList<FeedCandidateRecord> requested,
            FeedSessionContext session,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FeedHydrationResult(new IFeedItemContract[]
            {
                new FeedPlaceItemContract("TRACE", "trace-unsafe", "attacker-token", "trace-unsafe", "trace-unsafe", string.Empty, string.Empty, "test", null, "POPULAR_PLACE"),
                new FeedPlaceItemContract("PLACE", "place-safe", "attacker-token", "place-safe", "place-safe", string.Empty, string.Empty, "test", null, "POPULAR_PLACE")
            }));

        public Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedDataPortHealth(true, "test"));
    }

    private sealed class ReorderingSeenFeedDataPort : IFeedDataPort
    {
        private int candidateRequests;
        private int hydrationRequests;

        public Task<FeedCandidatePage> GetCandidatesAsync(
            FeedSessionContext session,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref candidateRequests);
            var seenQuality = requestNumber == 1 ? 0.9 : 0.1;
            var candidates = new[]
            {
                new FeedCandidateRecord(
                    "TRACE",
                    "seen",
                    "test",
                    FixedNow.AddMinutes(-1),
                    "seen",
                    Features: new FeedCandidateFeatureInput(QualityScore: seenQuality, QualityConfidence: 1)),
                new FeedCandidateRecord(
                    "TRACE",
                    "other",
                    "test",
                    FixedNow.AddMinutes(-2),
                    "other",
                    Features: new FeedCandidateFeatureInput(QualityScore: 0.05, QualityConfidence: 1))
            };
            return Task.FromResult(new FeedCandidatePage(candidates, null));
        }

        public Task<FeedHydrationResult> HydrateAsync(
            IReadOnlyList<FeedCandidateRecord> requested,
            FeedSessionContext session,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref hydrationRequests);
            var eligible = requestNumber == 1
                ? requested
                : requested.Where(candidate => candidate.EntityId == "seen").ToArray();
            return Task.FromResult(new FeedHydrationResult(eligible.Select(PublicItemFor).ToArray()));
        }

        public Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FeedDataPortHealth(true, "test"));
    }
}
