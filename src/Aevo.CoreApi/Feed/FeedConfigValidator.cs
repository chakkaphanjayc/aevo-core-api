using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public static class FeedConfigDefaults
{
    public const string BaselineJson = "{\"schemaVersion\":\"1\",\"enabled\":true,\"killSwitch\":false,\"candidateSources\":{\"trace\":{\"enabled\":true,\"budget\":100,\"minimum\":1},\"place\":{\"enabled\":true,\"budget\":100,\"minimum\":1}},\"ranking\":{\"mode\":\"DETERMINISTIC\",\"version\":\"deterministic-v1\",\"freshnessWindowHours\":168,\"weights\":{\"quality\":0.4,\"freshness\":0.2,\"proximity\":0.2,\"taste\":0.2}},\"diversity\":{\"maxConsecutiveSameSource\":2,\"maxSourceRatio\":0.75,\"explorationQuota\":0.1,\"maxItemsPerCategory\":3,\"maxItemsPerArea\":4,\"maxItemsPerBusiness\":1},\"geo\":{\"maxCoarseRadiusMeters\":50000},\"rollout\":{\"percent\":100,\"experimentId\":null,\"salt\":\"feed-v1\",\"variants\":[]},\"budgets\":{\"pageSize\":24,\"cacheTtlSeconds\":30},\"safety\":{\"guardrailsEnabled\":true,\"requireModerationProjection\":true,\"maxEligibilityAgeSeconds\":60},\"analytics\":{\"enabled\":true,\"samplePercent\":100,\"retentionDays\":90},\"discovery\":{\"intentPrecedence\":\"EXPLICIT_FIRST\",\"intentMatchWeight\":0.35,\"taste\":{\"minimumEvidence\":2,\"minimumConfidence\":0.4,\"decayHalfLifeDays\":180,\"maxAgeDays\":365},\"modules\":{\"enabled\":true,\"minimumItems\":1,\"maximumItems\":12,\"order\":[\"FOR_YOU\"]},\"evidence\":{\"enabled\":false,\"minimumConfidence\":0.7,\"maxAgeDays\":30},\"safety\":{\"policyVersion\":\"ugc-safe-v1\",\"publicEvidenceEnabled\":false,\"publicMediaEnabled\":false}}}";

    public static JsonElement Json => JsonDocument.Parse(BaselineJson).RootElement.Clone();

    public static FeedRuntimeConfig Config => FeedConfigValidator.Deserialize(Json);
}

public sealed record FeedConfigFallbackResult(
    FeedRuntimeConfig Config,
    JsonElement RawConfig,
    string Source,
    bool Degraded,
    string? ErrorCode);

public static class FeedConfigFallback
{
    public static FeedConfigFallbackResult Resolve(
        FeedRuntimeConfig? active,
        JsonElement? activeJson,
        FeedRuntimeConfig? lastKnownGood,
        JsonElement? lastKnownGoodJson,
        string? errorCode)
    {
        if (active is not null && activeJson is not null)
        {
            return new FeedConfigFallbackResult(active, activeJson.Value, "ACTIVE_REVISION", false, null);
        }

        if (lastKnownGood is not null && lastKnownGoodJson is not null)
        {
            return new FeedConfigFallbackResult(lastKnownGood, lastKnownGoodJson.Value, "LAST_KNOWN_GOOD", true, errorCode ?? "FEED_CONFIG_ACTIVE_UNAVAILABLE");
        }

        return new FeedConfigFallbackResult(
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            "DETERMINISTIC_BASELINE",
            true,
            errorCode ?? "FEED_CONFIG_FALLBACK");
    }
}

public static class FeedConfigValidator
{
    private static readonly HashSet<string> SupportedDiscoveryModules = new(StringComparer.Ordinal)
    {
        "FOR_YOU",
        "NEAR_SELECTED_AREA",
        "NEW_AND_USEFUL",
        "COMMUNITY_FAVORITES"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    private static readonly HashSet<string> ForbiddenFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql", "table", "tableName", "rpc", "rpcName", "credential", "credentials", "secret",
        "database", "databaseUrl", "connectionString", "pubsub", "cloudTasks", "cloudRun", "storage",
        "bucket", "workerConcurrency", "queryTimeout", "connectionPool", "retryLimit", "rankingExpression",
        "jsonPath", "serviceAccount", "providerKey", "webhookSecret"
    };

