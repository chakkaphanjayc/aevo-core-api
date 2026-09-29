using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[Collection("Feed database integration")]
public sealed class FeedCanonicalDataIntegrationTests
{
    private const string CursorSecret = "feed-canonical-integration-cursor-secret-0123456789";
    private static readonly string[] CafeTags = ["cafe", "feed"];
    private static readonly string[] GalleryTags = ["gallery", "feed"];

    [Fact]
    public async Task RealCanonicalSourcesEnforceEligibilityDedupeHydrationAndGoSafety()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        var otherUserId = await ReadActiveUserAsync(connection, actorId);
        var activeStore = await ReadStoreAsync(connection, "ACTIVE");
        var inactiveStore = await ReadStoreAsync(connection, "INACTIVE");
        if (actorId is null || otherUserId is null || activeStore is null)
        {
            return;
        }

        var marker = $"feed004-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var eligibleTraceId = Guid.NewGuid();
        var privateTraceId = Guid.NewGuid();
        var moderatedTraceId = Guid.NewGuid();
        var removedTraceId = Guid.NewGuid();
        var blockedTraceId = Guid.NewGuid();
        var mutedTraceId = Guid.NewGuid();
        var categoryMatchedTraceId = Guid.NewGuid();
        var categoryMismatchedTraceId = Guid.NewGuid();
        var eligiblePlaceId = Guid.NewGuid();
        var duplicatePlaceId = Guid.NewGuid();
        var hiddenPlaceId = Guid.NewGuid();
        var removedPlaceId = Guid.NewGuid();
        var profileSlug = $"{marker}-profile";
        var inactiveProfileSlug = $"{marker}-inactive";

