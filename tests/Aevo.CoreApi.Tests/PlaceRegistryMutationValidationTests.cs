using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceRegistryMutationValidationTests
{
    [Fact]
    public void AllowsCreateWithoutExpectedRevision()
    {
        var request = Request();

        var placeId = PlaceRegistryMutationValidation.ValidateUpsert(request);

        Assert.Equal(Guid.Parse(request.Summary.Id), placeId);
    }

    [Fact]
    public void RejectsExpectedRevisionOnCreateWhenItIsNotPositive()
    {
        var error = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateUpsert(Request(expectedRevision: 0)));

        Assert.Equal("PLACE_REVISION_INVALID", error.Code);
    }

    [Fact]
    public void RejectsSelfParentAndNonGeoJsonGeometry()
    {
        var selfParentError = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateUpsert(Request(parentPlaceId: PlaceId.ToString("D"))));
        Assert.Equal("PLACE_PARENT_INVALID", selfParentError.Code);

        var invalidGeometryError = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateUpsert(Request(
                canonicalGeometry: new PlaceGeometryContract("LineString", JsonDocument.Parse("[[100,13],[101,14]]").RootElement.Clone()))));
        Assert.Equal("PLACE_GEOMETRY_INVALID", invalidGeometryError.Code);
    }

    [Fact]
    public void RequiresReasonAndCurrentRevisionForDelete()
    {
        var error = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateDelete(new PlaceAdminDeleteRequestContract("test cleanup")));

        Assert.Equal("PLACE_REVISION_REQUIRED", error.Code);
    }

    [Fact]
    public void ValidatesScopedLegacyDeletionKeysAndReasons()
    {
        PlaceRegistryMutationValidation.ValidateLegacyMappingDelete(
            new PlaceAdminLegacyMappingDeleteRequestContract("aevo.store", "store-1", "fixture-1", "test cleanup"));

        var error = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateLegacyMappingDelete(
                new PlaceAdminLegacyMappingDeleteRequestContract("", "store-1", "fixture-1", "test cleanup")));

        Assert.Equal("LEGACY_NAMESPACE_INVALID", error.Code);
    }

    [Fact]
    public void RequiresAnAuditReasonForSourceLinkDeletion()
    {
        var error = Assert.Throws<PlaceMutationRequestException>(() =>
            PlaceRegistryMutationValidation.ValidateSourceLinkDelete(
                new PlaceAdminSourceLinkDeleteRequestContract("")));

        Assert.Equal("REASON_REQUIRED", error.Code);
    }

    private static readonly Guid PlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174090");

    private static PlaceAdminPlaceMutationRequestContract Request(
        long? expectedRevision = null,
        string? parentPlaceId = null,
        PlaceGeometryContract? canonicalGeometry = null) =>
        new(
            new PlaceSummaryContract(
                PlaceId.ToString("D"),
                "mutation-test-place",
                "Mutation test place",
                Array.Empty<PlaceLocalizedNameContract>(),
                new PlaceCategoryContract("test", "Test", Array.Empty<PlaceLocalizedNameContract>()),
                "visible",
                new PlaceGeoPointContract(100.5, 13.7),
                new PlaceGeoPointContract(100.5, 13.7),
                "Bangkok",
                null,
                parentPlaceId,
                new PlaceVerificationContract("unverified", null, null, null),
                null,
                new PlaceCapabilitySummaryContract(
                    false,
                    false,
                    false,
                    false,
                    null,
                    new PlaceFreshnessContract("fresh", null, null, "mutation-test-v1")),
                Array.Empty<PlaceAttributionContract>(),
                null),
            canonicalGeometry,
            null,
            "derived",
            "mutation-test",
            "test-fixture",
            "mutation-test-v1",
            "test mutation",
            expectedRevision,
            "places-public-v1");
}
