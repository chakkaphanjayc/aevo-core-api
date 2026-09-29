using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlacePublicReadServiceTests
{
    [Fact]
    public void MapParserRequiresACompleteNonAntimeridianViewport()
    {
        var context = Context("?west=100&south=13&east=101&north=14&zoom=12&limit=20&savedOnly=true");

        Assert.True(PlaceRequestParser.TryMap(context.Request.Query, out var request, out var error));
        Assert.Empty(error);
        Assert.Equal(20, request.Limit);
        Assert.Equal(12, request.Zoom);
        Assert.True(request.SavedOnly);

        var crossing = Context("?west=170&south=13&east=-170&north=14&zoom=12");
        Assert.False(PlaceRequestParser.TryMap(crossing.Request.Query, out _, out var crossingError));
        Assert.Contains("antimeridian", crossingError, StringComparison.OrdinalIgnoreCase);

        var oversized = Context("?west=-180&south=-70&east=180&north=70&zoom=1");
        Assert.False(PlaceRequestParser.TryMap(oversized.Request.Query, out _, out var oversizedError));
        Assert.Contains("cannot exceed", oversizedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchAndNearbyParsersStayBounded()
    {
        var search = Context("?q=อารีย์&categoryId=cafe,restaurant&limit=50&savedOnly=true");
        Assert.True(PlaceRequestParser.TrySearch(search.Request.Query, out var searchRequest, out var searchError));
        Assert.Empty(searchError);
        Assert.Equal("อารีย์", searchRequest.Query);
        Assert.Equal(2, searchRequest.CategoryIds!.Count);
        Assert.True(searchRequest.SavedOnly);

        var invalidSavedOnly = Context("?q=อารีย์&savedOnly=maybe");
        Assert.False(PlaceRequestParser.TrySearch(invalidSavedOnly.Request.Query, out _, out var invalidSavedOnlyError));
        Assert.Contains("savedOnly", invalidSavedOnlyError, StringComparison.OrdinalIgnoreCase);

        var nearby = Context("?longitude=100.54&latitude=13.78&radiusMeters=1500&limit=24");
        Assert.True(PlaceRequestParser.TryNearby(nearby.Request.Query, out var nearbyRequest, out var nearbyError));
        Assert.Empty(nearbyError);
        Assert.Equal(1500, nearbyRequest.RadiusMeters);

        var unbounded = Context("?sort=relevance");
        Assert.False(PlaceRequestParser.TrySearch(unbounded.Request.Query, out _, out var unboundedError));
        Assert.Contains("requires", unboundedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadServiceFailsClosedWhenTheProjectionDatabaseIsNotConfigured()
    {
        await using var database = new CoreDataStore(new ConfigurationBuilder().Build(), NullLogger<CoreDataStore>.Instance);
        var service = new PlacePublicReadService(database);

        await Assert.ThrowsAsync<PlaceProjectionUnavailableException>(() =>
            service.GetMapOverlayAsync(
                new PlaceMapOverlayRequestContract(
                    new PlaceBoundsContract(100, 13, 101, 14),
                    12,
                    null,
                    null,
                    10),
                "request-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task SavedOnlyReadsFailClosedWithoutCoreResolvedFilterState()
    {
        await using var database = new CoreDataStore(new ConfigurationBuilder().Build(), NullLogger<CoreDataStore>.Instance);
        var service = new PlacePublicReadService(database);
        var bounds = new PlaceBoundsContract(100, 13, 101, 14);

        await Assert.ThrowsAsync<PlaceProjectionUnavailableException>(() =>
            service.GetMapOverlayAsync(
                new PlaceMapOverlayRequestContract(bounds, 12, null, null, 10, true),
                "saved-map-request",
                CancellationToken.None));

        await Assert.ThrowsAsync<PlaceProjectionUnavailableException>(() =>
            service.SearchAsync(
                new PlaceSearchRequestContract(Query: "อารีย์", SavedOnly: true),
                "saved-search-request",
                CancellationToken.None,
                new HashSet<Guid>()));
    }

    [Fact]
    public void PlaceCursorRejectsTamperingAndBindsTheQueryShape()
    {
        var signer = new PlaceCursorSigner("place-cursor-test-secret-012345678901234567890123");
        var anchor = new PlaceCursorAnchor(
            "search",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "ร้านกาแฟ",
            null,
            Guid.Parse("123e4567-e89b-12d3-a456-426614174000"));
        var token = signer.Sign(anchor);

        var verified = signer.Verify(token, DateTimeOffset.UtcNow);
        Assert.True(verified.IsValid);
        Assert.Equal(anchor, verified.Anchor);

        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        Assert.False(signer.Verify(tampered, DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void PlaceCursorFingerprintChangesWhenTheQueryChanges()
    {
        var first = new PlaceSearchRequestContract(Query: "อารีย์", Limit: 24);
        var second = first with { Query = "ทองหล่อ" };

        Assert.NotEqual(PlaceCursorSigner.Fingerprint(first), PlaceCursorSigner.Fingerprint(second));

        var saved = first with { SavedOnly = true };
        Assert.NotEqual(PlaceCursorSigner.Fingerprint(first), PlaceCursorSigner.Fingerprint(saved));
        Assert.NotEqual(
            PlaceCursorSigner.Fingerprint(saved, "actor-a"),
            PlaceCursorSigner.Fingerprint(saved, "actor-b"));
    }

    [Fact]
    public void ResponseMetadataCollapsesMixedProjectionValuesWithoutThrowing()
    {
        var same = new[] { "places-public-v1", "places-public-v1" };
        var mixed = new[] { "places-public-v1", "places-public-v2" };
        var empty = Array.Empty<string>();

        Assert.Equal("places-public-v1", PlacePublicReadService.MetadataValue(same));
        Assert.Equal("mixed", PlacePublicReadService.MetadataValue(mixed));
        Assert.Equal("unknown", PlacePublicReadService.MetadataValue(empty));
    }

    private static DefaultHttpContext Context(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return context;
    }
}
