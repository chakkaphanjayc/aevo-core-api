using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedSourceHealthPolicyTests
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarksPopulatedRecentSourceHealthy()
    {
        var health = FeedSourceHealthPolicy.Evaluate(
            FeedSourceHealthPolicy.TraceSource,
            12,
            CheckedAt.AddHours(-1),
            CheckedAt,
            86_400);

        Assert.Equal("HEALTHY", health.Status);
        Assert.True(health.Available);
        Assert.True(health.Fresh);
        Assert.Equal(12, health.EligibleCount);
        Assert.Null(health.FailureCode);
    }

    [Fact]
    public void MarksEmptySourceAvailableWithoutCallingItStale()
    {
        var health = FeedSourceHealthPolicy.Evaluate(
            FeedSourceHealthPolicy.StoreProfileSource,
            0,
            null,
            CheckedAt,
            86_400);

        Assert.Equal("EMPTY", health.Status);
        Assert.True(health.Available);
        Assert.True(health.Fresh);
        Assert.Equal(0, health.EligibleCount);
    }

    [Fact]
    public void MarksPopulatedOldSourceStale()
    {
        var health = FeedSourceHealthPolicy.Evaluate(
            FeedSourceHealthPolicy.TraceDeePlaceSource,
            4,
            CheckedAt.AddDays(-8),
            CheckedAt,
            7 * 24 * 60 * 60);

        Assert.Equal("STALE", health.Status);
        Assert.True(health.Available);
        Assert.False(health.Fresh);
        Assert.Equal("FEED_SOURCE_STALE", health.FailureCode);
    }

    [Fact]
    public void ProducesAnUnavailableEntryForEveryCanonicalSource()
    {
        var sources = FeedSourceHealthPolicy.SourceNames(
            FeedSourceHealthPolicy.DefaultFreshnessWindowSeconds,
            "FEED_SOURCE_DATABASE_UNAVAILABLE",
            CheckedAt);

        Assert.Equal(
            new[]
            {
                FeedSourceHealthPolicy.TraceSource,
                FeedSourceHealthPolicy.TraceDeePlaceSource,
                FeedSourceHealthPolicy.StoreProfileSource
            },
            sources.Select(source => source.Source));
        Assert.All(sources, source =>
        {
            Assert.Equal("UNAVAILABLE", source.Status);
            Assert.False(source.Available);
            Assert.False(source.Fresh);
            Assert.Equal("FEED_SOURCE_DATABASE_UNAVAILABLE", source.FailureCode);
        });
    }
}
