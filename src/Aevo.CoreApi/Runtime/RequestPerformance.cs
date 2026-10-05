using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Aevo.CoreApi.Runtime;

public sealed record RequestPerformanceSnapshot(
    double TotalMilliseconds,
    double AuthenticationMilliseconds,
    double AuthorizationMilliseconds,
    double DatabaseWallMilliseconds,
    double DatabaseAggregateMilliseconds,
    double DatabaseConnectionOpenMilliseconds,
    double ExternalApiMilliseconds,
    double SerializationMilliseconds,
    double CacheLookupMilliseconds,
    int DatabaseQueryCount,
    string CacheStatus,
    string DatabaseOperationBreakdown);

/// <summary>
/// Request-local performance data. It deliberately contains timings and
/// bounded counters only; session tokens, cookies, claims, and tenant data do
/// not enter this object or its logs.
/// </summary>
public sealed class RequestPerformanceContext(string requestId)
{
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, double> phases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> cacheStatuses = new(StringComparer.Ordinal);
    private readonly object databaseLock = new();
    private readonly object cacheLock = new();
    private int databaseQueryCount;
    private double databaseAggregateMilliseconds;
    private double databaseConnectionOpenMilliseconds;
    private readonly List<(long StartTimestamp, long EndTimestamp)> databaseSpans = [];
    private readonly Dictionary<string, double> databaseOperations = new(StringComparer.Ordinal);
    private double cacheLookupMilliseconds;

    public string RequestId { get; } = requestId;

    public IDisposable Measure(string phase)
    {
        var stopwatch = Stopwatch.StartNew();
        return new Scope(() =>
        {
            stopwatch.Stop();
            phases.AddOrUpdate(phase, stopwatch.Elapsed.TotalMilliseconds, (_, current) => current + stopwatch.Elapsed.TotalMilliseconds);
        });
    }

    public void RecordDatabase(string operation, double milliseconds, long startTimestamp, long endTimestamp)
    {
        Interlocked.Increment(ref databaseQueryCount);
        lock (databaseLock)
        {
            databaseAggregateMilliseconds += milliseconds;
            databaseSpans.Add((startTimestamp, endTimestamp));
            var normalizedOperation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation.Trim();
            databaseOperations[normalizedOperation] = databaseOperations.GetValueOrDefault(normalizedOperation) + milliseconds;
        }
    }

    public void RecordDatabase(double milliseconds, long startTimestamp, long endTimestamp)
        => RecordDatabase("unknown", milliseconds, startTimestamp, endTimestamp);

    public void RecordDatabaseConnectionOpen(double milliseconds)
    {
        lock (databaseLock) databaseConnectionOpenMilliseconds += milliseconds;
    }

    public void RecordCacheLookup(double milliseconds)
    {
        lock (cacheLock) cacheLookupMilliseconds += milliseconds;
    }

    public void MarkCache(string status)
    {
        var normalized = status.Trim().ToUpperInvariant();
        if (normalized is "HIT" or "MISS" or "BYPASS" or "STALE") cacheStatuses.TryAdd(normalized, 0);
    }

