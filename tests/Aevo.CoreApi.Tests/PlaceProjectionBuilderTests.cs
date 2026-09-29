using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceProjectionBuilderTests
{
    private static readonly Guid PlaceId = Guid.Parse("123e4567-e89b-12d3-a456-426614174030");

    [Fact]
    public void BuildsAReplayableSummaryProjectionWithoutLiveState()
    {
        var observedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        var summary = Summary();

        var first = PlaceProjectionBuilder.Build(summary, "places-public-v1", "source-1", observedAt, TimeSpan.FromMinutes(5));
        var second = PlaceProjectionBuilder.Build(summary, "places-public-v1", "source-1", observedAt, TimeSpan.FromMinutes(5));

        Assert.Equal(first with { Payload = default }, second with { Payload = default });
        Assert.Equal(first.Payload.GetRawText(), second.Payload.GetRawText());
        Assert.Equal(PlaceId, first.PlaceId);
        Assert.Equal(JsonValueKind.Object, first.Payload.ValueKind);
        Assert.False(first.Payload.TryGetProperty("estimatedWaitMinutes", out _));
        Assert.False(first.Payload.TryGetProperty("queueLength", out _));
    }

    private static PlaceSummaryContract Summary() => new(
        PlaceId.ToString("D"),
        "ari-cafe",
        "Ari Cafe",
        new[] { new PlaceLocalizedNameContract("th", "อารี คาเฟ่", "canonical") },
        new PlaceCategoryContract("cafe", "Cafe", Array.Empty<PlaceLocalizedNameContract>()),
        "visible",
        new PlaceGeoPointContract(100.54, 13.78),
        null,
        "Ari",
        null,
        null,
        new PlaceVerificationContract("unverified", null, null, null),
        null,
        new PlaceCapabilitySummaryContract(
            false,
            false,
            false,
            false,
            null,
            new PlaceFreshnessContract("fresh", null, null, "source-1")),
        Array.Empty<PlaceAttributionContract>(),
        null);
}
