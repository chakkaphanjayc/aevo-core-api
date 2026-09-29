using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceProjectionReplayValidatorTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReplaysLabelledThaiParentAndCapabilityFixturesDeterministically()
    {
        var fixtures = new[]
        {
            Fixture("point-thai-cafe", "123e4567-e89b-12d3-a456-426614174030", "อารี คาเฟ่", null),
            Fixture("polygon-parent-summary", "123e4567-e89b-12d3-a456-426614174031", "ตลาดอารี", null),
            Fixture("multipolygon-child-summary", "123e4567-e89b-12d3-a456-426614174032", "Ari Market Hall", "123e4567-e89b-12d3-a456-426614174031"),
            Fixture("bookable-static-capability", "123e4567-e89b-12d3-a456-426614174033", "Ari Court", null, bookable: true)
        };
        var options = new PlaceProjectionReplayOptions(
            "places-public-v1",
            "fixture-2026-09-25",
            ObservedAt,
            TimeSpan.FromMinutes(5));

        var first = PlaceProjectionReplayValidator.Validate(fixtures, options);
        var second = PlaceProjectionReplayValidator.Validate(fixtures.Reverse(), options);

        Assert.True(first.IsValid);
        Assert.Equal(4, first.InputCount);
        Assert.Equal(4, first.PublishedCount);
        Assert.Equal(0, first.FailedCount);
        Assert.Equal(0, first.DuplicatePlaceCount);
        Assert.Equal(first.ReplayFingerprint, second.ReplayFingerprint);
        Assert.True(first.MaxPayloadBytes > 0);
        Assert.All(first.Projections, projection =>
        {
            Assert.Equal(JsonValueKind.Object, projection.Payload.ValueKind);
            Assert.True(PlaceProjectionReplayValidator.IsStaticPayload(projection.Payload, out _));
        });
    }

    [Fact]
    public void RejectsDuplicateIdsAndPayloadsOverTheConfiguredBudget()
    {
        var fixtures = new[]
        {
            Fixture("first", "123e4567-e89b-12d3-a456-426614174040", "Example", null),
            Fixture("duplicate", "123e4567-e89b-12d3-a456-426614174040", "Example duplicate", null)
        };
        var report = PlaceProjectionReplayValidator.Validate(
            fixtures,
            new PlaceProjectionReplayOptions(
                "places-public-v1",
                "fixture-duplicate",
                ObservedAt,
                TimeSpan.FromMinutes(5),
                MaxPayloadBytes: 32));

        Assert.False(report.IsValid);
        Assert.Equal(0, report.PublishedCount);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(1, report.DuplicatePlaceCount);
        Assert.Contains(report.Failures, failure => failure.Code == "duplicate-place-id");
        Assert.Contains(report.Failures, failure => failure.Code == "payload-budget-exceeded");
        Assert.False(report.IsWithinPayloadBudget);
    }

    [Fact]
    public void StaticPayloadGuardRejectsFutureLiveStateFields()
    {
        using var document = JsonDocument.Parse("{\"capabilities\":{\"liveState\":{\"slots\":2}}}");

        Assert.False(PlaceProjectionReplayValidator.IsStaticPayload(document.RootElement, out var path));
        Assert.Equal("$.capabilities.liveState", path);
    }

    [Fact]
    public void RejectsNonPublicLifecycleStatesBeforeProjectionPublish()
    {
        var fixture = Fixture("candidate", "123e4567-e89b-12d3-a456-426614174041", "Candidate", null);
        var report = PlaceProjectionReplayValidator.Validate(
            new[] { fixture with
            {
                Summary = fixture.Summary! with
                {
                    Status = "candidate"
                }
            } },
            new PlaceProjectionReplayOptions(
                "places-public-v1",
                "fixture-candidate",
                ObservedAt,
                TimeSpan.FromMinutes(5)));

        Assert.False(report.IsValid);
        Assert.Contains(report.Failures, failure => failure.Code == "non-public-status");
    }

    private static PlaceProjectionReplayFixture Fixture(
        string label,
        string id,
        string name,
        string? parentPlaceId,
        bool bookable = false)
    {
        return new PlaceProjectionReplayFixture(
            label,
            new PlaceSummaryContract(
                id,
                name.ToLowerInvariant().Replace(' ', '-'),
                name,
                new[]
                {
                    new PlaceLocalizedNameContract("th", name, "canonical"),
                    new PlaceLocalizedNameContract("en", name, "alias")
                },
                new PlaceCategoryContract("cafe", "Cafe", Array.Empty<PlaceLocalizedNameContract>()),
                "visible",
                new PlaceGeoPointContract(100.54, 13.78),
                new PlaceGeoPointContract(100.54, 13.78),
                "Ari",
                new PlaceAddressContract(
                    "TH",
                    "Thailand",
                    "Bangkok",
                    "Pathum Wan",
                    "Bangkok",
                    "Ari",
                    "Phahonyothin",
                    "1",
                    "10400",
                    name,
                    Array.Empty<PlaceLocalizedNameContract>()),
                parentPlaceId,
                new PlaceVerificationContract("unverified", null, null, null),
                null,
                new PlaceCapabilitySummaryContract(
                    bookable,
                    false,
                    false,
                    false,
                    bookable ? "/booking/ari-court" : null,
                    new PlaceFreshnessContract("fresh", ObservedAt, ObservedAt.AddMinutes(5), "fixture-2026-09-25")),
                Array.Empty<PlaceAttributionContract>(),
                null));
    }
}
