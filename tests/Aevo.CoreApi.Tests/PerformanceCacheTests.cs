using Aevo.CoreApi.Data;
using Aevo.CoreApi.Runtime;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PerformanceCacheTests
{
    [Fact]
    public void DatabaseMetricsSeparateOverlappingWallTimeFromAggregateTime()
    {
        var performance = new RequestPerformanceContext("request-1");
        var start = Stopwatch.GetTimestamp();
        var fiveMilliseconds = Math.Max(1L, Stopwatch.Frequency / 200);
        var tenMilliseconds = fiveMilliseconds * 2;

        performance.RecordDatabase(40, start, start + tenMilliseconds);
        performance.RecordDatabase(35, start + fiveMilliseconds, start + tenMilliseconds + fiveMilliseconds);

        var snapshot = performance.Snapshot();

        Assert.Equal(2, snapshot.DatabaseQueryCount);
        Assert.Equal(75, snapshot.DatabaseAggregateMilliseconds);
        Assert.InRange(snapshot.DatabaseWallMilliseconds, 14.9, 15.1);
    }

    [Fact]
    public async Task SingleFlightDoesNotRetainCompletedResults()
    {
        var flight = new AevoSingleFlight<string?>();
        var calls = 0;

        async Task<string?> Load()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(10, CancellationToken.None);
            return null;
        }

        var first = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => flight.RunAsync("session:v1", Load)));
        var second = await flight.RunAsync("session:v1", Load);

        Assert.All(first, value => Assert.Null(value));
        Assert.Null(second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SingleFlightLoadsOneSourceValueForTwentyConcurrentMisses()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        var cache = new AevoMemoryCache(memoryCache);
        var calls = 0;

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrCreateAsync(
                "core:test:single-flight:v1",
                TimeSpan.FromSeconds(5),
                async _ =>
                {
                    Interlocked.Increment(ref calls);
                    await Task.Delay(15, CancellationToken.None);
                    return "source-value";
                },
                CancellationToken.None))
            .ToArray();

        var values = await Task.WhenAll(tasks);

        Assert.All(values, value => Assert.Equal("source-value", value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RemoveForcesARefreshAndDifferentKeysDoNotShareValues()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        var cache = new AevoMemoryCache(memoryCache);
        var calls = 0;

        async Task<string> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return $"value-{calls}";
        }

        var registry = await cache.GetOrCreateAsync("core:application-registry:v1", TimeSpan.FromMinutes(1), Load, CancellationToken.None);
        var launchTarget = await cache.GetOrCreateAsync("core:application-launch-target:v1:PLAY:development", TimeSpan.FromMinutes(1), Load, CancellationToken.None);
        cache.Remove("core:application-registry:v1");
        var refreshed = await cache.GetOrCreateAsync("core:application-registry:v1", TimeSpan.FromMinutes(1), Load, CancellationToken.None);

        Assert.Equal("value-1", registry);
        Assert.Equal("value-2", launchTarget);
        Assert.Equal("value-3", refreshed);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExpiredEntryDoesNotRemainAuthoritative()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        var cache = new AevoMemoryCache(memoryCache);
        var calls = 0;

        async Task<int> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return calls;
        }

        var first = await cache.GetOrCreateAsync("core:test:ttl:v1", TimeSpan.FromMilliseconds(20), Load, CancellationToken.None);
        await Task.Delay(100);
        var second = await cache.GetOrCreateAsync("core:test:ttl:v1", TimeSpan.FromMilliseconds(20), Load, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void AuditCursorIsBoundToFilterAndCannotBeTamperedWith()
    {
        var signer = new AuditCursorSigner(new string('s', 64));
        var position = new AuditCursorPosition(
            new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero),
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            "ADMIN");

        var cursor = signer.Create(position);

        Assert.True(signer.TryVerify(cursor, "admin", DateTimeOffset.UtcNow, out var decoded));
        Assert.Equal(position, decoded);
        Assert.False(signer.TryVerify(cursor, "HUB", DateTimeOffset.UtcNow, out _));
        Assert.False(signer.TryVerify($"{cursor}x", "ADMIN", DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void ApplicationCacheKeysNormalizeScopeWithoutIncludingTenantData()
    {
        Assert.Equal(
            "core:application-launch-target:v1:PLAY:development",
            AevoCacheKeys.ApplicationLaunchTarget("play", "DEVELOPMENT"));
        Assert.DoesNotContain("organization", AevoCacheKeys.ApplicationRegistry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("store", AevoCacheKeys.ApplicationRegistry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HubCacheKeysFollowConsistentNamespacing()
    {
        var userId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var orgId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var storeId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var membershipId = Guid.Parse("44444444-4444-4444-8444-444444444444");

        Assert.Equal("core:session:v1:HUB:abc123hash", AevoCacheKeys.Session("hub", "abc123hash"));
        Assert.Equal($"core:hub:principal:v1:{userId}:{orgId}:{storeId}", AevoCacheKeys.HubPrincipal(userId, orgId, storeId));
        Assert.Equal($"core:hub:stores:v1:{orgId}:all", AevoCacheKeys.HubStores(orgId, true, membershipId));
        Assert.Equal($"core:hub:stores:v1:{orgId}:{membershipId}", AevoCacheKeys.HubStores(orgId, false, membershipId));
        Assert.Equal($"core:hub:templates:v1:{orgId}", AevoCacheKeys.HubTemplates(orgId));
        Assert.Equal($"core:hub:profiles:v1:{orgId}:all", AevoCacheKeys.HubProfiles(orgId, true, membershipId));
        Assert.Equal($"core:hub:org:v1:{userId}:{orgId}", AevoCacheKeys.HubOrganization(userId, orgId));
    }

    [Fact]
    public async Task SessionCacheEliminatesSubsequentResolutionsWithinTtl()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });
        var cache = new AevoMemoryCache(memoryCache);
        var calls = 0;

        Task<string> Resolve(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("session-payload");
        }

        var key = AevoCacheKeys.Session("HUB", "token-hash-1");
        var first = await cache.GetOrCreateAsync(key, TimeSpan.FromSeconds(30), Resolve, CancellationToken.None);
        var second = await cache.GetOrCreateAsync(key, TimeSpan.FromSeconds(30), Resolve, CancellationToken.None);
        var third = await cache.GetOrCreateAsync(key, TimeSpan.FromSeconds(30), Resolve, CancellationToken.None);

        Assert.Equal("session-payload", first);
        Assert.Equal("session-payload", second);
        Assert.Equal("session-payload", third);
        Assert.Equal(1, calls);

        // Invalidate on revocation
        cache.Remove(key);
        var afterRevocation = await cache.GetOrCreateAsync(key, TimeSpan.FromSeconds(30), Resolve, CancellationToken.None);
        Assert.Equal("session-payload", afterRevocation);
        Assert.Equal(2, calls);
    }
}
