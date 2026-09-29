using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

internal static class FeedPlaceReferenceAdapter
{
    public static PlaceReference ToReference(FeedCandidateRecord candidate)
    {
        var @namespace = candidate.Source switch
        {
            "store-profile" => "aevo.store",
            "tracedee-place" => "aevo.tracedee",
            _ => "feed.item"
        };
        return new PlaceReference(
            PlaceReferenceSurface.Feed,
            @namespace,
            candidate.EffectiveHydrationId,
            PlaceCompatibilityMapper.Unversioned);
    }

    public static string Key(PlaceReference reference) =>
        $"{reference.Namespace.Trim().ToLowerInvariant()}|{reference.ExternalId.Trim()}|{(string.IsNullOrWhiteSpace(reference.SourceVersion) ? PlaceCompatibilityMapper.Unversioned : reference.SourceVersion.Trim())}";

    public static FeedPlaceReferenceContract ToContract(
        PlaceReference reference,
        PlaceReferenceResolution? resolution,
        bool resolverEnabled,
        bool resolverUnavailable)
    {
        var status = resolution?.Status switch
        {
            PlaceReferenceResolutionStatus.Resolved => "resolved",
            PlaceReferenceResolutionStatus.Redirected => "redirected",
            PlaceReferenceResolutionStatus.Rejected => "rejected",
            PlaceReferenceResolutionStatus.UnsafeRedirect => "unsafe_redirect",
            PlaceReferenceResolutionStatus.Invalid => "invalid",
            PlaceReferenceResolutionStatus.Unresolved => "unresolved",
            _ => resolverUnavailable ? "unavailable" : "unresolved"
        };

        return new FeedPlaceReferenceContract(
            reference.Namespace.Trim().ToLowerInvariant(),
            reference.ExternalId.Trim(),
            string.IsNullOrWhiteSpace(reference.SourceVersion) ? PlaceCompatibilityMapper.Unversioned : reference.SourceVersion.Trim(),
            resolution?.ResolvedPlaceId?.ToString("D"),
            resolverEnabled ? status : "unresolved",
            resolution?.Redirected ?? false,
            resolution?.RedirectReason,
            PlaceReferenceResolver.ResolverVersion);
    }
}
