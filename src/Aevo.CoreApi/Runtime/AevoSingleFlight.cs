using System.Collections.Concurrent;

namespace Aevo.CoreApi.Runtime;

/// <summary>
/// Coalesces concurrent loads for the same key without retaining the result.
/// This is deliberately different from a cache: once the current operation
/// completes, a later caller must execute the source-of-truth read again.
/// </summary>
public sealed class AevoSingleFlight<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> flights = new(StringComparer.Ordinal);

    public async Task<T> RunAsync(string key, Func<Task<T>> factory)
    {
        var flight = flights.GetOrAdd(
            key,
            _ => new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await flight.Value.ConfigureAwait(false);
        }
        finally
        {
            flights.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, flight));
        }
    }
}
