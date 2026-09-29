using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceCompatibilityMapperTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnmappedSourceRemainsUnresolvedWithoutDerivingAnAevoId()
    {
        var result = PlaceCompatibilityMapper.Map(
            new[] { Candidate("osm", "node/42", "snapshot-1") },
            Array.Empty<PlaceLegacyMappingObservation>());

        var decision = Assert.Single(result);
        Assert.Null(decision.PlaceId);
        Assert.Equal("unresolved", decision.MatchStatus);
        Assert.False(decision.CanPublishToPublicProjection);
    }

    [Fact]
    public void ReplayUsesTheExplicitMappingAndIsStableForPointAndPolygonCandidates()
    {
        var placeId = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");
        var candidates = new[]
        {
            Candidate("osm", "way/9", "snapshot-1") with
            {
                Geometry = new PlaceGeometryContract(
                    "Polygon",
                    System.Text.Json.JsonDocument.Parse("[[[100,13],[101,13],[101,14],[100,13]]]").RootElement.Clone())
            },
            Candidate("aevo.store", "store-1", null)
        };
        var mapping = new[]
        {
            new PlaceLegacyMappingObservation("osm", "way/9", "snapshot-1", placeId, "linked", "reviewed", 0.99m),
            new PlaceLegacyMappingObservation("aevo.store", "store-1", "unversioned", placeId, "linked", "business-link", 1m)
        };

        var first = PlaceCompatibilityMapper.Map(candidates, mapping);
        var second = PlaceCompatibilityMapper.Map(candidates, mapping);

        Assert.Equal(first, second);
        Assert.All(first, decision =>
        {
            Assert.Equal(placeId, decision.PlaceId);
            Assert.Equal("linked", decision.MatchStatus);
            Assert.True(decision.CanPublishToPublicProjection);
        });
    }

    [Fact]
    public void ConflictingMappingsFailClosedInsteadOfChoosingAPlaceSilently()
    {
        var mappings = new[]
        {
            new PlaceLegacyMappingObservation("osm", "node/7", "snapshot-1", Guid.NewGuid(), "linked", "first", 0.9m),
            new PlaceLegacyMappingObservation("osm", "node/7", "snapshot-1", Guid.NewGuid(), "linked", "second", 0.8m)
        };

        Assert.Throws<InvalidOperationException>(() => PlaceCompatibilityMapper.Map(
            new[] { Candidate("osm", "node/7", "snapshot-1") },
            mappings));
    }

    [Fact]
    public void SourceKeyNormalizesNamespacesAndMissingVersions()
    {
        Assert.Equal(
            "osm|node/42|unversioned",
            PlaceCompatibilityMapper.SourceKey(" OSM ", " node/42 "));
    }

    private static PlaceCompatibilityCandidate Candidate(string @namespace, string externalId, string? sourceVersion) =>
        new(
            @namespace,
            externalId,
            sourceVersion ?? "unversioned",
            @namespace.Equals("osm", StringComparison.OrdinalIgnoreCase) ? "osm" : "aevo_admin",
            "Example Place",
            new PlaceGeometryContract("Point", System.Text.Json.JsonDocument.Parse("[100.5,13.7]").RootElement.Clone()),
            new PlaceGeoPointContract(100.5, 13.7),
            ObservedAt,
            sourceVersion ?? "unresolved");
}