    public static FeedConfigValidationResult Validate(JsonElement document)
    {
        var errors = new List<FeedConfigValidationIssue>();
        var warnings = new List<FeedConfigValidationIssue>();

        if (document.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Issue("CONFIG_OBJECT_REQUIRED", "$", "Feed config must be a JSON object."));
            return Result(errors, warnings, null);
        }

        if (document.GetRawText().Length > 32_768)
        {
            errors.Add(Issue("CONFIG_TOO_LARGE", "$", "Feed config must be 32 KiB or smaller."));
        }

        var forbiddenField = FindForbiddenField(document, "$", out var forbiddenPath);
        if (forbiddenField is not null)
        {
            errors.Add(Issue("INFRASTRUCTURE_FIELD_FORBIDDEN", forbiddenPath, $"Field '{forbiddenField}' is not a runtime Feed setting."));
        }

        FeedRuntimeConfig? config = null;
        try
        {
            config = Deserialize(document);
        }
        catch (JsonException exception)
        {
            var path = string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path;
            errors.Add(Issue("CONFIG_SCHEMA_INVALID", path, "Feed config contains missing, unknown, or incorrectly typed fields."));
        }

        if (config is not null)
        {
            ValidateConfig(config, errors, warnings);
        }

        return Result(errors, warnings, config);
    }

    public static FeedRuntimeConfig Deserialize(JsonElement document)
    {
        var config = JsonSerializer.Deserialize<FeedRuntimeConfig>(document.GetRawText(), SerializerOptions)
            ?? throw new JsonException("Feed config cannot be null.");
        var discovery = config.Discovery ?? FeedDiscoveryConfigDefaults.Config;
        return config with { Discovery = discovery };
    }

    public static JsonElement Serialize(FeedRuntimeConfig config)
    {
        return JsonSerializer.SerializeToElement(
            config with { Discovery = config.Discovery ?? FeedDiscoveryConfigDefaults.Config },
            SerializerOptions);
    }

    public static FeedRuntimeConfig EffectiveRuntimeConfig(FeedRuntimeConfig config)
    {
        if (!config.KillSwitch) return config;

        return config with
        {
            Ranking = config.Ranking with
            {
                Mode = "DETERMINISTIC",
                Version = FeedConfigContract.DeterministicRankingVersion
            },
            Rollout = config.Rollout with
            {
                Percent = 0,
                ExperimentId = null,
                Variants = Array.Empty<FeedRolloutVariantConfig>()
            }
        };
    }

    private static FeedConfigValidationResult Result(
        List<FeedConfigValidationIssue> errors,
        List<FeedConfigValidationIssue> warnings,
        FeedRuntimeConfig? config)
    {
        return new FeedConfigValidationResult(
            errors.Count == 0,
            FeedConfigContract.ValidatorVersion,
            errors,
            warnings,
            config);
    }

    private static void ValidateConfig(
        FeedRuntimeConfig config,
        List<FeedConfigValidationIssue> errors,
        List<FeedConfigValidationIssue> warnings)
    {
        if (config.SchemaVersion != FeedConfigContract.SchemaVersion)
        {
            errors.Add(Issue("SCHEMA_VERSION_UNSUPPORTED", "$.schemaVersion", "Only Feed config schema version 1 is supported."));
        }

        ValidateSource(config.CandidateSources.Trace, "$.candidateSources.trace", errors);
        ValidateSource(config.CandidateSources.Place, "$.candidateSources.place", errors);

        if (config.Ranking.Mode is not ("DETERMINISTIC" or "SHADOW" or "LIVE"))
        {
            errors.Add(Issue("ENUM_OUT_OF_RANGE", "$.ranking.mode", "Ranking mode must be DETERMINISTIC, SHADOW, or LIVE."));
        }
        ValidateIdentifier(config.Ranking.Version, "$.ranking.version", 64, errors);
        ValidateRange(config.Ranking.FreshnessWindowHours, 1, 720, "$.ranking.freshnessWindowHours", errors);
        ValidateWeight(config.Ranking.Weights.Quality, "$.ranking.weights.quality", errors);
        ValidateWeight(config.Ranking.Weights.Freshness, "$.ranking.weights.freshness", errors);
        ValidateWeight(config.Ranking.Weights.Proximity, "$.ranking.weights.proximity", errors);
        ValidateWeight(config.Ranking.Weights.Taste, "$.ranking.weights.taste", errors);
        var weightTotal = config.Ranking.Weights.Quality
            + config.Ranking.Weights.Freshness
            + config.Ranking.Weights.Proximity
            + config.Ranking.Weights.Taste;
        if (weightTotal is < 0.999m or > 1.001m)
        {
            errors.Add(Issue("WEIGHTS_MUST_SUM_TO_ONE", "$.ranking.weights", "Ranking weights must sum to 1.0."));
        }

        ValidateRange(config.Diversity.MaxConsecutiveSameSource, 1, 5, "$.diversity.maxConsecutiveSameSource", errors);
        ValidateRange(config.Diversity.MaxSourceRatio, 0.5m, 1m, "$.diversity.maxSourceRatio", errors);
        ValidateRange(config.Diversity.ExplorationQuota, 0m, 0.25m, "$.diversity.explorationQuota", errors);
        ValidateRange(config.Diversity.MaxItemsPerCategory, 1, 6, "$.diversity.maxItemsPerCategory", errors);
        ValidateRange(config.Diversity.MaxItemsPerArea, 1, 12, "$.diversity.maxItemsPerArea", errors);
        ValidateRange(config.Diversity.MaxItemsPerBusiness, 1, 3, "$.diversity.maxItemsPerBusiness", errors);

        ValidateRange(config.Geo.MaxCoarseRadiusMeters, 100, 50_000, "$.geo.maxCoarseRadiusMeters", errors);

        ValidateRange(config.Rollout.Percent, 0, 100, "$.rollout.percent", errors);
        ValidateIdentifier(config.Rollout.Salt, "$.rollout.salt", 64, errors);
        if (config.Rollout.Variants.Count > 8)
        {
            errors.Add(Issue("VARIANTS_TOO_MANY", "$.rollout.variants", "At most eight rollout variants are allowed."));
        }
        var variantKeys = new HashSet<string>(StringComparer.Ordinal);
        var variantTotal = 0;
        foreach (var variant in config.Rollout.Variants)
        {
            ValidateIdentifier(variant.Key, "$.rollout.variants[].key", 32, errors);
            ValidateRange(variant.Percent, 0, 100, "$.rollout.variants[].percent", errors);
            if (!variantKeys.Add(variant.Key)) errors.Add(Issue("VARIANT_KEY_DUPLICATE", "$.rollout.variants", "Variant keys must be unique."));
            variantTotal += variant.Percent;
        }
        if (config.Rollout.Variants.Count > 0 && variantTotal != 100)
        {
            errors.Add(Issue("VARIANT_PERCENT_TOTAL_INVALID", "$.rollout.variants", "Variant percentages must sum to 100."));
        }
        if (config.Rollout.ExperimentId is not null)
        {
            ValidateIdentifier(config.Rollout.ExperimentId, "$.rollout.experimentId", 64, errors);
            if (config.Rollout.Variants.Count == 0)
            {
                errors.Add(Issue("EXPERIMENT_VARIANTS_REQUIRED", "$.rollout.variants", "An experiment must define at least one variant."));
            }
        }
        else if (config.Rollout.Variants.Count > 0)
        {
            errors.Add(Issue("EXPERIMENT_ID_REQUIRED", "$.rollout.experimentId", "Variants require an experiment ID."));
        }

        ValidateRange(config.Budgets.PageSize, 1, 50, "$.budgets.pageSize", errors);
        ValidateRange(config.Budgets.CacheTtlSeconds, 0, 300, "$.budgets.cacheTtlSeconds", errors);
        var totalBudget = (config.CandidateSources.Trace.Enabled ? config.CandidateSources.Trace.Budget : 0)
            + (config.CandidateSources.Place.Enabled ? config.CandidateSources.Place.Budget : 0);
        if (config.Enabled && totalBudget < config.Budgets.PageSize)
        {
            errors.Add(Issue("PAGE_SIZE_EXCEEDS_CANDIDATE_BUDGET", "$.budgets.pageSize", "Enabled Feed page size cannot exceed enabled candidate budgets."));
        }

        ValidateRange(config.Safety.MaxEligibilityAgeSeconds, 5, 3_600, "$.safety.maxEligibilityAgeSeconds", errors);
        if (!config.Safety.RequireModerationProjection)
        {
            errors.Add(Issue("MODERATION_PROJECTION_REQUIRED", "$.safety.requireModerationProjection", "Feed runtime config must require the moderation projection."));
        }
        if (config.Ranking.Mode == "LIVE" && !config.Safety.GuardrailsEnabled)
        {
            errors.Add(Issue("LIVE_REQUIRES_GUARDRAILS", "$.safety.guardrailsEnabled", "LIVE ranking requires guardrails to be enabled."));
        }
        if (config.KillSwitch && config.Ranking.Mode == "LIVE")
        {
            warnings.Add(Issue("KILL_SWITCH_OVERRIDES_LIVE", "$.killSwitch", "Kill switch forces deterministic ranking and disables rollout at runtime."));
        }

        ValidateRange(config.Analytics.SamplePercent, 0, 100, "$.analytics.samplePercent", errors);
        ValidateRange(config.Analytics.RetentionDays, 7, 365, "$.analytics.retentionDays", errors);

        ValidateDiscovery(config.Discovery ?? FeedDiscoveryConfigDefaults.Config, errors);

        var enabledSources = (config.CandidateSources.Trace.Enabled ? 1 : 0) + (config.CandidateSources.Place.Enabled ? 1 : 0);
        if (enabledSources == 0 && config.Enabled && !config.KillSwitch)
        {
            errors.Add(Issue("NO_CANDIDATE_SOURCE_ENABLED", "$.candidateSources", "At least one candidate source must be enabled when Feed is enabled."));
        }
        if (enabledSources == 1 && config.Diversity.MaxSourceRatio < 1m)
        {
            errors.Add(Issue("DIVERSITY_CAP_MAKES_PAGE_IMPOSSIBLE", "$.diversity.maxSourceRatio", "A single enabled source must be allowed to fill the page."));
        }
    }

    private static void ValidateSource(
        FeedCandidateSourceConfig source,
        string path,
        List<FeedConfigValidationIssue> errors)
    {
        ValidateRange(source.Budget, 0, 200, $"{path}.budget", errors);
        ValidateRange(source.Minimum, 0, 50, $"{path}.minimum", errors);
        if (source.Minimum > source.Budget)
        {
            errors.Add(Issue("SOURCE_MINIMUM_EXCEEDS_BUDGET", path, "A source minimum cannot exceed its budget."));
        }
        if (!source.Enabled && (source.Budget != 0 || source.Minimum != 0))
        {
            errors.Add(Issue("DISABLED_SOURCE_HAS_QUOTA", path, "A disabled candidate source must have zero budget and minimum."));
        }
    }

    private static void ValidateDiscovery(
        FeedDiscoveryConfig discovery,
        List<FeedConfigValidationIssue> errors)
    {
        if (discovery.IntentPrecedence != "EXPLICIT_FIRST")
        {
            errors.Add(Issue("DISCOVERY_INTENT_PRECEDENCE_INVALID", "$.discovery.intentPrecedence", "Discovery intent precedence must remain EXPLICIT_FIRST."));
        }
        ValidateRange(discovery.IntentMatchWeight, 0.05m, 0.8m, "$.discovery.intentMatchWeight", errors);

        ValidateRange(discovery.Taste.MinimumEvidence, 2, 20, "$.discovery.taste.minimumEvidence", errors);
        ValidateRange(discovery.Taste.MinimumConfidence, 0.4m, 1m, "$.discovery.taste.minimumConfidence", errors);
        ValidateRange(discovery.Taste.DecayHalfLifeDays, 7, 730, "$.discovery.taste.decayHalfLifeDays", errors);
        ValidateRange(discovery.Taste.MaxAgeDays, 30, 1095, "$.discovery.taste.maxAgeDays", errors);
        if (discovery.Taste.MaxAgeDays < discovery.Taste.DecayHalfLifeDays)
        {
            errors.Add(Issue("DISCOVERY_TASTE_AGE_BELOW_HALF_LIFE", "$.discovery.taste.maxAgeDays", "Taste max age must be at least the decay half-life."));
        }

        ValidateRange(discovery.Modules.MinimumItems, 0, 24, "$.discovery.modules.minimumItems", errors);
        ValidateRange(discovery.Modules.MaximumItems, 1, 24, "$.discovery.modules.maximumItems", errors);
        if (discovery.Modules.MinimumItems > discovery.Modules.MaximumItems)
        {
            errors.Add(Issue("DISCOVERY_MODULE_MINIMUM_EXCEEDS_MAXIMUM", "$.discovery.modules", "Module minimum cannot exceed maximum."));
        }
        if (discovery.Modules.Order.Count > 4)
        {
            errors.Add(Issue("DISCOVERY_MODULE_ORDER_TOO_LARGE", "$.discovery.modules.order", "At most four discovery modules may be ordered."));
        }
        var moduleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in discovery.Modules.Order)
        {
            if (!SupportedDiscoveryModules.Contains(module))
            {
                errors.Add(Issue("DISCOVERY_MODULE_UNSUPPORTED", "$.discovery.modules.order", "The requested discovery module is not backed by the current TRACE/PLACE inventory."));
            }
            if (!moduleKeys.Add(module))
            {
                errors.Add(Issue("DISCOVERY_MODULE_DUPLICATE", "$.discovery.modules.order", "Discovery module order must not contain duplicates."));
            }
        }
        if (discovery.Modules.Enabled && discovery.Modules.Order.Count == 0)
        {
            errors.Add(Issue("DISCOVERY_MODULE_ORDER_REQUIRED", "$.discovery.modules.order", "Enabled discovery modules require an order."));
        }

        ValidateRange(discovery.Evidence.MinimumConfidence, 0, 1, "$.discovery.evidence.minimumConfidence", errors);
        ValidateRange(discovery.Evidence.MaxAgeDays, 1, 730, "$.discovery.evidence.maxAgeDays", errors);
        ValidateIdentifier(discovery.Safety.PolicyVersion, "$.discovery.safety.policyVersion", 64, errors);
        if (discovery.Safety.PublicEvidenceEnabled)
        {
            errors.Add(Issue("PUBLIC_EVIDENCE_PIPELINE_REQUIRED", "$.discovery.safety.publicEvidenceEnabled", "Public evidence remains disabled until its connected moderation/RLS pipeline is enabled."));
        }
        if (discovery.Safety.PublicMediaEnabled)
        {
            errors.Add(Issue("PUBLIC_MEDIA_PIPELINE_REQUIRED", "$.discovery.safety.publicMediaEnabled", "Public media remains disabled until approved variants and deletion propagation are connected."));
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        int maxLength,
        List<FeedConfigValidationIssue> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            errors.Add(Issue("IDENTIFIER_INVALID", path, $"Value must be a non-empty identifier of at most {maxLength} safe characters."));
        }
    }

    private static void ValidateWeight(decimal value, string path, List<FeedConfigValidationIssue> errors) => ValidateRange(value, 0m, 1m, path, errors);

    private static void ValidateRange<T>(T value, T minimum, T maximum, string path, List<FeedConfigValidationIssue> errors)
        where T : IComparable<T>
    {
        if (value.CompareTo(minimum) < 0 || value.CompareTo(maximum) > 0)
        {
            errors.Add(Issue("VALUE_OUT_OF_RANGE", path, $"Value must be between {minimum} and {maximum}."));
        }
    }

    private static FeedConfigValidationIssue Issue(string code, string path, string message) => new(code, path, message);

    private static string? FindForbiddenField(JsonElement value, string path, out string foundPath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var currentPath = $"{path}.{property.Name}";
                if (ForbiddenFieldNames.Contains(property.Name))
                {
                    foundPath = currentPath;
                    return property.Name;
                }
                var nested = FindForbiddenField(property.Value, currentPath, out foundPath);
                if (nested is not null) return nested;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindForbiddenField(item, $"{path}[{index}]", out foundPath);
                if (nested is not null) return nested;
                index++;
            }
        }

        foundPath = path;
        return null;
    }
}

