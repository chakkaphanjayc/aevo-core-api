using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceRedirectResolverTests
{
    [Fact]
    public void ResolvesAChainAndPreservesThePath()
    {
        var first = Guid.Parse("123e4567-e89b-12d3-a456-426614174000");
        var second = Guid.Parse("123e4567-e89b-12d3-a456-426614174001");
        var third = Guid.Parse("123e4567-e89b-12d3-a456-426614174002");

        var result = PlaceRedirectResolver.Resolve(
            first,
            new Dictionary<Guid, Guid> { [first] = second, [second] = third });

        Assert.True(result.IsSafe);
        Assert.True(result.WasRedirected);
        Assert.Equal(third, result.ResolvedPlaceId);
        Assert.Equal(2, result.Hops);
        Assert.Equal(new[] { first, second, third }, result.Path);
    }

    [Fact]
    public void RejectsLoopsAndUnboundedChains()
    {
        var first = Guid.Parse("123e4567-e89b-12d3-a456-426614174010");
        var second = Guid.Parse("123e4567-e89b-12d3-a456-426614174011");
        var loop = PlaceRedirectResolver.Resolve(
            first,
            new Dictionary<Guid, Guid> { [first] = second, [second] = first });
        Assert.Equal(PlaceRedirectResolutionStatus.LoopDetected, loop.Status);
        Assert.False(loop.IsSafe);

        var chain = Enumerable.Range(0, 8).Select(index => Guid.NewGuid()).ToArray();
        var redirects = chain.Take(chain.Length - 1).Zip(chain.Skip(1)).ToDictionary(pair => pair.First, pair => pair.Second);
        var capped = PlaceRedirectResolver.Resolve(chain[0], redirects, maxHops: 3);
        Assert.Equal(PlaceRedirectResolutionStatus.HopLimitExceeded, capped.Status);
        Assert.False(capped.IsSafe);
    }

    [Fact]
    public void RejectsNewWritesToRetiredIds()
    {
        var retired = Guid.Parse("123e4567-e89b-12d3-a456-426614174020");
        var current = Guid.Parse("123e4567-e89b-12d3-a456-426614174021");

        Assert.Throws<RetiredPlaceWriteException>(() => PlaceRedirectResolver.EnsureWritable(
            retired,
            new Dictionary<Guid, Guid> { [retired] = current }));
        Assert.Equal(
            current,
            PlaceRedirectResolver.EnsureWritable(current, new Dictionary<Guid, Guid>()).ResolvedPlaceId);
    }
}
