using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed record PlaceCompatibilityCandidate(
    string Namespace,
    string ExternalId,
    string SourceVersion,
    string SourceKind,
    string Name,
    PlaceGeometryContract? Geometry,
    PlaceGeoPointContract? DisplayPoint,
    DateTimeOffset ObservedAt,
    string SourceRevision);

public sealed record PlaceLegacyMappingObservation(
    string Namespace,
    string ExternalId,
    string SourceVersion,
    Guid? PlaceId,
    string MatchStatus,
    string? MatchReason,
    decimal? MatchConfidence);

public sealed record PlaceCompatibilityDecision(
    string Namespace,
    string ExternalId,
    string SourceVersion,
    Guid? PlaceId,
    string MatchStatus,
    string? MatchReason,
    decimal? MatchConfidence,
    string SourceRevision,
    bool CanPublishToPublicProjection);

/// <summary>
/// Reconciles legacy/source observations with the explicit mapping table.
/// Unknown records remain unresolved; this class never derives an Aevo Place
/// ID from a name, coordinate, slug, or provider ID.
/// </summary>
public static class PlaceCompatibilityMapper
{
    public const string Unversioned = "unversioned";

    public static IReadOnlyList<PlaceCompatibilityDecision> Map(
        IEnumerable<PlaceCompatibilityCandidate> candidates,
        IEnumerable<PlaceLegacyMappingObservation> existingMappings)
    {
        var existing = existingMappings
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ResolveExistingConflict(group),
                StringComparer.Ordinal);

        var normalizedCandidates = candidates
            .Select(NormalizeCandidate)
            .OrderBy(candidate => Key(candidate), StringComparer.Ordinal)
            .ToArray();

        var decisions = new List<PlaceCompatibilityDecision>(normalizedCandidates.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in normalizedCandidates)
        {
            var key = Key(candidate);
            if (!seen.Add(key)) continue;

            if (!existing.TryGetValue(key, out var mapping))
            {
                decisions.Add(new PlaceCompatibilityDecision(
                    candidate.Namespace,
                    candidate.ExternalId,
                    candidate.SourceVersion,
                    null,
                    "unresolved",
                    "No explicit legacy mapping exists.",
                    null,
                    candidate.SourceRevision,
                    false));
                continue;
            }

            var status = NormalizeStatus(mapping.MatchStatus, mapping.PlaceId);
            decisions.Add(new PlaceCompatibilityDecision(
                candidate.Namespace,
                candidate.ExternalId,
                candidate.SourceVersion,
                mapping.PlaceId,
                status,
                mapping.MatchReason,
                mapping.MatchConfidence,
                candidate.SourceRevision,
                status == "linked" && mapping.PlaceId.HasValue));
        }

        return decisions;
    }

    public static string SourceKey(string @namespace, string externalId, string? sourceVersion = null)
    {
        return Key(new PlaceCompatibilityCandidate(
            @namespace,
            externalId,
            NormalizeVersion(sourceVersion),
            "derived",
            "placeholder",
            null,
            null,
            DateTimeOffset.UnixEpoch,
            "unresolved"));
    }

    private static PlaceCompatibilityCandidate NormalizeCandidate(PlaceCompatibilityCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Namespace)) throw new ArgumentException("Source namespace is required.", nameof(candidate));
        if (string.IsNullOrWhiteSpace(candidate.ExternalId)) throw new ArgumentException("External ID is required.", nameof(candidate));
        if (string.IsNullOrWhiteSpace(candidate.Name)) throw new ArgumentException("Candidate name is required.", nameof(candidate));
        return candidate with
        {
            Namespace = candidate.Namespace.Trim().ToLowerInvariant(),
            ExternalId = candidate.ExternalId.Trim(),
            SourceVersion = NormalizeVersion(candidate.SourceVersion),
            Name = candidate.Name.Trim()
        };
    }

    private static PlaceLegacyMappingObservation ResolveExistingConflict(
        IEnumerable<PlaceLegacyMappingObservation> observations)
    {
        var ordered = observations
            .Select(observation => observation with
            {
                Namespace = observation.Namespace.Trim().ToLowerInvariant(),
                ExternalId = observation.ExternalId.Trim(),
                SourceVersion = NormalizeVersion(observation.SourceVersion),
                MatchStatus = NormalizeStatus(observation.MatchStatus, observation.PlaceId)
            })
            .OrderByDescending(observation => observation.PlaceId.HasValue)
            .ThenByDescending(observation => observation.MatchStatus == "linked")
            .ThenByDescending(observation => observation.MatchConfidence ?? -1)
            .ToArray();

        var winner = ordered[0];
        if (ordered.Skip(1).Any(other => other.PlaceId != winner.PlaceId))
        {
            throw new InvalidOperationException($"Conflicting canonical Place mappings for {Key(winner)}.");
        }
        return winner;
    }

    private static string NormalizeStatus(string? status, Guid? placeId)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        if (placeId.HasValue && normalized == "linked") return "linked";
        return normalized is "candidate" or "rejected" or "retired" ? normalized : "unresolved";
    }

    private static string NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Unversioned : version.Trim();

    private static string Key(PlaceCompatibilityCandidate candidate) =>
        $"{candidate.Namespace.Trim().ToLowerInvariant()}|{candidate.ExternalId.Trim()}|{NormalizeVersion(candidate.SourceVersion)}";

    private static string Key(PlaceLegacyMappingObservation observation) =>
        $"{observation.Namespace.Trim().ToLowerInvariant()}|{observation.ExternalId.Trim()}|{NormalizeVersion(observation.SourceVersion)}";
}
