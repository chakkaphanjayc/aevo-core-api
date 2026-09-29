namespace Aevo.CoreApi.Runtime;

public sealed record ReadinessResult(
    bool Ready,
    IReadOnlyList<string> MissingConfiguration);

public sealed class ReadinessState(IConfiguration configuration)
{
    public ReadinessResult Evaluate()
    {
        var requiredSettings = new Dictionary<string, string?>
        {
            ["AEVO_DATABASE_URL"] = configuration["AEVO_DATABASE_URL"],
            ["AEVO_IDENTITY_PLATFORM_PROJECT_ID"] = configuration["AEVO_IDENTITY_PLATFORM_PROJECT_ID"],
            ["AEVO_SESSION_SECRET"] = configuration["AEVO_SESSION_SECRET"],
            ["AEVO_ACCOUNTS_API_ORIGIN"] = configuration["AEVO_ACCOUNTS_API_ORIGIN"],
            ["AEVO_ACCOUNTS_SERVICE_SECRET"] = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"],
            ["AEVO_CORE_API_SERVICE_SECRET"] = configuration["AEVO_CORE_API_SERVICE_SECRET"]
        };

        if (RequiresManagedFeedRuntime(configuration["AEVO_ENVIRONMENT"]))
        {
            requiredSettings["AEVO_FEED_CURSOR_SECRET"] = IsUsableSecret(configuration["AEVO_FEED_CURSOR_SECRET"], 32)
                ? configuration["AEVO_FEED_CURSOR_SECRET"]
                : null;
            requiredSettings["AEVO_FEED_EVENT_WORKER_TOKEN"] = IsUsableSecret(configuration["AEVO_FEED_EVENT_WORKER_TOKEN"], 32)
                ? configuration["AEVO_FEED_EVENT_WORKER_TOKEN"]
                : null;
        }

        var missing = requiredSettings
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

        return new ReadinessResult(missing.Length == 0, missing);
    }

    private static bool RequiresManagedFeedRuntime(string? environment)
    {
        var normalized = environment?.Trim().ToLowerInvariant();
        return normalized is "nonprod" or "staging" or "production";
    }

    private static bool IsUsableSecret(string? value, int minimumBytes)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("replace-with", StringComparison.OrdinalIgnoreCase)
            || value.Contains("your-project-ref", StringComparison.OrdinalIgnoreCase)
            || value.Contains("your-secret", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return System.Text.Encoding.UTF8.GetByteCount(value.Trim()) >= minimumBytes;
    }
}
