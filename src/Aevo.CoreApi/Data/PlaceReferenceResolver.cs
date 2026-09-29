using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public enum PlaceReferenceSurface
{
    Map,
    Search,
    Feed,
    Trace,
    Save,
    Business,
    Booking,
    Url,
    Analytics
}

public enum PlaceReferenceResolutionStatus
{
    Resolved,
    Redirected,
    Unresolved,
    Rejected,
    Invalid,
    UnsafeRedirect
}

public sealed record PlaceReference(
    PlaceReferenceSurface Surface,
    string Namespace,
    string ExternalId,
    string? SourceVersion = null);

public sealed record PlaceReferenceResolution(
    PlaceReferenceSurface Surface,
    string Namespace,
    string ExternalId,
    string SourceVersion,
    Guid? RequestedPlaceId,
    Guid? ResolvedPlaceId,
    PlaceReferenceResolutionStatus Status,
    bool Redirected,
    string? RedirectReason,
    string ResolverVersion,
    string? FailureReason)
{
    public int RedirectHops { get; init; }

    public bool IsSafeRead => Status is PlaceReferenceResolutionStatus.Resolved or PlaceReferenceResolutionStatus.Redirected;

    public bool IsWritable => Status == PlaceReferenceResolutionStatus.Resolved && !Redirected;
}

/// <summary>
/// Shared read/write boundary for legacy and canonical Place references.
/// Legacy IDs resolve only through an explicit mapping; names, slugs,
/// coordinates, and provider IDs are never used to derive a canonical ID.
/// </summary>
public sealed class PlaceReferenceResolver
{
    public const string CanonicalNamespace = "aevo.place";
    public const string ResolverVersion = "place-reference-v1";

    private readonly Dictionary<string, PlaceLegacyMappingObservation> mappings;
    private readonly Dictionary<Guid, Guid> redirects;
    private readonly Dictionary<Guid, string> redirectReasons;
    private readonly int maxHops;

    public PlaceReferenceResolver(
        IEnumerable<PlaceLegacyMappingObservation> mappingObservations,
        IReadOnlyDictionary<Guid, Guid> redirects,
        IReadOnlyDictionary<Guid, string>? redirectReasons = null,
        int maxHops = PlaceApiContract.MaxRedirectHops)
    {
        ArgumentNullException.ThrowIfNull(mappingObservations);
        ArgumentNullException.ThrowIfNull(redirects);
        if (maxHops is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxHops));

