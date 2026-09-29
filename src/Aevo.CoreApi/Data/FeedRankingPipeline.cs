using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;

namespace Aevo.CoreApi.Data;

/// <summary>
/// FEED-005's server-side ranking boundary. The pipeline deliberately keeps
/// feature vectors, shadow scores, and calibration details in Core. Only the
/// bounded reason code is allowed to cross the public contract boundary.
/// </summary>
public static class FeedRankingPipeline
{
    private const double DefaultQuality = 0.5;
    private const double PopularityScale = 50;
    private const double PenaltyScale = 5;
    private const double PopularityUplift = 0.08;
    private const double PersonalizedRelationshipUplift = 0.12;
    private const double PenaltyWeight = 0.12;

    public static IReadOnlyList<FeedCandidateRecord> Rank(
        IEnumerable<FeedCandidateRecord> candidates,
        FeedSessionContext session)
    {
        var config = session.RuntimeConfig;
        var weights = NormalizeWeights(config.Ranking.Weights);
        var mode = config.KillSwitch ? "DETERMINISTIC" : config.Ranking.Mode;

        return candidates
            .Select(candidate =>
            {
                var calibrated = Calibrate(candidate, session);
                var deterministicScore = ScoreDeterministic(calibrated, weights, session);
                var shadowScore = session.IsAuthenticated && (mode is "SHADOW" or "LIVE")
                    ? (double?)ScorePersonalized(calibrated, weights, session)
                    : null;

                return candidate with
                {
                    RankingScore = deterministicScore,
                    ShadowScore = shadowScore,
                    RankingReasonCode = PublicReasonCode(candidate, calibrated, session)
                };
            })
            .ToArray();
    }

    private static CalibratedFeatures Calibrate(
        FeedCandidateRecord candidate,
        FeedSessionContext session)
    {
        var raw = candidate.Features ?? new FeedCandidateFeatureInput();
        var quality = Clamp01(raw.QualityScore);
        var confidence = Clamp01(raw.QualityConfidence);
        var qualityInfluence = candidate.EntityType == "PLACE"
            ? 0.5 + 0.5 * confidence
            : 0.35 + 0.65 * confidence;
        var popularityScale = candidate.EntityType == "PLACE" ? 100 : PopularityScale;

        // A missing or stale projection remains close to neutral and cannot
        // dominate the common feature space merely because a value is absent.
        quality = DefaultQuality + (quality - DefaultQuality) * qualityInfluence;

        var effectiveAffinity = FeedFeatureProjection.ApplyTasteDecay(
            raw.AffinitySignal,
            raw.AffinityEvidenceCount,
            raw.AffinityCalculatedAt,
            session.CandidateCutoffAt,
            session.RuntimeConfig.Discovery?.Taste);
        var hasExplicitIntent = FeedIntentMatcher.HasRankableSignal(session.DiscoveryIntent, session.Area);

        return new CalibratedFeatures(
            Clamp01(quality),
            Freshness(candidate.PublishedAt, session.CandidateCutoffAt, session.RuntimeConfig.Ranking.FreshnessWindowHours),
            Clamp01(raw.GeographySignal),
            NormalizeSigned(effectiveAffinity),
            Clamp01(raw.RelationshipSignal),
            NormalizeLog(raw.PopularitySignal, popularityScale),
            NormalizeLog(raw.PenaltySignal, PenaltyScale),
            raw.IsExplorationCandidate,
            FeedIntentMatcher.Match(raw, session.DiscoveryIntent, session.Area),
            hasExplicitIntent);
    }

    private static double ScoreDeterministic(
        CalibratedFeatures features,
        NormalizedWeights weights,
        FeedSessionContext session)
    {
        var score =
            features.Quality * weights.Quality
            + features.Freshness * weights.Freshness
            + features.Geography * weights.Proximity
            + DefaultQuality * weights.Taste
            + features.Popularity * PopularityUplift
            - features.Penalty * PenaltyWeight;
        return ApplyExplicitIntentPriority(score, features, session);
    }

    private static double ScorePersonalized(
        CalibratedFeatures features,
        NormalizedWeights weights,
        FeedSessionContext session)
    {
        var score =
            features.Quality * weights.Quality
            + features.Freshness * weights.Freshness
            + features.Geography * weights.Proximity
            + features.Affinity * weights.Taste
            + features.Popularity * PopularityUplift
            + features.Relationship * PersonalizedRelationshipUplift
            - features.Penalty * PenaltyWeight;
        return ApplyExplicitIntentPriority(score, features, session);
    }

