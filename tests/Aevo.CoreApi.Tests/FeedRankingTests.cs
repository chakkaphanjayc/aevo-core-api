using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedRankingTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 5, 0, 0, TimeSpan.Zero);
    private static readonly string[] TieBreakOrder = ["trace-a", "trace-b"];
    private static readonly string[] TraceReasonCodes = ["FOLLOWING_TRACER", "TASTE_MATCH", "POPULAR", "NEW_TRACE"];
    private static readonly string[] PlaceReasonCodes = ["NEARBY_PLACE", "POPULAR_PLACE"];
    private static readonly string[] ArtTags = ["art"];
    private static readonly string[] CoffeeTags = ["coffee"];

    [Fact]
    public void EqualScoresUseTheStableDeterministicTieBreak()
    {
        var context = CreateContext();
        var first = Candidate("TRACE", "trace-b", FixedNow.AddHours(-1), new FeedCandidateFeatureInput(QualityScore: 0.6));
        var second = Candidate("TRACE", "trace-a", FixedNow.AddHours(-1), new FeedCandidateFeatureInput(QualityScore: 0.6));

        var ranked = FeedRankingPipeline.Rank(new[] { first, second }, context);
        var ordered = FeedSessionOrdering.StableOrder(ranked, FixedNow);

        Assert.Equal(TieBreakOrder, ordered.Select(candidate => candidate.EntityId));
        Assert.All(ranked, candidate => Assert.InRange(candidate.RankingScore!.Value, 0, 1));
    }

    [Fact]
    public void CalibrationBoundsUntrustedProjectionValuesAndKeepsScoresFinite()
    {
        var context = CreateContext();
        var candidates = new[]
        {
            Candidate("TRACE", "high", FixedNow.AddMinutes(-1), new FeedCandidateFeatureInput(
                QualityScore: 10_000,
                QualityConfidence: 10_000,
                PopularitySignal: 10_000,
                PenaltySignal: -10_000)),
            Candidate("PLACE", "low", FixedNow.AddMinutes(-2), new FeedCandidateFeatureInput(
                QualityScore: -10_000,
                QualityConfidence: -10_000,
                PopularitySignal: double.PositiveInfinity,
                PenaltySignal: double.NaN))
        };

        var ranked = FeedRankingPipeline.Rank(candidates, context);

        Assert.All(ranked, candidate =>
        {
            Assert.True(double.IsFinite(candidate.RankingScore!.Value));
            Assert.InRange(candidate.RankingScore.Value, 0, 1);
        });
    }

    [Fact]
    public void ShadowPersonalizationIsComputedButCannotChangeServedOrder()
    {
        var config = FeedConfigDefaults.Config with
        {
            Ranking = FeedConfigDefaults.Config.Ranking with { Mode = "SHADOW" }
        };
        var context = CreateContext(config, authenticated: true);
        var qualityCandidate = Candidate(
            "TRACE",
            "quality",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(QualityScore: 0.8, QualityConfidence: 1));
        var affinityCandidate = Candidate(
            "TRACE",
            "affinity",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(
                QualityScore: 0.6,
                QualityConfidence: 1,
                AffinitySignal: 1,
                AffinityEvidenceCount: FeedFeatureProjection.FullTasteConfidenceEvidence,
                AffinityCalculatedAt: FixedNow));

        var ranked = FeedRankingPipeline.Rank(new[] { affinityCandidate, qualityCandidate }, context);
        var served = FeedSessionOrdering.StableOrder(ranked, FixedNow);

        Assert.Equal("quality", served[0].EntityId);
        Assert.All(ranked, candidate => Assert.NotNull(candidate.ShadowScore));
        Assert.True(ranked.Single(candidate => candidate.EntityId == "affinity").ShadowScore
            > ranked.Single(candidate => candidate.EntityId == "quality").ShadowScore);
    }

    [Fact]
    public void ExplicitDiscoveryIntentTakesPriorityOverHistoricalTaste()
    {
        var config = FeedConfigDefaults.Config with
        {
            Ranking = FeedConfigDefaults.Config.Ranking with { Mode = "SHADOW" }
        };
        var context = CreateContext(config, authenticated: true) with
        {
            Area = "Ari",
            DiscoveryIntent = new FeedDiscoveryIntentContract("art", null, null, null)
        };
        var intentMatch = Candidate(
            "TRACE",
            "intent-match",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(
                Area: "Ari",
                TopicTags: ArtTags,
                QualityScore: 0.45,
                QualityConfidence: 1));
        var historicalTaste = Candidate(
            "TRACE",
            "historical-taste",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(
                Area: "Ari",
                TopicTags: CoffeeTags,
                QualityScore: 0.95,
                QualityConfidence: 1,
                AffinitySignal: 1,
                AffinityEvidenceCount: FeedFeatureProjection.FullTasteConfidenceEvidence,
                AffinityCalculatedAt: FixedNow));

        var ranked = FeedRankingPipeline.Rank(new[] { historicalTaste, intentMatch }, context);
        var matched = ranked.Single(candidate => candidate.EntityId == "intent-match");
        var historical = ranked.Single(candidate => candidate.EntityId == "historical-taste");

        Assert.True(matched.RankingScore > historical.RankingScore);
        Assert.True(matched.ShadowScore > historical.ShadowScore);
    }

    [Fact]
    public void ExplicitDiscoveryIntentUsesConfiguredPriorityWeight()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = FeedConfigDefaults.Config.Discovery! with { IntentMatchWeight = 0.8m }
        };
        var context = CreateContext(config) with
        {
            Area = "Ari",
            DiscoveryIntent = new FeedDiscoveryIntentContract("art", null, null, null)
        };
        var candidate = Candidate(
            "TRACE",
            "intent-match",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(Area: "Ari", TopicTags: ArtTags, QualityScore: 0.2, QualityConfidence: 1));

        var ranked = FeedRankingPipeline.Rank(new[] { candidate }, context);

        Assert.InRange(ranked.Single().RankingScore!.Value, 0.895, 0.896);
    }

    [Fact]
    public void LegacyInMemoryRuntimeWithoutDiscoveryUsesSafeIntentDefaults()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = null
        };
        var context = CreateContext(config) with
        {
            Area = "Ari",
            DiscoveryIntent = new FeedDiscoveryIntentContract("art", null, null, null)
        };
        var candidate = Candidate(
            "TRACE",
            "intent-match",
            FixedNow.AddHours(-1),
            new FeedCandidateFeatureInput(Area: "Ari", TopicTags: ArtTags, QualityScore: 0.45, QualityConfidence: 1));

        var ranked = FeedRankingPipeline.Rank(new[] { candidate }, context);

        Assert.True(ranked.Single().RankingScore > 0.6);
    }

    [Fact]
    public void KillSwitchDisablesShadowScoreEvenForAuthenticatedSessions()
    {
        var config = FeedConfigDefaults.Config with
        {
            KillSwitch = true,
            Ranking = FeedConfigDefaults.Config.Ranking with { Mode = "LIVE" }
        };

        var ranked = FeedRankingPipeline.Rank(
            new[]
            {
                Candidate(
                    "TRACE",
                    "trace",
                    FixedNow.AddMinutes(-1),
                    new FeedCandidateFeatureInput(
                        AffinitySignal: 1,
                        AffinityEvidenceCount: FeedFeatureProjection.MinimumTasteEvidence,
                        AffinityCalculatedAt: FixedNow))
            },
            CreateContext(config, authenticated: true));

        Assert.Null(Assert.Single(ranked).ShadowScore);
    }

    [Fact]
    public void DiversitySelectorLimitsConsecutiveTypesAndCreatorConcentration()
    {
        var pool = Enumerable.Range(0, 8)
            .Select(index => Candidate(
                index % 2 == 0 ? "TRACE" : "PLACE",
                $"candidate-{index}",
                FixedNow.AddMinutes(-index),
                new FeedCandidateFeatureInput(
                    CreatorKey: index < 4 ? "creator-a" : $"creator-{index}",
                    IsExplorationCandidate: index >= 6)))
            .ToArray();

        var selected = FeedDiversitySelector.Select(pool, FeedConfigDefaults.Config, 6);

        Assert.Equal(6, selected.Count);
        Assert.Contains(selected, candidate => candidate.EntityType == "TRACE");
        Assert.Contains(selected, candidate => candidate.EntityType == "PLACE");
        Assert.True(MaxConsecutiveTypes(selected) <= FeedConfigDefaults.Config.Diversity.MaxConsecutiveSameSource);
        Assert.True(selected.Count(candidate => candidate.Features?.CreatorKey == "creator-a") <= 2);
    }

    [Fact]
    public void ExplorationQuotaIsMetWhenExplorationCandidatesExist()
    {
        var pool = Enumerable.Range(0, 6)
            .Select(index => Candidate(
                "TRACE",
                $"candidate-{index}",
                FixedNow.AddMinutes(-index),
                new FeedCandidateFeatureInput(IsExplorationCandidate: index >= 4)))
            .ToArray();
        var config = FeedConfigDefaults.Config with
        {
            Diversity = FeedConfigDefaults.Config.Diversity with { ExplorationQuota = 0.25m }
        };

        var selected = FeedDiversitySelector.Select(pool, config, 4);

        Assert.Equal(4, selected.Count);
        Assert.Contains(selected, candidate => candidate.Features?.IsExplorationCandidate == true);
    }

    [Fact]
    public void LineageConcentrationIsLimitedWhenAnotherLineageIsAvailable()
    {
        var pool = new[]
        {
            Candidate("TRACE", "lineage-a-1", FixedNow.AddMinutes(-1), new FeedCandidateFeatureInput(LineageKey: "lineage-a")),
            Candidate("TRACE", "lineage-a-2", FixedNow.AddMinutes(-2), new FeedCandidateFeatureInput(LineageKey: "lineage-a")),
            Candidate("TRACE", "lineage-b-1", FixedNow.AddMinutes(-3), new FeedCandidateFeatureInput(LineageKey: "lineage-b")),
            Candidate("TRACE", "lineage-b-2", FixedNow.AddMinutes(-4), new FeedCandidateFeatureInput(LineageKey: "lineage-b")),
            Candidate("TRACE", "lineage-c-1", FixedNow.AddMinutes(-5), new FeedCandidateFeatureInput(LineageKey: "lineage-c"))
        };

        var selected = FeedDiversitySelector.Select(pool, FeedConfigDefaults.Config, 3);

        Assert.Equal(3, selected.Count);
        Assert.True(selected.Count(candidate => candidate.Features?.LineageKey == "lineage-a") <= 1);
    }

    [Fact]
    public void ContextualDiversityCapsCategoryAreaAndBusinessConcentration()
    {
        var config = FeedConfigDefaults.Config with
        {
            Diversity = FeedConfigDefaults.Config.Diversity with
            {
                MaxItemsPerCategory = 3,
                MaxItemsPerArea = 2,
                MaxItemsPerBusiness = 1
            }
        };
        var pool = new[]
        {
            Candidate("TRACE", "one", FixedNow.AddMinutes(-1), new FeedCandidateFeatureInput(
                Area: "Ari", CategoryKey: "cafe", BusinessKey: "business-a")),
            Candidate("TRACE", "same-business", FixedNow.AddMinutes(-2), new FeedCandidateFeatureInput(
                Area: "Ari", CategoryKey: "cafe", BusinessKey: "business-a")),
            Candidate("TRACE", "two", FixedNow.AddMinutes(-3), new FeedCandidateFeatureInput(
                Area: "Ari", CategoryKey: "cafe", BusinessKey: "business-b")),
            Candidate("TRACE", "three", FixedNow.AddMinutes(-4), new FeedCandidateFeatureInput(
                Area: "Thonglor", CategoryKey: "cafe", BusinessKey: "business-c")),
            Candidate("TRACE", "four", FixedNow.AddMinutes(-5), new FeedCandidateFeatureInput(
                Area: "Thonglor", CategoryKey: "art", BusinessKey: "business-d")),
            Candidate("TRACE", "five", FixedNow.AddMinutes(-6), new FeedCandidateFeatureInput(
                Area: "Ekkamai", CategoryKey: "art", BusinessKey: "business-e"))
        };

        var selected = FeedDiversitySelector.Select(pool, config, 4);

        Assert.Equal(4, selected.Count);
        Assert.True(selected.Count(candidate => candidate.Features?.CategoryKey == "cafe") <= 3);
        Assert.True(selected.Count(candidate => candidate.Features?.Area == "Ari") <= 2);
        Assert.True(selected.GroupBy(candidate => candidate.Features?.BusinessKey)
            .Where(group => group.Key is not null)
            .All(group => group.Count() <= 1));
        Assert.DoesNotContain(selected, candidate => candidate.EntityId == "same-business");
    }

    [Fact]
    public void RankingScoreIsBoundToTheSignedCursorAnchor()
    {
        var candidate = FeedRankingPipeline.Rank(
                new[] { Candidate("TRACE", "trace", FixedNow.AddMinutes(-1), new FeedCandidateFeatureInput(QualityScore: 0.9)) },
                CreateContext())
            .Single();

        Assert.True(FeedOrderingAnchor.TryDecode(FeedOrderingAnchor.Encode(candidate), out var anchor));
        Assert.Equal(candidate.RankingScore, anchor!.RankingScore);
    }

    [Fact]
    public void RankingReasonCodesStayWithinThePublicAllowlist()
    {
        var candidates = FeedRankingPipeline.Rank(
            new[]
            {
                Candidate("TRACE", "trace", FixedNow.AddMinutes(-1), new FeedCandidateFeatureInput(PopularitySignal: 100)),
                Candidate("PLACE", "place", FixedNow.AddMinutes(-2), new FeedCandidateFeatureInput(GeographySignal: 1))
            },
            CreateContext());

        Assert.All(candidates, candidate => Assert.Contains(
            candidate.RankingReasonCode,
            candidate.EntityType == "TRACE"
                ? TraceReasonCodes
                : PlaceReasonCodes));
    }

    private static FeedCandidateRecord Candidate(
        string entityType,
        string entityId,
        DateTimeOffset publishedAt,
        FeedCandidateFeatureInput features) =>
        new(entityType, entityId, entityType.ToLowerInvariant(), publishedAt, entityId, Features: features);

    private static FeedSessionContext CreateContext(
        FeedRuntimeConfig? config = null,
        bool authenticated = false) =>
        new(
            "feed-test-session",
            FeedApiContract.ApplicationCode,
            "feed-test-binding",
            authenticated,
            "explore",
            "for_you",
            null,
            null,
            null,
            "feed-test-fingerprint",
            FixedNow,
            FixedNow,
            "feed-test-epoch",
            "config-v1",
            "ranking-v1",
            FeedSessionContextFactory.PolicyVersion,
            "feed-test-seed",
            24,
            FixedNow.AddMinutes(5),
            config ?? FeedConfigDefaults.Config,
            authenticated ? Guid.NewGuid() : null,
            null);

    private static int MaxConsecutiveTypes(IReadOnlyList<FeedCandidateRecord> candidates)
    {
        var maximum = 0;
        var current = 0;
        var last = string.Empty;
        foreach (var candidate in candidates)
        {
            current = candidate.EntityType == last ? current + 1 : 1;
            last = candidate.EntityType;
            maximum = Math.Max(maximum, current);
        }

        return maximum;
    }
}
