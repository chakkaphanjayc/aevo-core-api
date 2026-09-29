namespace Aevo.CoreApi.Data;

public enum PlaceRedirectResolutionStatus
{
    Resolved,
    NotRedirected,
    LoopDetected,
    HopLimitExceeded,
    InvalidTarget
}

public sealed record PlaceRedirectResolution(
    Guid RequestedPlaceId,
    Guid ResolvedPlaceId,
    int Hops,
    PlaceRedirectResolutionStatus Status,
    IReadOnlyList<Guid> Path)
{
    public bool IsSafe => Status is PlaceRedirectResolutionStatus.Resolved or PlaceRedirectResolutionStatus.NotRedirected;
    public bool WasRedirected => Hops > 0 && IsSafe;
}

public sealed class RetiredPlaceWriteException(Guid requestedPlaceId, Guid resolvedPlaceId)
    : InvalidOperationException($"Place {requestedPlaceId:D} is retired; write to resolved Place {resolvedPlaceId:D} instead.");

public sealed class PlaceRedirectResolutionException(string message) : Exception(message);

/// <summary>
/// Shared, bounded redirect resolution for every Place read surface. The
/// resolver is deliberately pure so Map, Search, Feed, Trace, Saves, booking
/// links, old URLs, and analytics adapters can use identical safety rules.
/// </summary>
public static class PlaceRedirectResolver
{
    public static PlaceRedirectResolution EnsureWritable(
        Guid requestedPlaceId,
        IReadOnlyDictionary<Guid, Guid> redirects,
        int maxHops = 5)
    {
        var resolution = Resolve(requestedPlaceId, redirects, maxHops);
        if (!resolution.IsSafe)
        {
            throw new InvalidOperationException("The Place redirect graph is invalid and cannot accept a write.");
        }
        if (resolution.WasRedirected)
        {
            throw new RetiredPlaceWriteException(requestedPlaceId, resolution.ResolvedPlaceId);
        }
        return resolution;
    }

    public static PlaceRedirectResolution Resolve(
        Guid requestedPlaceId,
        IReadOnlyDictionary<Guid, Guid> redirects,
        int maxHops = 5)
    {
        if (requestedPlaceId == Guid.Empty)
        {
            return new PlaceRedirectResolution(
                requestedPlaceId,
                requestedPlaceId,
                0,
                PlaceRedirectResolutionStatus.InvalidTarget,
                Array.Empty<Guid>());
        }

        if (maxHops is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxHops));

        var visited = new HashSet<Guid>();
        var path = new List<Guid> { requestedPlaceId };
        var current = requestedPlaceId;

        for (var hop = 0; hop <= maxHops; hop++)
        {
            if (!visited.Add(current))
            {
                return new PlaceRedirectResolution(
                    requestedPlaceId,
                    current,
                    hop,
                    PlaceRedirectResolutionStatus.LoopDetected,
                    path);
            }

            if (!redirects.TryGetValue(current, out var target))
            {
                return new PlaceRedirectResolution(
                    requestedPlaceId,
                    current,
                    hop,
                    hop == 0 ? PlaceRedirectResolutionStatus.NotRedirected : PlaceRedirectResolutionStatus.Resolved,
                    path);
            }

            if (target == Guid.Empty)
            {
                return new PlaceRedirectResolution(
                    requestedPlaceId,
                    current,
                    hop,
                    PlaceRedirectResolutionStatus.InvalidTarget,
                    path);
            }

            if (hop == maxHops)
            {
                path.Add(target);
                return new PlaceRedirectResolution(
                    requestedPlaceId,
                    target,
                    hop + 1,
                    PlaceRedirectResolutionStatus.HopLimitExceeded,
                    path);
            }

            current = target;
            path.Add(current);
        }

        throw new InvalidOperationException("Place redirect resolution did not terminate.");
    }
}
