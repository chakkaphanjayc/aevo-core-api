using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Runtime;

/// <summary>
/// Warms a small set of pooled Core connections before Kestrel accepts traffic.
/// This moves the first network handshakes out of the first authenticated or
/// parallel dashboard request without changing the database or authorization
/// source of truth.
/// </summary>
public sealed class CoreDatabaseWarmupService(
    CoreDataStore database,
    IConfiguration configuration,
    ILogger<CoreDatabaseWarmupService> logger) : IHostedService
{
    private static readonly Action<ILogger, double, Exception?> WarmupCompletedLog =
        LoggerMessage.Define<double>(
            LogLevel.Information,
            new EventId(2301, nameof(WarmupCompletedLog)),
            "Core database connection pool warmed in {ElapsedMs:F1} ms.");

    private static readonly Action<ILogger, int, Exception?> WarmupTimeoutLog =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(2302, nameof(WarmupTimeoutLog)),
            "Core database warmup exceeded {TimeoutSeconds} seconds; serving traffic and allowing the pool to recover lazily.");

    private static readonly Action<ILogger, Exception?> WarmupFailedLog =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2303, nameof(WarmupFailedLog)),
            "Core database warmup failed; serving traffic and allowing the pool to recover lazily.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!database.IsConfigured || !IsEnabled(configuration["AEVO_DATABASE_WARMUP"])) return;

        var timeoutSeconds = int.TryParse(configuration["AEVO_DATABASE_WARMUP_TIMEOUT_SECONDS"], out var configured)
            ? Math.Clamp(configured, 1, 15)
            : 5;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var connectionCount = int.TryParse(configuration["AEVO_DATABASE_WARMUP_CONNECTIONS"], out var configuredConnections)
                ? Math.Clamp(configuredConnections, 1, 16)
                : 4;
            await database.WarmConnectionPoolAsync(connectionCount, timeout.Token);
            var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            WarmupCompletedLog(logger, elapsedMs, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            WarmupTimeoutLog(logger, timeoutSeconds, null);
        }
        catch (Exception error)
        {
            WarmupFailedLog(logger, error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsEnabled(string? value) =>
        string.IsNullOrWhiteSpace(value)
            || !string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