        mappings = BuildMappingIndex(mappingObservations);
        this.redirects = new Dictionary<Guid, Guid>(redirects);
        this.redirectReasons = redirectReasons is null
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(redirectReasons);
        this.maxHops = maxHops;
    }

    public PlaceReferenceResolution Resolve(PlaceReference reference)
    {
        var normalizedNamespace = NormalizeNamespace(reference.Namespace);
        var normalizedExternalId = reference.ExternalId?.Trim() ?? string.Empty;
        var normalizedVersion = NormalizeVersion(reference.SourceVersion);

        if (!Enum.IsDefined(reference.Surface))
        {
            return Failure(
                reference,
                normalizedNamespace,
                normalizedExternalId,
                normalizedVersion,
                PlaceReferenceResolutionStatus.Invalid,
                "The reference surface is not supported.");
        }

        if (string.IsNullOrWhiteSpace(normalizedNamespace) || string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            return Failure(
                reference,
                normalizedNamespace,
                normalizedExternalId,
                normalizedVersion,
                PlaceReferenceResolutionStatus.Invalid,
                "Reference namespace and external ID are required.");
        }

        Guid requestedPlaceId;
        if (string.Equals(normalizedNamespace, CanonicalNamespace, StringComparison.Ordinal))
        {
            if (!Guid.TryParse(normalizedExternalId, out requestedPlaceId) || requestedPlaceId == Guid.Empty)
            {
                return Failure(
                    reference,
                    normalizedNamespace,
                    normalizedExternalId,
                    normalizedVersion,
                    PlaceReferenceResolutionStatus.Invalid,
                    "A canonical Place reference must contain a non-empty UUID.");
            }
        }
        else
        {
            var key = PlaceCompatibilityMapper.SourceKey(
                normalizedNamespace,
                normalizedExternalId,
                normalizedVersion);
            if (!mappings.TryGetValue(key, out var mapping))
            {
                return Failure(
                    reference,
                    normalizedNamespace,
                    normalizedExternalId,
                    normalizedVersion,
                    PlaceReferenceResolutionStatus.Unresolved,
                    "No explicit canonical Place mapping exists.");
            }

            var status = NormalizeMappingStatus(mapping.MatchStatus, mapping.PlaceId);
            if (status != "linked" || !mapping.PlaceId.HasValue || mapping.PlaceId.Value == Guid.Empty)
            {
                var reason = string.IsNullOrWhiteSpace(mapping.MatchReason)
                    ? $"The explicit mapping is {status}."
                    : mapping.MatchReason;
                return Failure(
                    reference,
                    normalizedNamespace,
                    normalizedExternalId,
                    normalizedVersion,
                    PlaceReferenceResolutionStatus.Rejected,
                    reason);
            }

            requestedPlaceId = mapping.PlaceId.Value;
        }

        var redirect = PlaceRedirectResolver.Resolve(requestedPlaceId, redirects, maxHops);
        if (!redirect.IsSafe)
        {
            return new PlaceReferenceResolution(
                reference.Surface,
                normalizedNamespace,
                normalizedExternalId,
                normalizedVersion,
                requestedPlaceId,
                redirect.ResolvedPlaceId == Guid.Empty ? null : redirect.ResolvedPlaceId,
                PlaceReferenceResolutionStatus.UnsafeRedirect,
                false,
                null,
                ResolverVersion,
                $"The redirect graph is {redirect.Status}.")
            {
                RedirectHops = redirect.Hops
            };
        }

        var wasRedirected = redirect.WasRedirected;
        var redirectReason = wasRedirected && redirect.Path.Count > 1
            ? redirectReasons.GetValueOrDefault(redirect.Path[0], "redirected")
            : null;
        return new PlaceReferenceResolution(
            reference.Surface,
            normalizedNamespace,
            normalizedExternalId,
            normalizedVersion,
            requestedPlaceId,
            redirect.ResolvedPlaceId,
            wasRedirected ? PlaceReferenceResolutionStatus.Redirected : PlaceReferenceResolutionStatus.Resolved,
            wasRedirected,
            redirectReason,
            ResolverVersion,
            null)
        {
            RedirectHops = redirect.Hops
        };
    }

    public PlaceReferenceResolution ResolveCanonical(PlaceReferenceSurface surface, Guid placeId) =>
        Resolve(new PlaceReference(surface, CanonicalNamespace, placeId.ToString("D")));

    public PlaceReferenceResolution EnsureWritable(PlaceReference reference)
    {
        var resolution = Resolve(reference);
        if (!resolution.IsSafeRead || !resolution.RequestedPlaceId.HasValue)
        {
            throw new InvalidOperationException(
                resolution.FailureReason ?? "The Place reference is not safe for writing.");
        }
        if (resolution.Redirected && resolution.ResolvedPlaceId.HasValue)
        {
            throw new RetiredPlaceWriteException(
                resolution.RequestedPlaceId.Value,
                resolution.ResolvedPlaceId.Value);
        }
        return resolution;
    }

    private static Dictionary<string, PlaceLegacyMappingObservation> BuildMappingIndex(
        IEnumerable<PlaceLegacyMappingObservation> observations)
    {
        var index = new Dictionary<string, PlaceLegacyMappingObservation>(StringComparer.Ordinal);
        foreach (var group in observations.GroupBy(Key, StringComparer.Ordinal))
        {
            var ordered = group
                .Select(NormalizeMapping)
                .OrderByDescending(observation => observation.PlaceId.HasValue)
                .ThenByDescending(observation => NormalizeMappingStatus(observation.MatchStatus, observation.PlaceId) == "linked")
                .ThenByDescending(observation => observation.MatchConfidence ?? -1)
                .ToArray();
            var winner = ordered[0];
            if (ordered.Skip(1).Any(other => other.PlaceId != winner.PlaceId))
            {
                throw new InvalidOperationException($"Conflicting canonical Place mappings for {group.Key}.");
            }
            index[group.Key] = winner;
        }
        return index;
    }

    private static PlaceLegacyMappingObservation NormalizeMapping(PlaceLegacyMappingObservation observation) =>
        observation with
        {
            Namespace = NormalizeNamespace(observation.Namespace),
            ExternalId = observation.ExternalId.Trim(),
            SourceVersion = NormalizeVersion(observation.SourceVersion),
            MatchStatus = NormalizeMappingStatus(observation.MatchStatus, observation.PlaceId)
        };

    private static PlaceReferenceResolution Failure(
        PlaceReference reference,
        string normalizedNamespace,
        string normalizedExternalId,
        string normalizedVersion,
        PlaceReferenceResolutionStatus status,
        string reason) =>
        new(
            reference.Surface,
            normalizedNamespace,
            normalizedExternalId,
            normalizedVersion,
            null,
            null,
            status,
            false,
            null,
            ResolverVersion,
            reason);

    private static string Key(PlaceLegacyMappingObservation observation) =>
        PlaceCompatibilityMapper.SourceKey(
            NormalizeNamespace(observation.Namespace),
            observation.ExternalId.Trim(),
            NormalizeVersion(observation.SourceVersion));

    private static string NormalizeNamespace(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? PlaceCompatibilityMapper.Unversioned : value.Trim();

    private static string NormalizeMappingStatus(string? status, Guid? placeId)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        if (placeId.HasValue && normalized == "linked") return "linked";
        return normalized is "candidate" or "rejected" or "retired" ? normalized : "unresolved";
    }
}