public static class FeedExperimentBucketing
{
    public static int Bucket(string stableKey, string experimentId, string salt)
    {
        if (string.IsNullOrWhiteSpace(stableKey)) throw new ArgumentException("A stable server-resolved key is required.", nameof(stableKey));
        if (string.IsNullOrWhiteSpace(experimentId)) throw new ArgumentException("An experiment ID is required.", nameof(experimentId));
        if (string.IsNullOrWhiteSpace(salt)) throw new ArgumentException("An experiment salt is required.", nameof(salt));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{experimentId}:{stableKey}:{salt}"));
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(0, sizeof(uint)));
        return (int)(value % 10_000);
    }

    public static string? SelectVariant(FeedRuntimeConfig config, string stableKey)
    {
        var runtimeConfig = FeedConfigValidator.EffectiveRuntimeConfig(config);
        if (runtimeConfig.Rollout.ExperimentId is null || runtimeConfig.Rollout.Variants.Count == 0) return null;
        if (Bucket(stableKey, runtimeConfig.Rollout.ExperimentId, runtimeConfig.Rollout.Salt) >= runtimeConfig.Rollout.Percent * 100) return null;

        var cursor = 0;
        var bucket = Bucket(stableKey, runtimeConfig.Rollout.ExperimentId, runtimeConfig.Rollout.Salt) % 100;
        foreach (var variant in runtimeConfig.Rollout.Variants)
        {
            cursor += variant.Percent;
            if (bucket < cursor) return variant.Key;
        }
        return null;
    }
}

