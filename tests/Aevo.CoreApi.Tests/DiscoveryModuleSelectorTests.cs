using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class DiscoveryModuleSelectorTests
{
    private static readonly IReadOnlyList<string> ForYouOrder = new[] { "FOR_YOU" };
    private static readonly IReadOnlyList<string> NearSelectedAreaOrder = new[] { "NEAR_SELECTED_AREA" };
    private static readonly IReadOnlyList<string> NewAndUsefulOrder = new[] { "NEW_AND_USEFUL" };
    private static readonly IReadOnlyList<string> PortfolioOrder = new[] { "FOR_YOU", "NEAR_SELECTED_AREA", "NEW_AND_USEFUL", "COMMUNITY_FAVORITES" };
    private static readonly string[] ExpectedPortfolioModules = { "FOR_YOU", "NEAR_SELECTED_AREA", "NEW_AND_USEFUL", "COMMUNITY_FAVORITES" };

    [Fact]
    public void SelectsAnEntityOnlyForYouModuleWithExplicitVibeReason()
    {
        var modules = DiscoveryModuleSelector.Select(
            new IFeedItemContract[]
            {
                new FeedPlaceItemContract("PLACE", "place-1", "token-1", "place", "Place", "", "Ari", "Cafe", null, "POPULAR_PLACE")
            },
            "Ari",
            new FeedDiscoveryIntentContract("art", null, null, null));

        var module = Assert.Single(modules);
        Assert.Equal("FOR_YOU", module.ModuleId);
        Assert.Equal("BECAUSE_VIBE", module.ReasonCode);
        Assert.Equal("PLACE", Assert.Single(module.Items).ItemType);
    }

    [Fact]
    public void DoesNotCreateAStandaloneSocialModuleOrExposeUnsupportedItems()
    {
        var modules = DiscoveryModuleSelector.Select(
            new IFeedItemContract[]
            {
                new FakeItem("POST", "post-1", "token-post"),
                new FeedTraceItemContract("TRACE", "trace-1", "token-trace", "trace", "Trace", "", "Creator", "Ari", Array.Empty<string>(), 1, "NEW_TRACE", DateTimeOffset.UtcNow)
            },
            null,
            null);

        var module = Assert.Single(modules);
        var item = Assert.Single(module.Items);
        Assert.Equal("TRACE", item.ItemType);
        Assert.Null(module.ReasonCode);
    }

    [Fact]
    public void ModulePolicyCanDisableOrBoundTheServerOwnedShelf()
    {
        var item = new FeedPlaceItemContract("PLACE", "place-1", "token-1", "place", "Place", "", "Ari", "Cafe", null, "POPULAR_PLACE");
        var disabled = DiscoveryModuleSelector.Select(
            new[] { item },
            "Ari",
            null,
            new FeedDiscoveryModulesConfig(false, 1, 1, ForYouOrder));
        var belowMinimum = DiscoveryModuleSelector.Select(
            new[] { item },
            "Ari",
            null,
            new FeedDiscoveryModulesConfig(true, 2, 3, ForYouOrder));

        Assert.Empty(disabled);
        Assert.Empty(belowMinimum);
    }

    [Fact]
    public void DoesNotEmitEmptyShelvesWhenMinimumIsZero()
    {
        var modules = DiscoveryModuleSelector.Select(
            new IFeedItemContract[]
            {
                new FeedPlaceItemContract("PLACE", "place-1", "token-1", "place", "Place", "", "Ari", "Cafe", null, "POPULAR_PLACE")
            },
            "Thonglor",
            null,
            new FeedDiscoveryModulesConfig(true, 0, 4, NearSelectedAreaOrder));

        Assert.Empty(modules);
    }

    [Fact]
    public void SelectsOnlyModulesBackedByCurrentEntityInventory()
    {
        var modules = DiscoveryModuleSelector.Select(
            new IFeedItemContract[]
            {
                new FeedTraceItemContract("TRACE", "trace-new", "token-new", "trace-new", "New trace", "", "Creator", "Ari", Array.Empty<string>(), 2, "NEW_TRACE", DateTimeOffset.UtcNow),
                new FeedPlaceItemContract("PLACE", "place-popular", "token-popular", "place-popular", "Popular place", "", "Thonglor", "Cafe", null, "POPULAR_PLACE"),
                new FeedPlaceItemContract("PLACE", "place-area", "token-area", "place-area", "Ari place", "", "Ari", "Gallery", null, "NEARBY_PLACE"),
                new FeedPlaceItemContract("PLACE", "place-favorite-area", "token-favorite-area", "place-favorite-area", "Ari favorite", "", "Ari", "Cafe", null, "POPULAR_PLACE"),
            },
            "Ari",
            null,
            new FeedDiscoveryModulesConfig(true, 1, 4, PortfolioOrder));

        Assert.Equal(ExpectedPortfolioModules, modules.Select(module => module.ModuleId));
        Assert.Equal("NEAR_SELECTED_AREA", modules[1].ReasonCode);
        Assert.Equal("NEW_IN_AREA", modules[2].ReasonCode);
        Assert.DoesNotContain(modules, module => module.ModuleId == "BOOKABLE_NOW");
        Assert.DoesNotContain(modules[1].Items, item => item.ItemId == "place-popular");
        var itemKeys = modules
            .SelectMany(module => module.Items)
            .Select(item => $"{item.ItemType}:{item.ItemId}")
            .ToArray();
        Assert.Equal(itemKeys.Length, itemKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MarksOnlyShelvesAffectedByFailedGeneratorsAsDegraded()
    {
        var trace = new FeedTraceItemContract(
            "TRACE",
            "trace-new",
            "token-new",
            "trace-new",
            "New trace",
            "",
            "Creator",
            "Ari",
            Array.Empty<string>(),
            2,
            "NEW_TRACE",
            DateTimeOffset.UtcNow);
        var modules = DiscoveryModuleSelector.Select(
            new[] { trace },
            "Ari",
            null,
            new FeedDiscoveryModulesConfig(true, 1, 4, NewAndUsefulOrder),
            new[]
            {
                new FeedGeneratorTelemetry("trace-v1", "timeout", 0, 0, 0, 100, true, "FEED_GENERATOR_TIMEOUT"),
                new FeedGeneratorTelemetry("place-v1", "failed", 0, 0, 0, 100, true, "FEED_GENERATOR_FAILED")
            });

        var module = Assert.Single(modules);
        Assert.True(module.Degraded);

        var placeFailureOnly = DiscoveryModuleSelector.Select(
            new[] { trace },
            "Ari",
            null,
            new FeedDiscoveryModulesConfig(true, 1, 4, NewAndUsefulOrder),
            new[]
            {
                new FeedGeneratorTelemetry("trace-v1", "succeeded", 1, 1, 0, 1, false),
                new FeedGeneratorTelemetry("place-v1", "timeout", 0, 0, 0, 100, true, "FEED_GENERATOR_TIMEOUT")
            });

        Assert.False(Assert.Single(placeFailureOnly).Degraded);
    }

    private sealed record FakeItem(string ItemType, string Id, string ItemToken) : IFeedItemContract;
}
