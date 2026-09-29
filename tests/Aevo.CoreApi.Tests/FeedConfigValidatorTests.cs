using System.Text.Json;
using System.Text.Json.Nodes;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedConfigValidatorTests
{
    private static readonly string[] RolloutVariants = ["control", "treatment"];

    [Fact]
    public void BaselineIsValidAndUsesTheExpectedSchema()
    {
        using var document = JsonDocument.Parse(FeedConfigDefaults.BaselineJson);

        var result = FeedConfigValidator.Validate(document.RootElement);

        Assert.True(result.Valid);
        Assert.Equal(FeedConfigContract.SchemaVersion, result.Config!.SchemaVersion);
        Assert.Equal("DETERMINISTIC", result.Config.Ranking.Mode);
        Assert.Equal("EXPLICIT_FIRST", result.Config.Discovery!.IntentPrecedence);
        Assert.Equal(0.35m, result.Config.Discovery.IntentMatchWeight);
        Assert.Equal(3, result.Config.Diversity.MaxItemsPerCategory);
        Assert.Equal(4, result.Config.Diversity.MaxItemsPerArea);
        Assert.Equal(1, result.Config.Diversity.MaxItemsPerBusiness);
        Assert.False(result.Config.Discovery.Safety.PublicMediaEnabled);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PreDiscoveryRevisionReceivesSafeDiscoveryDefaults()
    {
        var legacyDocument = JsonNode.Parse(FeedConfigDefaults.BaselineJson)!.AsObject();
        legacyDocument.Remove("discovery");
        using var document = JsonDocument.Parse(legacyDocument.ToJsonString());

        var result = FeedConfigValidator.Validate(document.RootElement);

        Assert.True(result.Valid);
        Assert.Equal("EXPLICIT_FIRST", result.Config!.Discovery!.IntentPrecedence);
        Assert.Equal(0.35m, result.Config.Discovery.IntentMatchWeight);
        Assert.Equal(2, result.Config.Discovery.Taste.MinimumEvidence);
        Assert.False(result.Config.Discovery.Safety.PublicMediaEnabled);
    }

    [Fact]
    public void DiscoveryRevisionWithoutIntentWeightReceivesTheBoundedDefault()
    {
        var legacyDocument = JsonNode.Parse(FeedConfigDefaults.BaselineJson)!.AsObject();
        legacyDocument["discovery"]!.AsObject().Remove("intentMatchWeight");
        using var document = JsonDocument.Parse(legacyDocument.ToJsonString());

        var result = FeedConfigValidator.Validate(document.RootElement);

        Assert.True(result.Valid);
        Assert.Equal(0.35m, result.Config!.Discovery!.IntentMatchWeight);
    }

    [Fact]
    public void LegacyDiversityRevisionReceivesContextualDiversityDefaults()
    {
        var legacyDocument = JsonNode.Parse(FeedConfigDefaults.BaselineJson)!.AsObject();
        var diversity = legacyDocument["diversity"]!.AsObject();
        diversity.Remove("maxItemsPerCategory");
        diversity.Remove("maxItemsPerArea");
        diversity.Remove("maxItemsPerBusiness");
        using var document = JsonDocument.Parse(legacyDocument.ToJsonString());

        var result = FeedConfigValidator.Validate(document.RootElement);

        Assert.True(result.Valid);
        Assert.Equal(3, result.Config!.Diversity.MaxItemsPerCategory);
        Assert.Equal(4, result.Config.Diversity.MaxItemsPerArea);
        Assert.Equal(1, result.Config.Diversity.MaxItemsPerBusiness);
    }

    [Fact]
    public void ContextualDiversityCapsAreBounded()
    {
        var config = FeedConfigDefaults.Config with
        {
            Diversity = FeedConfigDefaults.Config.Diversity with
            {
                MaxItemsPerCategory = 7,
                MaxItemsPerArea = 13,
                MaxItemsPerBusiness = 4
            }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Path == "$.diversity.maxItemsPerCategory");
        Assert.Contains(result.Errors, issue => issue.Path == "$.diversity.maxItemsPerArea");
        Assert.Contains(result.Errors, issue => issue.Path == "$.diversity.maxItemsPerBusiness");
    }

    [Fact]
    public void UnknownInfrastructureFieldsAreRejected()
    {
        using var document = JsonDocument.Parse("""
        {
          "schemaVersion":"1",
          "enabled":true,
          "killSwitch":false,
          "candidateSources":{"trace":{"enabled":true,"budget":100,"minimum":1},"place":{"enabled":true,"budget":100,"minimum":1}},
          "ranking":{"mode":"DETERMINISTIC","version":"deterministic-v1","freshnessWindowHours":168,"weights":{"quality":0.4,"freshness":0.2,"proximity":0.2,"taste":0.2},"rankingExpression":"select * from private_table"},
          "diversity":{"maxConsecutiveSameSource":2,"maxSourceRatio":0.75,"explorationQuota":0.1},
          "geo":{"maxCoarseRadiusMeters":50000},
          "rollout":{"percent":100,"experimentId":null,"salt":"feed-v1","variants":[]},
          "budgets":{"pageSize":24,"cacheTtlSeconds":30},
          "safety":{"guardrailsEnabled":true,"requireModerationProjection":true,"maxEligibilityAgeSeconds":60},
          "analytics":{"enabled":true,"samplePercent":100,"retentionDays":90}
        }
        """);

        var result = FeedConfigValidator.Validate(document.RootElement);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Code == "INFRASTRUCTURE_FIELD_FORBIDDEN");
    }

    [Fact]
    public void BoundsAndCrossFieldRulesRejectUnsafeCombinations()
    {
        var config = FeedConfigDefaults.Config with
        {
            CandidateSources = FeedConfigDefaults.Config.CandidateSources with
            {
                Trace = new FeedCandidateSourceConfig(false, 1, 0),
                Place = new FeedCandidateSourceConfig(true, 2, 3)
            },
            Budgets = new FeedBudgetConfig(24, 301),
            Ranking = FeedConfigDefaults.Config.Ranking with { Mode = "LIVE" },
            Safety = FeedConfigDefaults.Config.Safety with { GuardrailsEnabled = false }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Code == "DISABLED_SOURCE_HAS_QUOTA");
        Assert.Contains(result.Errors, issue => issue.Code == "SOURCE_MINIMUM_EXCEEDS_BUDGET");
        Assert.Contains(result.Errors, issue => issue.Code == "VALUE_OUT_OF_RANGE");
        Assert.Contains(result.Errors, issue => issue.Code == "LIVE_REQUIRES_GUARDRAILS");
    }

    [Fact]
    public void KillSwitchForcesDeterministicRuntimeAndDisablesRollout()
    {
        var config = FeedConfigDefaults.Config with
        {
            KillSwitch = true,
            Ranking = FeedConfigDefaults.Config.Ranking with { Mode = "LIVE", Version = "canary-v2" },
            Rollout = FeedConfigDefaults.Config.Rollout with
            {
                Percent = 100,
                ExperimentId = "feed-canary",
                Variants = new[] { new FeedRolloutVariantConfig("control", 100) }
            }
        };

        var effective = FeedConfigValidator.EffectiveRuntimeConfig(config);

        Assert.Equal("DETERMINISTIC", effective.Ranking.Mode);
        Assert.Equal(FeedConfigContract.DeterministicRankingVersion, effective.Ranking.Version);
        Assert.Equal(0, effective.Rollout.Percent);
        Assert.Null(effective.Rollout.ExperimentId);
        Assert.Empty(effective.Rollout.Variants);
    }

    [Fact]
    public void DiscoveryControlsRejectUnconnectedPublicEvidenceOrMedia()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = FeedConfigDefaults.Config.Discovery! with
            {
                Safety = FeedConfigDefaults.Config.Discovery!.Safety with
                {
                    PublicEvidenceEnabled = true,
                    PublicMediaEnabled = true
                }
            }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Code == "PUBLIC_EVIDENCE_PIPELINE_REQUIRED");
        Assert.Contains(result.Errors, issue => issue.Code == "PUBLIC_MEDIA_PIPELINE_REQUIRED");
    }

    [Fact]
    public void DiscoveryIntentWeightIsBounded()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = FeedConfigDefaults.Config.Discovery! with { IntentMatchWeight = 0.81m }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Path == "$.discovery.intentMatchWeight");
    }

    [Fact]
    public void DiscoveryModuleOrderIsTypedAndBounded()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = FeedConfigDefaults.Config.Discovery! with
            {
                Modules = FeedConfigDefaults.Config.Discovery!.Modules with
                {
                    Order = new[] { "FOR_YOU", "FOR_YOU", "BOOKABLE_NOW" },
                    MinimumItems = 13,
                    MaximumItems = 12
                }
            }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, issue => issue.Code == "DISCOVERY_MODULE_MINIMUM_EXCEEDS_MAXIMUM");
        Assert.Contains(result.Errors, issue => issue.Code == "DISCOVERY_MODULE_UNSUPPORTED");
        Assert.Contains(result.Errors, issue => issue.Code == "DISCOVERY_MODULE_DUPLICATE");
    }

    [Fact]
    public void DiscoveryModulePortfolioAcceptsOnlyInventoryBackedModules()
    {
        var config = FeedConfigDefaults.Config with
        {
            Discovery = FeedConfigDefaults.Config.Discovery! with
            {
                Modules = FeedConfigDefaults.Config.Discovery!.Modules with
                {
                    Order = new[] { "FOR_YOU", "NEAR_SELECTED_AREA", "NEW_AND_USEFUL", "COMMUNITY_FAVORITES" }
                }
            }
        };

        var result = FeedConfigValidator.Validate(FeedConfigValidator.Serialize(config));

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FallbackPrefersLastKnownGoodThenCodeBaseline()
    {
        var lastKnownGood = FeedConfigDefaults.Config with { Ranking = FeedConfigDefaults.Config.Ranking with { Version = "known-good-v1" } };
        using var lastKnownGoodDocument = JsonDocument.Parse(FeedConfigDefaults.BaselineJson);

        var lkg = FeedConfigFallback.Resolve(null, null, lastKnownGood, lastKnownGoodDocument.RootElement, "ACTIVE_UNAVAILABLE");
        var baseline = FeedConfigFallback.Resolve(null, null, null, null, "DATABASE_UNAVAILABLE");

        Assert.Equal("LAST_KNOWN_GOOD", lkg.Source);
        Assert.Equal("known-good-v1", lkg.Config.Ranking.Version);
        Assert.Equal("DETERMINISTIC_BASELINE", baseline.Source);
        Assert.Equal(FeedConfigContract.DeterministicRankingVersion, baseline.Config.Ranking.Version);
        Assert.True(baseline.Degraded);
    }

    [Fact]
    public void BucketingIsStableAndDoesNotRequirePersistedAssignments()
    {
        var config = FeedConfigDefaults.Config with
        {
            Rollout = FeedConfigDefaults.Config.Rollout with
            {
                Percent = 100,
                ExperimentId = "feed-canary",
                Variants = new[]
                {
                    new FeedRolloutVariantConfig("control", 50),
                    new FeedRolloutVariantConfig("treatment", 50)
                }
            }
        };

        var firstBucket = FeedExperimentBucketing.Bucket("user-123", "feed-canary", "feed-v1");
        var secondBucket = FeedExperimentBucketing.Bucket("user-123", "feed-canary", "feed-v1");
        var variant = FeedExperimentBucketing.SelectVariant(config, "user-123");

        Assert.Equal(firstBucket, secondBucket);
        Assert.InRange(firstBucket, 0, 9_999);
        Assert.Contains(variant, RolloutVariants);
    }

    [Fact]
    public void LegacyFlagsAreReadOnlyCompatibilityTelemetry()
    {
        Assert.True(FeedConfigCompatibility.IsLegacyFeedFlagKey(" MIXED_FEED "));
        Assert.False(FeedConfigCompatibility.IsLegacyFeedFlagKey("comments_v2"));

        var compatibility = FeedConfigCompatibility.Evaluate(
            FeedConfigDefaults.Config,
            new[]
            {
                new FeedConfigLegacyFlagObservation("mixed_feed", true, 100),
                new FeedConfigLegacyFlagObservation("taste_ranking_v1", false, 0)
            });

        Assert.True(compatibility.ReadOnly);
        Assert.True(compatibility.MixedFeedMatches);
        Assert.True(compatibility.TasteRankingMatches);
        Assert.Empty(compatibility.MismatchedFlags);
    }

    [Fact]
    public async Task RuntimeServiceFallsBackWhenCoreDatabaseIsUnavailable()
    {
        await using var database = new CoreDataStore(new ConfigurationBuilder().Build(), NullLogger<CoreDataStore>.Instance);
        var service = new FeedConfigService(database);

        var snapshot = await service.GetRuntimeSnapshotAsync(CancellationToken.None);

        Assert.Equal("DETERMINISTIC_BASELINE", snapshot.Source);
        Assert.True(snapshot.Degraded);
        Assert.Equal("FEED_CONFIG_DATABASE_UNAVAILABLE", snapshot.ErrorCode);
        Assert.Equal(FeedConfigContract.DeterministicRankingVersion, snapshot.Config.Ranking.Version);
    }
}