public static class FeedConfigCompatibility
{
    public static bool IsLegacyFeedFlagKey(string? flagKey) =>
        flagKey?.Trim().ToLowerInvariant() is "mixed_feed" or "taste_ranking_v1";

    public static FeedConfigLegacyCompatibility Evaluate(
        FeedRuntimeConfig config,
        IReadOnlyList<FeedConfigLegacyFlagObservation> legacyFlags)
    {
        var effective = FeedConfigValidator.EffectiveRuntimeConfig(config);
        var expectedMixedFeed = effective.Enabled && !effective.KillSwitch;
        var expectedTasteRanking = effective.Ranking.Mode == "LIVE";
        var expectedRollout = effective.Rollout.Percent;
        var mismatched = new List<string>();

        var mixedFeed = legacyFlags.FirstOrDefault(flag => flag.FlagKey == "mixed_feed");
        var mixedMatches = mixedFeed is not null
            && mixedFeed.Enabled == expectedMixedFeed
            && mixedFeed.RolloutPercent == expectedRollout;
        if (!mixedMatches) mismatched.Add("mixed_feed");

        var tasteRanking = legacyFlags.FirstOrDefault(flag => flag.FlagKey == "taste_ranking_v1");
        var tasteMatches = tasteRanking is not null
            && tasteRanking.Enabled == expectedTasteRanking
            && (!expectedTasteRanking || tasteRanking.RolloutPercent == expectedRollout);
        if (!tasteMatches) mismatched.Add("taste_ranking_v1");

        return new FeedConfigLegacyCompatibility(true, mixedMatches, tasteMatches, mismatched, DateTimeOffset.UtcNow);
    }
}
