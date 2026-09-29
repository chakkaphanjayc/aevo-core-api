using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly IReadOnlyList<string> TraceTopics = new[] { "coffee", "art" };

    [Fact]
    public void SerializesConcreteTracePayloadThroughFeedItemInterface()
    {
        IFeedItemContract item = new FeedTraceItemContract(
            "TRACE",
            "trace-1",
            "opaque-token",
            "trace-slug",
            "A Trace",
            "Description",
            "Creator",
            "Bangkok",
            TraceTopics,
            3,
            "NEW_TRACE",
            DateTimeOffset.Parse("2026-09-23T12:00:00Z"));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize<IReadOnlyList<IFeedItemContract>>(
            new[] { item },
            JsonOptions));
        var payload = document.RootElement[0];

        Assert.Equal("TRACE", payload.GetProperty("itemType").GetString());
        Assert.Equal("trace-slug", payload.GetProperty("slug").GetString());
        Assert.Equal("A Trace", payload.GetProperty("title").GetString());
        Assert.Equal(3, payload.GetProperty("stopCount").GetInt32());
    }

    [Fact]
    public void SerializesConcretePlacePayloadAndReferenceThroughFeedItemInterface()
    {
        IFeedItemContract item = new FeedPlaceItemContract(
            "PLACE",
            "place-1",
            "opaque-token",
            "place-slug",
            "A Place",
            "Description",
            "Bangkok",
            "Cafe",
            null,
            "POPULAR_PLACE",
            new FeedPlaceReferenceContract(
                "legacy",
                "external-1",
                "v1",
                null,
                "unresolved",
                false,
                null,
                "resolver-v1"));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize<IReadOnlyList<IFeedItemContract>>(
            new[] { item },
            JsonOptions));
        var payload = document.RootElement[0];

        Assert.Equal("PLACE", payload.GetProperty("itemType").GetString());
        Assert.Equal("place-slug", payload.GetProperty("slug").GetString());
        Assert.Equal("unresolved", payload.GetProperty("placeReference").GetProperty("resolutionStatus").GetString());
    }
}