    private static double ApplyExplicitIntentPriority(
        double score,
        CalibratedFeatures features,
        FeedSessionContext session)
    {
        var discovery = session.RuntimeConfig.Discovery ?? FeedDiscoveryConfigDefaults.Config;
        if (!features.HasExplicitIntent || discovery.IntentPrecedence != "EXPLICIT_FIRST")
        {
            return Clamp01(score);
        }

        return Clamp01(
            Clamp01(score) * (1 - ExplicitIntentPriority(session))
            + features.IntentMatch * ExplicitIntentPriority(session));
    }

    private static double ExplicitIntentPriority(FeedSessionContext session)
    {
        var discovery = session.RuntimeConfig.Discovery ?? FeedDiscoveryConfigDefaults.Config;
        var configured = discovery.IntentMatchWeight;
        return Clamp((double)configured, 0.05, 0.8);
    }

    private static string PublicReasonCode(
        FeedCandidateRecord candidate,
        CalibratedFeatures features,
        FeedSessionContext session)
    {
        if (candidate.EntityType == "TRACE")
        {
            if (session.Tab == "following" && features.Relationship >= 0.5)
            {
                return "FOLLOWING_TRACER";
            }

            return features.Popularity >= 0.45 ? "POPULAR" : "NEW_TRACE";
        }

        return session.Tab == "nearby" && features.Geography >= 0.9
            ? "NEARBY_PLACE"
            : "POPULAR_PLACE";
    }

    private static NormalizedWeights NormalizeWeights(FeedRankingWeightsConfig weights)
    {
        var quality = Math.Max(0, (double)weights.Quality);
        var freshness = Math.Max(0, (double)weights.Freshness);
        var proximity = Math.Max(0, (double)weights.Proximity);
        var taste = Math.Max(0, (double)weights.Taste);
        var total = quality + freshness + proximity + taste;
        if (total <= double.Epsilon)
        {
            return new NormalizedWeights(0.4, 0.2, 0.2, 0.2);
        }

        return new NormalizedWeights(
            quality / total,
            freshness / total,
            proximity / total,
            taste / total);
    }

    private static double Freshness(
        DateTimeOffset publishedAt,
        DateTimeOffset cutoff,
        int freshnessWindowHours)
    {
        var ageHours = Math.Max(0, (cutoff - publishedAt).TotalHours);
        var window = Math.Max(1, freshnessWindowHours);
        return Clamp01(Math.Exp(-ageHours / window));
    }

    private static double NormalizeLog(double value, double scale)
    {
        if (!double.IsFinite(value) || value <= 0) return 0;
        return Clamp01(Math.Log(1 + value) / Math.Log(1 + scale));
    }

    private static double NormalizeSigned(double value)
    {
        if (!double.IsFinite(value)) return DefaultQuality;
        return Clamp01(DefaultQuality + 0.5 * Clamp(value, -1, 1));
    }

    private static double Clamp01(double value) => Clamp(value, 0, 1);

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value)) return minimum;
        return Math.Clamp(value, minimum, maximum);
    }

    private sealed record CalibratedFeatures(
        double Quality,
        double Freshness,
        double Geography,
        double Affinity,
        double Relationship,
        double Popularity,
        double Penalty,
        bool IsExploration,
        double IntentMatch,
        bool HasExplicitIntent);

    private sealed record NormalizedWeights(
        double Quality,
        double Freshness,
        double Proximity,
        double Taste);
}

