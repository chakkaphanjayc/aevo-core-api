using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Aevo.CoreApi.Runtime;

public static class AevoCacheKeys
{
    public const string ApplicationRegistry = "core:application-registry:v1";
    public const string ApplicationConnectionsPrefix = "core:application-connections:v1:";

    public static string ApplicationConnections(string environment)
        => $"{ApplicationConnectionsPrefix}{environment.Trim().ToLowerInvariant()}";

    public static string ApplicationLaunchTarget(string application, string environment)
        => $"core:application-launch-target:v1:{application.Trim().ToUpperInvariant()}:{environment.Trim().ToLowerInvariant()}";

    public static string Session(string appCode, string tokenHash)
        => $"core:session:v1:{appCode.Trim().ToUpperInvariant()}:{tokenHash}";

    public const string AccessSnapshotPrefix = "core:access-snapshot:v1:";
    public const string HubPrincipalPrefix = "core:hub:principal:v1:";
    public const string HubBootstrapPrefix = "core:hub:bootstrap:v1:";

    public static string AccessSnapshot(
        Guid sessionId,
        Guid userId,
        string application,
        Guid? organizationId,
        Guid? storeId,
        bool requireSessionApplicationBinding)
        => $"{AccessSnapshotPrefix}{sessionId}:{userId}:{application.Trim().ToUpperInvariant()}:{organizationId?.ToString() ?? "none"}:{storeId?.ToString() ?? "none"}:{(requireSessionApplicationBinding ? "bound" : "proxy")}";

    public static string HubPrincipal(Guid userId, Guid? organizationId, Guid? storeId)
        => $"{HubPrincipalPrefix}{userId}:{organizationId?.ToString() ?? "none"}:{storeId?.ToString() ?? "none"}";

    public static string HubBootstrap(string tokenHash)
        => $"{HubBootstrapPrefix}{tokenHash}";

    public static string HubStores(Guid organizationId, bool globalAccess, Guid membershipId)
        => globalAccess
            ? $"core:hub:stores:v1:{organizationId}:all"
            : $"core:hub:stores:v1:{organizationId}:{membershipId}";

    public static string HubTemplates(Guid organizationId)
        => $"core:hub:templates:v1:{organizationId}";

    public static string HubProfiles(Guid organizationId, bool globalAccess, Guid membershipId)
        => globalAccess
            ? $"core:hub:profiles:v1:{organizationId}:all"
            : $"core:hub:profiles:v1:{organizationId}:{membershipId}";

    public static string HubOrganization(Guid userId, Guid organizationId)
        => $"core:hub:org:v1:{userId}:{organizationId}";

    public static string HubDashboardProjection(Guid organizationId, Guid? storeId)
        => $"core:hub:dashboard-projection:v1:{organizationId}:{storeId?.ToString() ?? "organization"}";
}

/// <summary>
/// Small bounded L1 cache with single-flight loading. Sensitive snapshots use
/// short TTLs plus explicit invalidation; this cache never becomes a durable
/// authorization store and does not provide an L2/distributed cache.
/// </summary>
public interface IAevoMemoryCache
{
    Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken,
        Func<T, bool>? isValid = null);
    void Put<T>(string key, T value, TimeSpan ttl);
    void Remove(string key);
    void RemoveByPrefix(string prefix);
}

public sealed class AevoMemoryCache(IMemoryCache cache) : IAevoMemoryCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> trackedKeys = new(StringComparer.Ordinal);

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken,
        Func<T, bool>? isValid = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            RequestPerformance.MarkCache("BYPASS");
            return await factory(cancellationToken);
        }

        T? cached = default;
        var cacheHit = RequestPerformance.MeasureCacheLookup(() => cache.TryGetValue(key, out cached));
        if (cacheHit && cached is not null && (isValid is null || isValid(cached)))
        {
            RequestPerformance.MarkCache("HIT");
            return cached;
        }

        if (cacheHit) cache.Remove(key);

        RequestPerformance.MarkCache("MISS");
        var flight = flights.GetOrAdd(
            key,
            _ => new Lazy<Task<object>>(
                () => LoadAsync(key, ttl, factory, isValid, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return (T)await flight.Value;
        }
        finally
        {
            flights.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, flight));
        }
    }

    public void Put<T>(string key, T value, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero || value is null) return;
        cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1
        });
        trackedKeys[key] = 0;
    }

    public void Remove(string key)
    {
        cache.Remove(key);
        trackedKeys.TryRemove(key, out _);
        RequestPerformance.MarkCache("STALE");
    }

    public void RemoveByPrefix(string prefix)
    {
        foreach (var key in trackedKeys.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) Remove(key);
        }
    }

    private async Task<object> LoadAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        Func<T, bool>? isValid,
        CancellationToken cancellationToken)
    {
        T? cached = default;
        var cacheHit = RequestPerformance.MeasureCacheLookup(() => cache.TryGetValue(key, out cached));
        if (cacheHit && cached is not null && (isValid is null || isValid(cached))) return cached;
        if (cacheHit) cache.Remove(key);
        var value = await factory(cancellationToken);
        if (value is not null)
        {
            cache.Set(key, value, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1
            });
            trackedKeys[key] = 0;
        }
        return value!;
    }
}