    public RequestPerformanceSnapshot Snapshot()
    {
        var totalMilliseconds = total.Elapsed.TotalMilliseconds;
        var authentication = phases.GetValueOrDefault("authentication");
        var authorization = phases.GetValueOrDefault("authorization");
        var externalApi = phases.GetValueOrDefault("external_api");
        List<(long StartTimestamp, long EndTimestamp)> spans;
        double databaseAggregate;
        double databaseConnectionOpen;
        string databaseOperationBreakdown;
        lock (databaseLock)
        {
            spans = [.. databaseSpans];
            databaseAggregate = databaseAggregateMilliseconds;
            databaseConnectionOpen = databaseConnectionOpenMilliseconds;
            databaseOperationBreakdown = string.Join(",", databaseOperations
                .OrderByDescending(entry => entry.Value)
                .Select(entry => $"{entry.Key}={entry.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")
                .Take(12));
        }

        var databaseWall = DatabaseWallMilliseconds(spans);
        var accounted = authentication + authorization + externalApi + databaseWall;
        var serialization = Math.Max(0, totalMilliseconds - accounted);
        double cacheLookup;
        lock (cacheLock) cacheLookup = cacheLookupMilliseconds;
        var cacheStatus = cacheStatuses.ContainsKey("STALE")
            ? "STALE"
            : cacheStatuses.ContainsKey("MISS")
                ? "MISS"
                : cacheStatuses.ContainsKey("HIT")
                    ? "HIT"
                    : "BYPASS";

        return new RequestPerformanceSnapshot(
            Math.Round(totalMilliseconds, 2),
            Math.Round(authentication, 2),
            Math.Round(authorization, 2),
            Math.Round(databaseWall, 2),
            Math.Round(databaseAggregate, 2),
            Math.Round(databaseConnectionOpen, 2),
            Math.Round(externalApi, 2),
            Math.Round(serialization, 2),
            Math.Round(cacheLookup, 2),
            Volatile.Read(ref databaseQueryCount),
            cacheStatus,
            databaseOperationBreakdown);
    }

    private static double DatabaseWallMilliseconds(List<(long StartTimestamp, long EndTimestamp)> spans)
    {
        if (spans.Count == 0) return 0;

        var ordered = spans
            .Where(span => span.EndTimestamp > span.StartTimestamp)
            .OrderBy(span => span.StartTimestamp)
            .ToArray();
        if (ordered.Length == 0) return 0;

        var unionStart = ordered[0].StartTimestamp;
        var unionEnd = ordered[0].EndTimestamp;
        long unionTicks = 0;
        foreach (var span in ordered.Skip(1))
        {
            if (span.StartTimestamp <= unionEnd)
            {
                unionEnd = Math.Max(unionEnd, span.EndTimestamp);
                continue;
            }

            unionTicks += unionEnd - unionStart;
            unionStart = span.StartTimestamp;
            unionEnd = span.EndTimestamp;
        }

        unionTicks += unionEnd - unionStart;
        return unionTicks * 1000d / Stopwatch.Frequency;
    }

    private sealed class Scope(Action complete) : IDisposable
    {
        private int completed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0) complete();
        }
    }
}

public static class RequestPerformance
{
    private static readonly AsyncLocal<RequestPerformanceContext?> CurrentContext = new();

    public static RequestPerformanceContext? Current => CurrentContext.Value;

    public static RequestPerformanceContext Attach(HttpContext context, string requestId)
    {
        var performance = new RequestPerformanceContext(requestId);
        context.Items[typeof(RequestPerformanceContext)] = performance;
        CurrentContext.Value = performance;
        return performance;
    }

    public static void Detach(RequestPerformanceContext performance)
    {
        if (ReferenceEquals(CurrentContext.Value, performance)) CurrentContext.Value = null;
    }

    public static IDisposable Measure(HttpContext context, string phase)
    {
        return context.Items[typeof(RequestPerformanceContext)] is RequestPerformanceContext performance
            ? performance.Measure(phase)
            : NoopDisposable.Instance;
    }

    public static void MarkCache(string status) => Current?.MarkCache(status);

    public static T MeasureCacheLookup<T>(Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            stopwatch.Stop();
            Current?.RecordCacheLookup(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public static async Task<T> MeasureDatabaseAsync<T>(string operation, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            return await action();
        }
        finally
        {
            stopwatch.Stop();
            Current?.RecordDatabase(operation, stopwatch.Elapsed.TotalMilliseconds, startTimestamp, Stopwatch.GetTimestamp());
        }
    }

    public static async Task MeasureDatabaseAsync(string operation, Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await action();
        }
        finally
        {
            stopwatch.Stop();
            Current?.RecordDatabase(operation, stopwatch.Elapsed.TotalMilliseconds, startTimestamp, Stopwatch.GetTimestamp());
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