        try
        {
            await InsertTraceAsync(connection, eligibleTraceId, actorId.Value, $"{marker}-eligible-trace", "PUBLISHED", "PUBLIC", "VISIBLE", now.AddMinutes(-1), marker);
            await InsertTraceAsync(connection, privateTraceId, actorId.Value, $"{marker}-private-trace", "DRAFT", "PRIVATE", "VISIBLE", now.AddMinutes(-2), marker);
            await InsertTraceAsync(connection, moderatedTraceId, actorId.Value, $"{marker}-moderated-trace", "PUBLISHED", "PUBLIC", "UNDER_REVIEW", now.AddMinutes(-3), marker);
            await InsertTraceAsync(connection, removedTraceId, actorId.Value, $"{marker}-removed-trace", "REMOVED", "PUBLIC", "REMOVED", now.AddMinutes(-3), marker);
            await InsertTraceAsync(connection, blockedTraceId, otherUserId.Value, $"{marker}-blocked-trace", "PUBLISHED", "PUBLIC", "VISIBLE", now.AddMinutes(-4), marker);
            await InsertTraceAsync(connection, mutedTraceId, otherUserId.Value, $"{marker}-muted-trace", "PUBLISHED", "PUBLIC", "VISIBLE", now.AddMinutes(-5), marker);
            await InsertTraceAsync(connection, categoryMatchedTraceId, actorId.Value, $"{marker}-category-match", "PUBLISHED", "PUBLIC", "VISIBLE", now.AddMinutes(-6), marker, CafeTags);
            await InsertTraceAsync(connection, categoryMismatchedTraceId, actorId.Value, $"{marker}-category-mismatch", "PUBLISHED", "PUBLIC", "VISIBLE", now.AddMinutes(-7), marker, GalleryTags);
            await InsertSafetyRelationAsync(connection, "public.tracedee_user_blocks", actorId.Value, otherUserId.Value);
            await InsertSafetyRelationAsync(connection, "public.tracedee_user_mutes", actorId.Value, otherUserId.Value);

            await InsertPlaceAsync(connection, eligiblePlaceId, null, null, $"{marker}-eligible-place", "VISIBLE", now.AddMinutes(-1), 13.7563, 100.4925, marker);
            await InsertPlaceAsync(connection, duplicatePlaceId, activeStore.Id, activeStore.OrganizationId, $"{marker}-store-place", "VISIBLE", now.AddMinutes(-2), 13.7560, 100.4930, marker);
            await InsertPlaceAsync(connection, hiddenPlaceId, null, null, $"{marker}-hidden-place", "UNDER_REVIEW", now.AddMinutes(-3), 13.7561, 100.4931, marker);
            await InsertPlaceAsync(connection, removedPlaceId, null, null, $"{marker}-removed-place", "REMOVED", now.AddMinutes(-4), 13.7562, 100.4932, marker);
            await InsertStoreProfileAsync(connection, activeStore, profileSlug, marker, true, 13.7560, 100.4930);
            if (inactiveStore is not null)
            {
                await InsertStoreProfileAsync(connection, inactiveStore, inactiveProfileSlug, marker, true, 13.7560, 100.4930);
            }

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "1000"
                })
                .Build();
            await using var dataStore = new FeedCanonicalDataStore(configuration);
            Assert.True(dataStore.IsConfigured);
            Assert.True(await dataStore.CanConnectAsync(CancellationToken.None));
            var dataPort = new CanonicalFeedDataPort(
                dataStore,
                new IFeedCandidateGenerator[]
                {
                    new TraceFeedCandidateGenerator(dataStore),
                    new PlaceFeedCandidateGenerator(dataStore)
                },
                new FeedCursorSigner(CursorSecret),
                configuration,
                NullLogger<CanonicalFeedDataPort>.Instance);
            var sourceHealth = await dataPort.CheckAsync(CancellationToken.None);
            Assert.True(sourceHealth.Available, sourceHealth.FailureCode);
            Assert.Contains(sourceHealth.SourceHealth, source =>
                source.Source == FeedSourceHealthPolicy.TraceSource
                && source.EligibleCount > 0
                && source.Available);
            Assert.Contains(sourceHealth.SourceHealth, source =>
                source.Source == FeedSourceHealthPolicy.TraceDeePlaceSource
                && source.EligibleCount > 0
                && source.Available);
            var context = CreateContext(actorId.Value, now.AddMinutes(1), "for_you", marker, null);

            var candidatePage = await dataPort.GetCandidatesAsync(context, CancellationToken.None);
            var candidateIds = candidatePage.Candidates.Select(candidate => candidate.EntityId).ToHashSet(StringComparer.Ordinal);
            Assert.True(candidatePage.Candidates.Count > 0, string.Join("; ", candidatePage.GeneratorResults.Select(telemetry => $"{telemetry.Generator}:{telemetry.Status}:{telemetry.FailureCode}")));

            Assert.Contains(eligibleTraceId.ToString(), candidateIds);
            Assert.DoesNotContain(privateTraceId.ToString(), candidateIds);
            Assert.DoesNotContain(moderatedTraceId.ToString(), candidateIds);
            Assert.DoesNotContain(removedTraceId.ToString(), candidateIds);
            Assert.DoesNotContain(blockedTraceId.ToString(), candidateIds);
            Assert.DoesNotContain(mutedTraceId.ToString(), candidateIds);
            Assert.Contains(eligiblePlaceId.ToString(), candidateIds);
            Assert.Contains(activeStore.Id.ToString(), candidateIds);
            Assert.DoesNotContain(duplicatePlaceId.ToString(), candidateIds);
            Assert.DoesNotContain(hiddenPlaceId.ToString(), candidateIds);
            Assert.DoesNotContain(removedPlaceId.ToString(), candidateIds);
            if (inactiveStore is not null)
            {
                Assert.DoesNotContain(inactiveStore.Id.ToString(), candidateIds);
            }

            var categoryContext = CreateContext(
                actorId.Value,
                now.AddMinutes(1),
                "for_you",
                marker,
                null,
                new FeedDiscoveryIntentContract(null, "cafe", null, null));
            var categoryPage = await dataPort.GetCandidatesAsync(categoryContext, CancellationToken.None);
            Assert.Contains(categoryPage.Candidates, candidate => candidate.EntityId == categoryMatchedTraceId.ToString());
            Assert.DoesNotContain(categoryPage.Candidates, candidate => candidate.EntityId == categoryMismatchedTraceId.ToString());

            var literalPatternContext = CreateContext(
                actorId.Value,
                now.AddMinutes(1),
                "for_you",
                marker,
                null,
                new FeedDiscoveryIntentContract(null, "cafe%", null, null));
            var literalPatternPage = await dataPort.GetCandidatesAsync(literalPatternContext, CancellationToken.None);
            Assert.DoesNotContain(literalPatternPage.Candidates, candidate => candidate.EntityId == categoryMatchedTraceId.ToString());

            var duplicateCandidates = candidatePage.Candidates
                .Where(candidate => candidate.EntityType == "PLACE" && candidate.EffectiveCanonicalIdentity == $"store:{activeStore.Id}")
                .ToArray();
            var duplicateWinner = Assert.Single(duplicateCandidates);
            Assert.Equal("store-profile", duplicateWinner.Source);
            Assert.All(candidatePage.GeneratorResults, telemetry => Assert.Equal("succeeded", telemetry.Status));

            var hydration = await dataPort.HydrateAsync(candidatePage.Candidates, context, CancellationToken.None);
            var hydratedIds = hydration.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(eligibleTraceId.ToString(), hydratedIds);
            Assert.Contains(eligiblePlaceId.ToString(), hydratedIds);
            Assert.Contains(activeStore.Id.ToString(), hydratedIds);
            Assert.DoesNotContain(hiddenPlaceId.ToString(), hydratedIds);
            Assert.False(hydration.Degraded);

            var json = JsonSerializer.Serialize(hydration.Items);
            Assert.DoesNotContain("moderation", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("latitude", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("longitude", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rankScore", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"source\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"sourceId\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"rawSource\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.All(hydration.Items, item => Assert.StartsWith("feed-item-v1.", item.ItemToken, StringComparison.Ordinal));

            await SetTraceModerationStatusAsync(connection, eligibleTraceId, "UNDER_REVIEW");
            var staleHydration = await dataPort.HydrateAsync(candidatePage.Candidates, context, CancellationToken.None);
            Assert.DoesNotContain(staleHydration.Items, item => item.Id == eligibleTraceId.ToString());

            var disabledContext = context with
            {
                RuntimeConfig = context.RuntimeConfig with
                {
                    CandidateSources = context.RuntimeConfig.CandidateSources with
                    {
                        Trace = context.RuntimeConfig.CandidateSources.Trace with { Enabled = false, Budget = 0 }
                    }
                }
            };
            var disabledPage = await dataPort.GetCandidatesAsync(disabledContext, CancellationToken.None);
            Assert.DoesNotContain(disabledPage.Candidates, candidate => candidate.EntityType == "TRACE");
            Assert.Contains(disabledPage.GeneratorResults, telemetry => telemetry.Generator == "trace-v1" && telemetry.Status == "disabled");
        }
        finally
        {
            await DeleteFixtureAsync(
                connection,
                new[] { eligibleTraceId, privateTraceId, moderatedTraceId, removedTraceId, blockedTraceId, mutedTraceId, categoryMatchedTraceId, categoryMismatchedTraceId },
                new[] { eligiblePlaceId, duplicatePlaceId, hiddenPlaceId, removedPlaceId },
                profileSlug,
                inactiveProfileSlug,
                actorId.Value,
                otherUserId.Value);
        }
    }

    [Fact]
    public async Task NearbyGeneratorUsesServerDecodedGeohashAndExistingSpatialState()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        if (actorId is null) return;

        var marker = $"feed004-nearby-{Guid.NewGuid():N}";
        var nearbyId = Guid.NewGuid();
        var farId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await InsertPlaceAsync(connection, nearbyId, null, null, $"{marker}-nearby", "VISIBLE", now.AddMinutes(-1), 13.7563, 100.4925, marker);
            await InsertPlaceAsync(connection, farId, null, null, $"{marker}-far", "VISIBLE", now.AddMinutes(-2), 13.8500, 100.5000, marker);

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "1000"
                })
                .Build();
            await using var dataStore = new FeedCanonicalDataStore(configuration);
            var generator = new PlaceFeedCandidateGenerator(dataStore);
            var context = CreateContext(
                actorId.Value,
                now,
                "nearby",
                marker,
                new FeedCoarseLocationContract(null, "w4rqqb", 1_000));

            var output = await generator.GenerateAsync(
                new FeedGeneratorContext(context, context.RuntimeConfig.CandidateSources.Place, 10),
                CancellationToken.None);

            Assert.Contains(output.Candidates, candidate => candidate.EntityId == nearbyId.ToString());
            Assert.DoesNotContain(output.Candidates, candidate => candidate.EntityId == farId.ToString());
        }
        finally
        {
            await DeletePlacesAsync(connection, new[] { nearbyId, farId });
        }
    }

    [Fact]
    public async Task QualityAndTasteProjectionsStayPrivateAndDriveDeterministicRanking()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        if (actorId is null) return;

        var marker = $"feed005-ranking-{Guid.NewGuid():N}";
        var highQualityTraceId = Guid.NewGuid();
        var lowQualityTraceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            var publishedAt = now.AddMinutes(-5);
            await InsertTraceAsync(connection, highQualityTraceId, actorId.Value, $"{marker}-high", "PUBLISHED", "PUBLIC", "VISIBLE", publishedAt, marker);
            await InsertTraceAsync(connection, lowQualityTraceId, actorId.Value, $"{marker}-low", "PUBLISHED", "PUBLIC", "VISIBLE", publishedAt, marker);
            await InsertQualityProjectionAsync(connection, "TRACE", highQualityTraceId, 1, 0.95m, 1m);
            await InsertQualityProjectionAsync(connection, "TRACE", lowQualityTraceId, 1, 0.10m, 1m);
            await InsertTasteProjectionAsync(connection, actorId.Value, marker, 0.8m, 1, 1, now.AddMinutes(-1));
            await InsertTasteProjectionAsync(connection, actorId.Value, marker, 0.2m, 2, 5, now.AddMinutes(-1));

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            await using var dataStore = new FeedCanonicalDataStore(configuration);
            var dataPort = new CanonicalFeedDataPort(
                dataStore,
                new IFeedCandidateGenerator[] { new TraceFeedCandidateGenerator(dataStore) },
                new FeedCursorSigner(CursorSecret),
                configuration,
                NullLogger<CanonicalFeedDataPort>.Instance);
            var context = CreateContext(actorId.Value, now, "for_you", marker, null);

            var candidatePage = await dataPort.GetCandidatesAsync(context, CancellationToken.None);
            var highQuality = Assert.Single(candidatePage.Candidates, candidate => candidate.EntityId == highQualityTraceId.ToString());
            var lowQuality = Assert.Single(candidatePage.Candidates, candidate => candidate.EntityId == lowQualityTraceId.ToString());
            Assert.Equal(0.95, highQuality.Features!.QualityScore, 3);
            Assert.Equal(0.10, lowQuality.Features!.QualityScore, 3);
            Assert.Equal(0.2, highQuality.Features.AffinitySignal, 3);
            Assert.Equal(5, highQuality.Features.AffinityEvidenceCount);
            Assert.Equal(now.AddMinutes(-1), highQuality.Features.AffinityCalculatedAt);

            var ranked = FeedRankingPipeline.Rank(candidatePage.Candidates, context);
            Assert.True(
                ranked.Single(candidate => candidate.EntityId == highQualityTraceId.ToString()).RankingScore
                > ranked.Single(candidate => candidate.EntityId == lowQualityTraceId.ToString()).RankingScore);
            Assert.All(ranked, candidate => Assert.Null(candidate.ShadowScore));

            var hydration = await dataPort.HydrateAsync(candidatePage.Candidates, context, CancellationToken.None);
            var json = JsonSerializer.Serialize(hydration.Items);
            Assert.DoesNotContain("qualityScore", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("affinity", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteRankingFixtureAsync(connection, new[] { highQualityTraceId, lowQualityTraceId }, actorId.Value, marker);
        }
    }

    [Fact]
    public async Task ConnectedFacadeReturnsSignedEntityOnlyForYouModuleFromCanonicalSources()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        if (actorId is null) return;

        var marker = $"feed007-facade-{Guid.NewGuid():N}";
        var traceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await InsertTraceAsync(
                connection,
                traceId,
                actorId.Value,
                $"{marker}-trace",
                "PUBLISHED",
                "PUBLIC",
                "VISIBLE",
                now.AddMinutes(-1),
                marker);

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_CURSOR_SECRET"] = CursorSecret,
                    ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "2000",
                    ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
                })
                .Build();
            await using var coreDataStore = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
            await using var feedDataStore = new FeedCanonicalDataStore(configuration);
            var cursorSigner = new FeedCursorSigner(CursorSecret);
            var dataPort = new CanonicalFeedDataPort(
                feedDataStore,
                new IFeedCandidateGenerator[]
                {
                    new TraceFeedCandidateGenerator(feedDataStore),
                    new PlaceFeedCandidateGenerator(feedDataStore)
                },
                cursorSigner,
                configuration,
                NullLogger<CanonicalFeedDataPort>.Instance);
            var configService = new FeedConfigService(coreDataStore);
            var sessionFactory = new FeedSessionContextFactory(cursorSigner, configuration);
            var facade = new FeedFacadeService(
                configService,
                sessionFactory,
                new FeedRateLimiter(configuration),
                cursorSigner,
                dataPort);
            var principal = FeedPrincipalFactory.FromGoSession(new CoreSession(
                Guid.NewGuid(),
                actorId.Value,
                "GO",
                now.AddHours(1),
                null,
                null,
                "feed007@example.test",
                "Feed 007 Test",
                null,
                null));

            var request = new NormalizedFeedRequest("explore", "for_you", marker, "bangkok", null, null, 12);
            var result = await facade.GetPageAsync(
                principal,
                request,
                "feed007-connected",
                now,
                CancellationToken.None);

            Assert.Null(result.ErrorCode);
            Assert.NotNull(result.Response);
            Assert.False(result.Response!.Degraded);
            var item = Assert.Single(result.Response.Items);
            Assert.Equal("TRACE", item.ItemType);
            Assert.Equal(traceId.ToString(), item.Id);
            Assert.StartsWith("feed-item-v1.", item.ItemToken, StringComparison.Ordinal);

            var module = Assert.Single(result.Response.Modules!);
            Assert.Equal("FOR_YOU", module.ModuleId);
            Assert.Equal("NEAR_SELECTED_AREA", module.ReasonCode);
            var reference = Assert.Single(module.Items);
            Assert.Equal(item.ItemType, reference.ItemType);
            Assert.Equal(item.Id, reference.ItemId);
            Assert.Equal(item.ItemToken, reference.ItemToken);
        }
        finally
        {
            await DeleteFixtureAsync(
                connection,
                new[] { traceId },
                Array.Empty<Guid>(),
                string.Empty,
                string.Empty,
                actorId.Value,
                actorId.Value);
        }
    }

    [Fact]
    public async Task HydrationRechecksActiveNegativeFeedbackForTraceAndPlace()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        if (actorId is null) return;

        var marker = $"feed010-feedback-race-{Guid.NewGuid():N}";
        var traceId = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await InsertTraceAsync(
                connection,
                traceId,
                actorId.Value,
                $"{marker}-trace",
                "PUBLISHED",
                "PUBLIC",
                "VISIBLE",
                now.AddMinutes(-2),
                marker);
            await InsertPlaceAsync(
                connection,
                placeId,
                null,
                null,
                $"{marker}-place",
                "VISIBLE",
                now.AddMinutes(-1),
                13.7563,
                100.4925,
                marker);
            await InsertActiveNegativeFeedbackAsync(connection, actorId.Value, "TRACE", traceId.ToString());
            await InsertActiveNegativeFeedbackAsync(connection, actorId.Value, "PLACE", placeId.ToString());

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_CURSOR_SECRET"] = CursorSecret,
                    ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "2000"
                })
                .Build();
            await using var dataStore = new FeedCanonicalDataStore(configuration);
            var dataPort = new CanonicalFeedDataPort(
                dataStore,
                new IFeedCandidateGenerator[]
                {
                    new TraceFeedCandidateGenerator(dataStore),
                    new PlaceFeedCandidateGenerator(dataStore)
                },
                new FeedCursorSigner(CursorSecret),
                configuration,
                NullLogger<CanonicalFeedDataPort>.Instance);
            var context = CreateContext(actorId.Value, now, "for_you", marker, null);

            var candidatePage = await dataPort.GetCandidatesAsync(context, CancellationToken.None);
            Assert.DoesNotContain(candidatePage.Candidates, candidate => candidate.EntityId == traceId.ToString());
            Assert.DoesNotContain(candidatePage.Candidates, candidate => candidate.EntityId == placeId.ToString());

            var raceCandidates = new[]
            {
                new FeedCandidateRecord("TRACE", traceId.ToString(), "tracedee", now.AddMinutes(-2), traceId.ToString()),
                new FeedCandidateRecord("PLACE", placeId.ToString(), "tracedee-place", now.AddMinutes(-1), placeId.ToString())
            };
            var hydration = await dataPort.HydrateAsync(raceCandidates, context, CancellationToken.None);

            Assert.DoesNotContain(hydration.Items, item => item.Id == traceId.ToString());
            Assert.DoesNotContain(hydration.Items, item => item.Id == placeId.ToString());
            Assert.False(hydration.Degraded);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                "delete from aevo_feed_negative_feedback where actor_id = @actor_id and item_id in (@trace_id, @place_id); delete from public.tracedee_traces where id = @trace_uuid; delete from public.tracedee_places where id = @place_uuid;",
                connection);
            cleanup.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId.Value;
            cleanup.Parameters.Add("trace_id", NpgsqlDbType.Text).Value = traceId.ToString();
            cleanup.Parameters.Add("place_id", NpgsqlDbType.Text).Value = placeId.ToString();
            cleanup.Parameters.Add("trace_uuid", NpgsqlDbType.Uuid).Value = traceId;
            cleanup.Parameters.Add("place_uuid", NpgsqlDbType.Uuid).Value = placeId;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConnectedCanonicalSourcesFillInventoryBackedModulePortfolio()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection, null);
        var otherUserId = await ReadActiveUserAsync(connection, actorId);
        if (actorId is null || otherUserId is null) return;

        var marker = $"feed007-portfolio-{Guid.NewGuid():N}";
        var newTraceId = Guid.NewGuid();
        var popularTraceId = Guid.NewGuid();
        var fallbackTraceId = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            await InsertTraceAsync(
                connection,
                newTraceId,
                actorId.Value,
                $"{marker}-new-trace",
                "PUBLISHED",
                "PUBLIC",
                "VISIBLE",
                now.AddMinutes(-1),
                marker);
            await InsertTraceAsync(
                connection,
                popularTraceId,
                actorId.Value,
                $"{marker}-popular-trace",
                "PUBLISHED",
                "PUBLIC",
                "VISIBLE",
                now.AddMinutes(-2),
                marker);
            await InsertTraceAsync(
                connection,
                fallbackTraceId,
                actorId.Value,
                $"{marker}-fallback-trace",
                "PUBLISHED",
                "PUBLIC",
                "VISIBLE",
                now.AddMinutes(-4),
                marker);
            var insertedSaveCount = await InsertTraceSavesAsync(connection, popularTraceId, actorId.Value, 5);
            Assert.True(insertedSaveCount >= 5, "The connected portfolio smoke needs five active test actors to classify a TRACE as popular.");
            await InsertPlaceAsync(
                connection,
                placeId,
                null,
                null,
                $"{marker}-place",
                "VISIBLE",
                now.AddMinutes(-3),
                13.7563,
                100.4925,
                marker);

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_CURSOR_SECRET"] = CursorSecret,
                    ["AEVO_FEED_GENERATOR_TIMEOUT_MS"] = "2000",
                    ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
                })
                .Build();
            await using var feedDataStore = new FeedCanonicalDataStore(configuration);
            var dataPort = new CanonicalFeedDataPort(
                feedDataStore,
                new IFeedCandidateGenerator[]
                {
                    new TraceFeedCandidateGenerator(feedDataStore),
                    new PlaceFeedCandidateGenerator(feedDataStore)
                },
                new FeedCursorSigner(CursorSecret),
                configuration,
                NullLogger<CanonicalFeedDataPort>.Instance);
            var portfolioOrder = new[]
            {
                "FOR_YOU",
                "NEAR_SELECTED_AREA",
                "NEW_AND_USEFUL",
                "COMMUNITY_FAVORITES"
            };
            var runtimeConfig = FeedConfigDefaults.Config with
            {
                Discovery = FeedDiscoveryConfigDefaults.Config with
                {
                    Modules = new FeedDiscoveryModulesConfig(true, 1, 12, portfolioOrder)
                }
            };
            var context = CreateContext(
                actorId.Value,
                now,
                "for_you",
                marker,
                null,
                runtimeConfig: runtimeConfig);
            context = context with { Area = "bangkok" };

            var candidatePage = await dataPort.GetCandidatesAsync(context, CancellationToken.None);
            Assert.False(candidatePage.Degraded, string.Join("; ", candidatePage.GeneratorResults.Select(telemetry => $"{telemetry.Generator}:{telemetry.Status}:{telemetry.FailureCode}")));
            Assert.Contains(candidatePage.Candidates, candidate => candidate.EntityId == newTraceId.ToString());
            Assert.Contains(candidatePage.Candidates, candidate => candidate.EntityId == popularTraceId.ToString());
            Assert.Contains(candidatePage.Candidates, candidate => candidate.EntityId == fallbackTraceId.ToString());
            Assert.Contains(candidatePage.Candidates, candidate => candidate.EntityId == placeId.ToString());

            var ranked = FeedRankingPipeline.Rank(candidatePage.Candidates, context);
            var ordered = FeedSessionOrdering.StableOrder(ranked, context.CandidateCutoffAt);
            var hydration = await dataPort.HydrateAsync(ordered, context, CancellationToken.None);
            Assert.False(hydration.Degraded);
            Assert.Contains(hydration.Items, item => item.Id == newTraceId.ToString());
            Assert.Contains(hydration.Items, item => item.Id == popularTraceId.ToString());
            Assert.Contains(hydration.Items, item => item.Id == fallbackTraceId.ToString());
            Assert.Contains(hydration.Items, item => item.Id == placeId.ToString());

            var modules = DiscoveryModuleSelector.Select(
                hydration.Items,
                context.Area,
                context.DiscoveryIntent,
                runtimeConfig.Discovery!.Modules);
            Assert.Equal(portfolioOrder, modules.Select(module => module.ModuleId));
            Assert.Contains(modules[2].Items, item => item.ItemId == newTraceId.ToString()
                || item.ItemId == fallbackTraceId.ToString());
            Assert.DoesNotContain(modules[2].Items, item => item.ItemId == popularTraceId.ToString());
            Assert.Contains(modules[3].Items, item => item.ItemId == popularTraceId.ToString()
                || item.ItemId == placeId.ToString());
            var moduleKeys = modules
                .SelectMany(module => module.Items)
                .Select(item => $"{item.ItemType}:{item.ItemId}")
                .ToArray();
            Assert.Equal(moduleKeys.Length, moduleKeys.Distinct(StringComparer.Ordinal).Count());

            var itemKeys = hydration.Items
                .Select(item => $"{item.ItemType}:{item.Id}")
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(
                modules.SelectMany(module => module.Items),
                reference => Assert.Contains($"{reference.ItemType}:{reference.ItemId}", itemKeys));
        }
        finally
        {
            await DeleteFixtureAsync(
                connection,
                new[] { newTraceId, popularTraceId, fallbackTraceId },
                new[] { placeId },
                string.Empty,
                string.Empty,
                actorId.Value,
                otherUserId.Value);
        }
    }

    private static FeedSessionContext CreateContext(
        Guid userId,
        DateTimeOffset now,
        string tab,
        string query,
        FeedCoarseLocationContract? coarseLocation,
        FeedDiscoveryIntentContract? discoveryIntent = null,
        FeedRuntimeConfig? runtimeConfig = null)
    {
        var principal = FeedPrincipalFactory.FromGoSession(new CoreSession(
            Guid.NewGuid(),
            userId,
            "GO",
            now.AddHours(1),
            null,
            null,
            "feed004@example.test",
            "Feed 004 Test",
            null,
            null));
        var request = new NormalizedFeedRequest("explore", tab, query, null, coarseLocation, null, 50, discoveryIntent);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            runtimeConfig ?? FeedConfigDefaults.Config,
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
        return factory.Create(principal, request, runtime, now).Context!;
    }

    private static async Task InsertTraceAsync(
        NpgsqlConnection connection,
        Guid id,
        Guid creatorId,
        string slug,
        string status,
        string visibility,
        string moderationStatus,
        DateTimeOffset publishedAt,
        string marker,
        IReadOnlyList<string>? topicTags = null)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.tracedee_traces
              (id, creator_id, slug, title, description, status, visibility, revision, area, topic_tags, published_at, created_at, updated_at, moderation_status)
            values
              (@id, @creator_id, @slug, @title, @description, @status, @visibility, 1, 'Bangkok', @topic_tags, @published_at, @published_at, @published_at, @moderation_status)
            """,
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("creator_id", NpgsqlDbType.Uuid).Value = creatorId;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = slug;
        command.Parameters.Add("title", NpgsqlDbType.Text).Value = $"{marker} title";
        command.Parameters.Add("description", NpgsqlDbType.Text).Value = $"{marker} description";
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = status;
        command.Parameters.Add("visibility", NpgsqlDbType.Text).Value = visibility;
        command.Parameters.Add("topic_tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = (topicTags ?? new[] { marker, "feed" }).ToArray();
        command.Parameters.Add("published_at", NpgsqlDbType.TimestampTz).Value = publishedAt;
        command.Parameters.Add("moderation_status", NpgsqlDbType.Text).Value = moderationStatus;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertTraceSavesAsync(
        NpgsqlConnection connection,
        Guid traceId,
        Guid excludedUserId,
        int count)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.tracedee_trace_saves (trace_id, user_id)
            select @trace_id, u.id
            from auth.users u
            join public.user_profiles p on p.id = u.id and p.status = 'ACTIVE'
            where u.id <> @excluded_user_id
            order by u.created_at asc, u.id asc
            limit @count
            on conflict do nothing
            """,
            connection);
        command.Parameters.Add("trace_id", NpgsqlDbType.Uuid).Value = traceId;
        command.Parameters.Add("excluded_user_id", NpgsqlDbType.Uuid).Value = excludedUserId;
        command.Parameters.Add("count", NpgsqlDbType.Integer).Value = count;
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPlaceAsync(
        NpgsqlConnection connection,
        Guid id,
        Guid? storeId,
        Guid? organizationId,
        string slug,
        string moderationStatus,
        DateTimeOffset createdAt,
        double latitude,
        double longitude,
        string marker)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.tracedee_places
              (id, source_kind, store_id, organization_id, slug, name, area, category, topic_tags, description, latitude, longitude, moderation_status, created_at, updated_at)
            values
              (@id, @source_kind, @store_id, @organization_id, @slug, @name, 'Bangkok', 'CAFE', @topic_tags, @description, @latitude, @longitude, @moderation_status, @created_at, @created_at)
            """,
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("source_kind", NpgsqlDbType.Text).Value = storeId is null ? "EXTERNAL" : "STORE";
        command.Parameters.Add("store_id", NpgsqlDbType.Uuid).Value = (object?)storeId ?? DBNull.Value;
        command.Parameters.Add("organization_id", NpgsqlDbType.Uuid).Value = (object?)organizationId ?? DBNull.Value;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = slug;
        command.Parameters.Add("name", NpgsqlDbType.Text).Value = $"{marker} place";
        command.Parameters.Add("topic_tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = new[] { "feed", "cafe" };
        command.Parameters.Add("description", NpgsqlDbType.Text).Value = $"{marker} place description";
        command.Parameters.Add("latitude", NpgsqlDbType.Numeric).Value = latitude;
        command.Parameters.Add("longitude", NpgsqlDbType.Numeric).Value = longitude;
        command.Parameters.Add("moderation_status", NpgsqlDbType.Text).Value = moderationStatus;
        command.Parameters.Add("created_at", NpgsqlDbType.TimestampTz).Value = createdAt;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertStoreProfileAsync(
        NpgsqlConnection connection,
        StoreIdentity store,
        string publicSlug,
        string marker,
        bool enabled,
        double latitude,
        double longitude)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.customer_store_profiles
              (store_id, organization_id, public_slug, public_enabled, area, category, description, latitude, longitude)
            values
              (@store_id, @organization_id, @public_slug, @public_enabled, 'Bangkok', 'CAFE', @description, @latitude, @longitude)
            """,
            connection);
        command.Parameters.Add("store_id", NpgsqlDbType.Uuid).Value = store.Id;
        command.Parameters.Add("organization_id", NpgsqlDbType.Uuid).Value = store.OrganizationId;
        command.Parameters.Add("public_slug", NpgsqlDbType.Text).Value = publicSlug;
        command.Parameters.Add("public_enabled", NpgsqlDbType.Boolean).Value = enabled;
        command.Parameters.Add("description", NpgsqlDbType.Text).Value = $"{marker} public profile";
        command.Parameters.Add("latitude", NpgsqlDbType.Numeric).Value = latitude;
        command.Parameters.Add("longitude", NpgsqlDbType.Numeric).Value = longitude;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertActiveNegativeFeedbackAsync(
        NpgsqlConnection connection,
        Guid actorId,
        string itemType,
        string itemId)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_feed_negative_feedback
              (actor_id, app_code, item_type, item_id, feed_session_id, item_token_hash, action, reason_code, active)
            values
              (@actor_id, 'GO', @item_type, @item_id, @feed_session_id, @item_token_hash, 'HIDE', 'OTHER', true)
            on conflict (actor_id, app_code, item_type, item_id) do update set
              active = true,
              updated_at = now()
            """,
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("item_type", NpgsqlDbType.Text).Value = itemType;
        command.Parameters.Add("item_id", NpgsqlDbType.Text).Value = itemId;
        command.Parameters.Add("feed_session_id", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        command.Parameters.Add("item_token_hash", NpgsqlDbType.Text).Value = new string('a', 64);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSafetyRelationAsync(NpgsqlConnection connection, string table, Guid actorId, Guid targetId)
    {
        await using var command = new NpgsqlCommand(
            $"insert into {table} ({(table.EndsWith("blocks", StringComparison.Ordinal) ? "blocker_id" : "muter_id")}, {(table.EndsWith("blocks", StringComparison.Ordinal) ? "blocked_id" : "muted_id")}) values (@actor_id, @target_id)",
            connection);
        command.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
        command.Parameters.Add("target_id", NpgsqlDbType.Uuid).Value = targetId;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetTraceModerationStatusAsync(NpgsqlConnection connection, Guid traceId, string status)
    {
        await using var command = new NpgsqlCommand("update public.tracedee_traces set moderation_status = @status where id = @id", connection);
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = status;
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = traceId;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertQualityProjectionAsync(
        NpgsqlConnection connection,
        string entityType,
        Guid entityId,
        int scoreVersion,
        decimal score,
        decimal confidence)
    {
        await using var command = new NpgsqlCommand(
            "insert into public.tracedee_content_quality_scores (entity_type, entity_id, score_version, score, confidence) values (@entity_type, @entity_id, @score_version, @score, @confidence)",
            connection);
        command.Parameters.Add("entity_type", NpgsqlDbType.Text).Value = entityType;
        command.Parameters.Add("entity_id", NpgsqlDbType.Uuid).Value = entityId;
        command.Parameters.Add("score_version", NpgsqlDbType.Integer).Value = scoreVersion;
        command.Parameters.Add("score", NpgsqlDbType.Numeric).Value = score;
        command.Parameters.Add("confidence", NpgsqlDbType.Numeric).Value = confidence;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTasteProjectionAsync(
        NpgsqlConnection connection,
        Guid profileId,
        string dimensionKey,
        decimal affinity,
        int scoreVersion,
        int evidenceCount,
        DateTimeOffset calculatedAt)
    {
        await using var command = new NpgsqlCommand(
            "insert into public.tracedee_taste_affinities (profile_id, dimension_type, dimension_key, score_version, affinity, evidence_count, calculated_at) values (@profile_id, 'TOPIC', @dimension_key, @score_version, @affinity, @evidence_count, @calculated_at)",
            connection);
        command.Parameters.Add("profile_id", NpgsqlDbType.Uuid).Value = profileId;
        command.Parameters.Add("dimension_key", NpgsqlDbType.Text).Value = dimensionKey;
        command.Parameters.Add("score_version", NpgsqlDbType.Integer).Value = scoreVersion;
        command.Parameters.Add("affinity", NpgsqlDbType.Numeric).Value = affinity;
        command.Parameters.Add("evidence_count", NpgsqlDbType.Integer).Value = evidenceCount;
        command.Parameters.Add("calculated_at", NpgsqlDbType.TimestampTz).Value = calculatedAt;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteRankingFixtureAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> traceIds,
        Guid actorId,
        string dimensionKey)
    {
        await using (var quality = new NpgsqlCommand(
            "delete from public.tracedee_content_quality_scores where entity_type = 'TRACE' and entity_id = any(@ids)",
            connection))
        {
            quality.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = traceIds.ToArray();
            await quality.ExecuteNonQueryAsync();
        }

        await using (var taste = new NpgsqlCommand(
            "delete from public.tracedee_taste_affinities where profile_id = @profile_id and dimension_type = 'TOPIC' and dimension_key = @dimension_key",
            connection))
        {
            taste.Parameters.Add("profile_id", NpgsqlDbType.Uuid).Value = actorId;
            taste.Parameters.Add("dimension_key", NpgsqlDbType.Text).Value = dimensionKey;
            await taste.ExecuteNonQueryAsync();
        }

        await using var traces = new NpgsqlCommand("delete from public.tracedee_traces where id = any(@ids)", connection);
        traces.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = traceIds.ToArray();
        await traces.ExecuteNonQueryAsync();
    }

    private static async Task DeleteFixtureAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> traceIds,
        IReadOnlyList<Guid> placeIds,
        string profileSlug,
        string inactiveProfileSlug,
        Guid actorId,
        Guid otherUserId)
    {
        await using (var safety = new NpgsqlCommand(
            "delete from public.tracedee_user_blocks where blocker_id = @actor_id and blocked_id = @target_id; delete from public.tracedee_user_mutes where muter_id = @actor_id and muted_id = @target_id;",
            connection))
        {
            safety.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId;
            safety.Parameters.Add("target_id", NpgsqlDbType.Uuid).Value = otherUserId;
            await safety.ExecuteNonQueryAsync();
        }

        await using (var profiles = new NpgsqlCommand(
            "delete from public.customer_store_profiles where public_slug in (@profile_slug, @inactive_profile_slug)",
            connection))
        {
            profiles.Parameters.Add("profile_slug", NpgsqlDbType.Text).Value = profileSlug;
            profiles.Parameters.Add("inactive_profile_slug", NpgsqlDbType.Text).Value = inactiveProfileSlug;
            await profiles.ExecuteNonQueryAsync();
        }

        await using (var traces = new NpgsqlCommand("delete from public.tracedee_traces where id = any(@ids)", connection))
        {
            traces.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = traceIds.ToArray();
            await traces.ExecuteNonQueryAsync();
        }

        await DeletePlacesAsync(connection, placeIds);
    }

    private static async Task DeletePlacesAsync(NpgsqlConnection connection, IReadOnlyList<Guid> ids)
    {
        await using var command = new NpgsqlCommand("delete from public.tracedee_places where id = any(@ids)", connection);
        command.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = ids.ToArray();
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid?> ReadActiveUserAsync(NpgsqlConnection connection, Guid? excluded)
    {
        await using var command = new NpgsqlCommand(
            """
            select u.id
            from auth.users u
            join public.user_profiles p on p.id = u.id and p.status = 'ACTIVE'
            where (@excluded is null or u.id <> @excluded)
            order by u.created_at asc, u.id asc
            limit 1
            """,
            connection);
        command.Parameters.Add("excluded", NpgsqlDbType.Uuid).Value = (object?)excluded ?? DBNull.Value;
        return await command.ExecuteScalarAsync() as Guid?;
    }

    private static async Task<StoreIdentity?> ReadStoreAsync(NpgsqlConnection connection, string status)
    {
        await using var command = new NpgsqlCommand(
            "select s.id, s.organization_id from public.stores s join public.organizations o on o.id = s.organization_id and o.status = 'ACTIVE' where s.status = @status and not exists (select 1 from public.customer_store_profiles p where p.store_id = s.id) order by s.id asc limit 1",
            connection);
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = status;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new StoreIdentity(reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var raw = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Database integration is not configured.");
        var connection = new NpgsqlConnection(NormalizeDatabaseConnectionString(raw));
        await connection.OpenAsync();
        return connection;
    }

    private static bool DatabaseConfigured() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVO_DATABASE_URL"));

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "postgres" and not "postgresql"))
        {
            throw new InvalidOperationException("The database integration URL is not PostgreSQL.");
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
            if (pair.Length != 2 || !string.Equals(Uri.UnescapeDataString(pair[0]), "sslmode", StringComparison.OrdinalIgnoreCase)) continue;
            builder.SslMode = Uri.UnescapeDataString(pair[1]).ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("The database integration URL has an unsupported sslmode.")
            };
        }
        return builder.ConnectionString;
    }

    private sealed record StoreIdentity(Guid Id, Guid OrganizationId);
}
