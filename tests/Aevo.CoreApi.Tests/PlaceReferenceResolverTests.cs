using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceReferenceResolverTests
{
    private static readonly Guid RetiredPlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174050");
    private static readonly Guid CurrentPlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174051");

    public static IEnumerable<object[]> AllSurfaces() =>
        Enum.GetValues<PlaceReferenceSurface>()
            .Select(surface => new object[] { surface });

    [Theory]
    [MemberData(nameof(AllSurfaces))]
    public void EveryCrossProductSurfaceUsesTheSameBoundedRedirectRule(PlaceReferenceSurface surface)
    {
        var resolver = Resolver();

        var result = resolver.ResolveCanonical(surface, RetiredPlaceId);

        Assert.Equal(PlaceReferenceResolutionStatus.Redirected, result.Status);
        Assert.True(result.IsSafeRead);
        Assert.True(result.Redirected);
        Assert.Equal(RetiredPlaceId, result.RequestedPlaceId);
        Assert.Equal(CurrentPlaceId, result.ResolvedPlaceId);
        Assert.Equal("merged", result.RedirectReason);
        Assert.Equal(1, result.RedirectHops);
        Assert.Equal("place-reference-v1", result.ResolverVersion);
    }

    [Fact]
    public void ResolvesOnlyAnExplicitLegacyMappingAndNeverGuessesFromTheExternalId()
    {
        var resolver = Resolver();

        var mapped = resolver.Resolve(new PlaceReference(
            PlaceReferenceSurface.Feed,
            " AEVO.STORE ",
            " store-42 "));
        var unknown = resolver.Resolve(new PlaceReference(
            PlaceReferenceSurface.Trace,
            "aevo.store",
            "store-unknown"));

        Assert.Equal(PlaceReferenceResolutionStatus.Resolved, mapped.Status);
        Assert.Equal(CurrentPlaceId, mapped.ResolvedPlaceId);
        Assert.Equal(PlaceReferenceResolutionStatus.Unresolved, unknown.Status);
        Assert.Null(unknown.ResolvedPlaceId);
    }

    [Fact]
    public void RejectsLoopsAndRetiredWritesThroughTheSharedAdapter()
    {
        var loopStart = Guid.Parse("123e4567-e89b-12d3-a456-426614174060");
        var loopEnd = Guid.Parse("123e4567-e89b-12d3-a456-426614174061");
        var resolver = new PlaceReferenceResolver(
            Array.Empty<PlaceLegacyMappingObservation>(),
            new Dictionary<Guid, Guid> { [loopStart] = loopEnd, [loopEnd] = loopStart },
            maxHops: 3);

        var loop = resolver.ResolveCanonical(PlaceReferenceSurface.Save, loopStart);
        Assert.Equal(PlaceReferenceResolutionStatus.UnsafeRedirect, loop.Status);
        Assert.False(loop.IsSafeRead);
        Assert.Throws<InvalidOperationException>(() => resolver.EnsureWritable(
            new PlaceReference(PlaceReferenceSurface.Save, PlaceReferenceResolver.CanonicalNamespace, loopStart.ToString("D"))));

        Assert.Throws<RetiredPlaceWriteException>(() => Resolver().EnsureWritable(
            new PlaceReference(
                PlaceReferenceSurface.Booking,
                PlaceReferenceResolver.CanonicalNamespace,
                RetiredPlaceId.ToString("D"))));
    }

    [Fact]
    public void RejectsConflictingExplicitMappingsAtConstruction()
    {
        var observations = new[]
        {
            new PlaceLegacyMappingObservation("tracedee", "place-7", "snapshot-1", RetiredPlaceId, "linked", "first", 0.9m),
            new PlaceLegacyMappingObservation("tracedee", "place-7", "snapshot-1", CurrentPlaceId, "linked", "second", 0.8m)
        };

        Assert.Throws<InvalidOperationException>(() => new PlaceReferenceResolver(
            observations,
            new Dictionary<Guid, Guid>()));
    }

    private static PlaceReferenceResolver Resolver() =>
        new(
            new[]
            {
                new PlaceLegacyMappingObservation(
                    "aevo.store",
                    "store-42",
                    "unversioned",
                    CurrentPlaceId,
                    "linked",
                    "business-link",
                    1m)
            },
            new Dictionary<Guid, Guid> { [RetiredPlaceId] = CurrentPlaceId },
            new Dictionary<Guid, string> { [RetiredPlaceId] = "merged" });
}