/// <summary>
/// Explainable, bounded matching between the current request intent and the
/// private candidate facets. Unknown facets stay neutral; they never create a
/// synthetic match or expose raw query text in the public response.
/// </summary>
public static class FeedIntentMatcher
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> VibeKeywords =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["slow-bar"] = new[] { "slow-bar", "slow", "coffee", "cafe", "กาแฟ" },
            ["quiet"] = new[] { "quiet", "wellness", "เงียบ", "พักใจ", "slow" },
            ["art"] = new[] { "art", "gallery", "design", "ศิลป", "งานออกแบบ", "ถ่ายรูป" },
            ["work"] = new[] { "work", "studio", "design", "กาแฟ" },
            ["speakeasy"] = new[] { "speakeasy", "bar", "บาร์", "dining", "มื้อเย็น" }
        };

    public static bool HasRankableSignal(
        FeedDiscoveryIntentContract? intent,
        string? selectedArea)
    {
        return !string.IsNullOrWhiteSpace(selectedArea)
            || !string.IsNullOrWhiteSpace(intent?.Vibe) && intent.Vibe != "match"
            || !string.IsNullOrWhiteSpace(intent?.Category);
    }

    public static double Match(
        FeedCandidateFeatureInput? features,
        FeedDiscoveryIntentContract? intent,
        string? selectedArea)
    {
        if (features is null || !HasRankableSignal(intent, selectedArea)) return 0.5;

        var matches = new List<double>(3);
        var tags = features.EffectiveTopicTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .ToArray();

        if (!string.IsNullOrWhiteSpace(selectedArea))
        {
            matches.Add(string.IsNullOrWhiteSpace(features.Area)
                ? 0.5
                : string.Equals(features.Area.Trim(), selectedArea.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        }

        if (!string.IsNullOrWhiteSpace(intent?.Category))
        {
            matches.Add(tags.Length == 0
                ? 0.5
                : tags.Any(tag => string.Equals(tag, intent.Category.Trim(), StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
        }

        if (!string.IsNullOrWhiteSpace(intent?.Vibe) && intent.Vibe != "match")
        {
            var keywords = VibeKeywords.GetValueOrDefault(intent.Vibe.Trim().ToLowerInvariant());
            matches.Add(keywords is null || tags.Length == 0
                ? 0.5
                : tags.Any(tag => keywords.Any(keyword => tag.Contains(keyword, StringComparison.OrdinalIgnoreCase))) ? 1 : 0);
        }

        // Explicit dimensions are conjunctive: a candidate that misses one
        // known dimension must not outrank a candidate that satisfies all of
        // them merely because it has a strong historical affinity elsewhere.
        return matches.Count == 0 ? 0.5 : matches.Min();
    }
}

/// <summary>
/// Pure, private projection rules for long-term taste. The database stores the
/// projection; this seam decides whether it is trustworthy enough to affect a
/// ranked candidate. It intentionally cannot create taste from an impression
/// without the projection's evidence count.
/// </summary>
public static class FeedFeatureProjection
{
    public const int MinimumTasteEvidence = 2;
    public const int FullTasteConfidenceEvidence = 5;
    public const int TasteHalfLifeDays = 180;
    public const int MaximumTasteAgeDays = 365;

    public static double ApplyTasteDecay(
        double affinity,
        int evidenceCount,
        DateTimeOffset? calculatedAt,
        DateTimeOffset asOf,
        FeedDiscoveryTasteConfig? policy = null)
    {
        var effectivePolicy = policy ?? new FeedDiscoveryTasteConfig(
            MinimumTasteEvidence,
            MinimumTasteConfidence,
            TasteHalfLifeDays,
            MaximumTasteAgeDays);
        if (!double.IsFinite(affinity)
            || evidenceCount < effectivePolicy.MinimumEvidence
            || calculatedAt is null
            || calculatedAt.Value > asOf)
        {
            return 0;
        }

        var ageDays = Math.Max(0, (asOf - calculatedAt.Value).TotalDays);
        if (ageDays > effectivePolicy.MaxAgeDays) return 0;

        var evidenceConfidence = Math.Min(1, evidenceCount / (double)FullTasteConfidenceEvidence);
        if (evidenceConfidence < (double)effectivePolicy.MinimumConfidence) return 0;

        var decay = Math.Exp(-Math.Log(2) * ageDays / effectivePolicy.DecayHalfLifeDays);
        return Math.Clamp(affinity * evidenceConfidence * decay, -1, 1);
    }

    private const decimal MinimumTasteConfidence = 0.4m;
}

/// <summary>
/// Deterministic selection after ranking. It protects a page from source and
/// creator concentration while relaxing only when the candidate pool cannot
/// satisfy a configured cap without returning an artificially short page.
/// </summary>
public static class FeedDiversitySelector
{
    private const int MaxItemsPerCreator = 2;
    private const int MaxItemsPerLineage = 1;

    public static IReadOnlyList<FeedCandidateRecord> Select(
        IEnumerable<FeedCandidateRecord> orderedCandidates,
        FeedRuntimeConfig config,
        int limit)
    {
        var pool = orderedCandidates.ToArray();
        var target = Math.Min(Math.Max(0, limit), pool.Length);
        if (target == 0) return Array.Empty<FeedCandidateRecord>();

        var selected = new List<FeedCandidateRecord>(target);
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var creatorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lineageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var areaCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var businessCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var maximumPerType = Math.Max(1, (int)Math.Ceiling(target * (double)config.Diversity.MaxSourceRatio));
        var explorationTarget = Math.Min(
            target,
            (int)Math.Ceiling(target * (double)config.Diversity.ExplorationQuota));
        var maximumPerCategory = Math.Max(1, config.Diversity.MaxItemsPerCategory);
        var maximumPerArea = Math.Max(1, config.Diversity.MaxItemsPerArea);
        var maximumPerBusiness = Math.Max(1, config.Diversity.MaxItemsPerBusiness);

        var lastType = string.Empty;
        var consecutiveTypeCount = 0;
        var maxConsecutive = Math.Max(1, config.Diversity.MaxConsecutiveSameSource);

        bool HasUnselectedOtherType(string entityType)
        {
            return pool.Any(candidate =>
                candidate.EntityType != entityType
                && !selectedKeys.Contains(Key(candidate)));
        }

        bool HasUnselectedOtherCreator(string? creatorKey)
        {
            if (string.IsNullOrWhiteSpace(creatorKey)) return false;
            return pool.Any(candidate =>
                !selectedKeys.Contains(Key(candidate))
                && !string.Equals(candidate.Features?.CreatorKey, creatorKey, StringComparison.Ordinal));
        }

        bool HasUnselectedOtherLineage(string lineageKey)
        {
            return pool.Any(candidate =>
                !selectedKeys.Contains(Key(candidate))
                && !string.Equals(candidate.Features?.LineageKey, lineageKey, StringComparison.Ordinal));
        }

        string? CategoryKey(FeedCandidateRecord candidate) =>
            NormalizeFacetKey(candidate.Features?.CategoryKey);

        string? AreaKey(FeedCandidateRecord candidate) =>
            NormalizeFacetKey(candidate.Features?.Area);

        string? BusinessKey(FeedCandidateRecord candidate) =>
            NormalizeFacetKey(candidate.Features?.BusinessKey
                ?? (candidate.EntityType == "PLACE" ? candidate.EffectiveCanonicalIdentity : null));

        bool HasUnselectedOtherFacet(
            string facetKey,
            Func<FeedCandidateRecord, string?> facetSelector)
        {
            return pool.Any(candidate =>
                !selectedKeys.Contains(Key(candidate))
                && facetSelector(candidate) is { } otherKey
                && !string.Equals(otherKey, facetKey, StringComparison.Ordinal));
        }

        bool CanAdd(
            FeedCandidateRecord candidate,
            bool enforceTypeRatio,
            bool enforceConsecutive,
            bool enforceConcentrationCaps)
        {
            var key = Key(candidate);
            if (!selectedKeys.Add(key)) return false;
            selectedKeys.Remove(key);

            if (enforceTypeRatio
                && typeCounts.GetValueOrDefault(candidate.EntityType) >= maximumPerType
                && HasUnselectedOtherType(candidate.EntityType))
            {
                return false;
            }

            if (enforceConsecutive
                && string.Equals(lastType, candidate.EntityType, StringComparison.Ordinal)
                && consecutiveTypeCount >= maxConsecutive
                && HasUnselectedOtherType(candidate.EntityType))
            {
                return false;
            }

            var creatorKey = candidate.Features?.CreatorKey;
            if (enforceConcentrationCaps
                && !string.IsNullOrWhiteSpace(creatorKey)
                && creatorCounts.GetValueOrDefault(creatorKey) >= MaxItemsPerCreator
                && HasUnselectedOtherCreator(creatorKey))
            {
                return false;
            }

            var lineageKey = candidate.Features?.LineageKey;
            if (enforceConcentrationCaps
                && !string.IsNullOrWhiteSpace(lineageKey)
                && lineageCounts.GetValueOrDefault(lineageKey) >= MaxItemsPerLineage
                && HasUnselectedOtherLineage(lineageKey))
            {
                return false;
            }

            var categoryKey = CategoryKey(candidate);
            if (enforceConcentrationCaps
                && categoryKey is not null
                && categoryCounts.GetValueOrDefault(categoryKey) >= maximumPerCategory
                && HasUnselectedOtherFacet(categoryKey, CategoryKey))
            {
                return false;
            }

            var areaKey = AreaKey(candidate);
            if (enforceConcentrationCaps
                && areaKey is not null
                && areaCounts.GetValueOrDefault(areaKey) >= maximumPerArea
                && HasUnselectedOtherFacet(areaKey, AreaKey))
            {
                return false;
            }

            var businessKey = BusinessKey(candidate);
            if (enforceConcentrationCaps
                && businessKey is not null
                && businessCounts.GetValueOrDefault(businessKey) >= maximumPerBusiness
                && HasUnselectedOtherFacet(businessKey, BusinessKey))
            {
                return false;
            }

            return true;
        }

        void Add(FeedCandidateRecord candidate)
        {
            selected.Add(candidate);
            selectedKeys.Add(Key(candidate));
            typeCounts[candidate.EntityType] = typeCounts.GetValueOrDefault(candidate.EntityType) + 1;
            var creatorKey = candidate.Features?.CreatorKey;
            if (!string.IsNullOrWhiteSpace(creatorKey))
            {
                creatorCounts[creatorKey] = creatorCounts.GetValueOrDefault(creatorKey) + 1;
            }

            var lineageKey = candidate.Features?.LineageKey;
            if (!string.IsNullOrWhiteSpace(lineageKey))
            {
                lineageCounts[lineageKey] = lineageCounts.GetValueOrDefault(lineageKey) + 1;
            }

            var categoryKey = CategoryKey(candidate);
            if (categoryKey is not null)
            {
                categoryCounts[categoryKey] = categoryCounts.GetValueOrDefault(categoryKey) + 1;
            }

            var areaKey = AreaKey(candidate);
            if (areaKey is not null)
            {
                areaCounts[areaKey] = areaCounts.GetValueOrDefault(areaKey) + 1;
            }

            var businessKey = BusinessKey(candidate);
            if (businessKey is not null)
            {
                businessCounts[businessKey] = businessCounts.GetValueOrDefault(businessKey) + 1;
            }

            if (string.Equals(lastType, candidate.EntityType, StringComparison.Ordinal))
            {
                consecutiveTypeCount++;
            }
            else
            {
                lastType = candidate.EntityType;
                consecutiveTypeCount = 1;
            }
        }

        void AddPass(
            Func<FeedCandidateRecord, bool> predicate,
            bool enforceTypeRatio,
            bool enforceConsecutive,
            bool enforceCreatorCap)
        {
            foreach (var candidate in pool)
            {
                if (selected.Count >= target) break;
                if (!predicate(candidate) || !CanAdd(candidate, enforceTypeRatio, enforceConsecutive, enforceCreatorCap)) continue;
                Add(candidate);
            }
        }

        foreach (var (entityType, minimum) in new[]
                 {
                     ("TRACE", config.CandidateSources.Trace.Minimum),
                     ("PLACE", config.CandidateSources.Place.Minimum)
                 })
        {
            var desired = Math.Min(target, Math.Max(0, minimum));
            while (typeCounts.GetValueOrDefault(entityType) < desired && selected.Count < target)
            {
                var candidate = pool.FirstOrDefault(item =>
                    item.EntityType == entityType
                    && !selectedKeys.Contains(Key(item))
                    && CanAdd(item, true, false, true));
                if (candidate is null) break;
                Add(candidate);
            }
        }

        if (explorationTarget > 0)
        {
            AddPass(
                candidate => candidate.Features?.IsExplorationCandidate == true
                    && selected.Count(item => item.Features?.IsExplorationCandidate == true) < explorationTarget,
                true,
                true,
                true);
        }

        AddPass(_ => true, true, true, true);

        // If a source or creator is the only remaining supply, preserve page
        // fullness rather than dropping otherwise eligible public candidates.
        AddPass(_ => true, false, false, false);

        var positions = pool
            .Select((candidate, index) => (candidate, index))
            .ToDictionary(item => Key(item.candidate), item => item.index, StringComparer.Ordinal);
        return selected
            .OrderBy(candidate => positions[Key(candidate)])
            .ToArray();
    }

    private static string Key(FeedCandidateRecord candidate) =>
        string.Concat(candidate.EntityType, ":", candidate.EntityId);

    private static string? NormalizeFacetKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
