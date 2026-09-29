using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedPlaceReferenceAdapterTests
{
    [Theory]
    [InlineData("store-profile", "aevo.store")]
    [InlineData("tracedee-place", "aevo.tracedee")]
    public void PreservesTheFeedSourceNamespaceAndStableLegacyIdentity(string source, string expectedNamespace)
    {
        var candidate = new FeedCandidateRecord(
            "PLACE",
            "feed-item-id",
            source,
            DateTimeOffset.UnixEpoch,
            "stable-key",
            HydrationId: "legacy-place-id");

        var reference = FeedPlaceReferenceAdapter.ToReference(candidate);
        var contract = FeedPlaceReferenceAdapter.ToContract(reference, null, false, false);

        Assert.Equal(expectedNamespace, contract.Namespace);
        Assert.Equal("legacy-place-id", contract.ExternalId);
        Assert.Equal("unversioned", contract.SourceVersion);
        Assert.Null(contract.CanonicalPlaceId);
        Assert.Equal("unresolved", contract.ResolutionStatus);
        Assert.False(contract.Redirected);
        Assert.Equal("place-reference-v1", contract.ResolverVersion);
    }

    [Fact]
    public void ExposesTheResolvedTargetAndRedirectMetadataWithoutChangingFeedIdentity()
    {
        var candidate = new FeedCandidateRecord(
            "PLACE",
            "store-legacy",
            "store-profile",
            DateTimeOffset.UnixEpoch,
            "store:legacy",
            HydrationId: "store-legacy");
        var reference = FeedPlaceReferenceAdapter.ToReference(candidate);
        var retiredPlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174050");
        var currentPlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174051");
        var resolution = new PlaceReferenceResolver(
            new[]
            {
                new PlaceLegacyMappingObservation(
                    "aevo.store",
                    "store-legacy",
                    "unversioned",
                    retiredPlaceId,
                    "linked",
                    "business-link",
                    1m)
            },
            new Dictionary<Guid, Guid> { [retiredPlaceId] = currentPlaceId },
            new Dictionary<Guid, string> { [retiredPlaceId] = "merged" })
            .Resolve(reference);

        var contract = FeedPlaceReferenceAdapter.ToContract(reference, resolution, true, false);

        Assert.Equal(currentPlaceId.ToString("D"), contract.CanonicalPlaceId);
        Assert.Equal("redirected", contract.ResolutionStatus);
        Assert.True(contract.Redirected);
        Assert.Equal("merged", contract.RedirectReason);
    }
}
