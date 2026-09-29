using Aevo.CoreApi.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class ReadinessStateTests
{
    private static readonly string[] FeedSecretConfigurationNames =
        ["AEVO_FEED_CURSOR_SECRET", "AEVO_FEED_EVENT_WORKER_TOKEN"];

    [Fact]
    public void DevelopmentReadinessDoesNotRequireManagedFeedSecrets()
    {
        var result = new ReadinessState(Configuration(new Dictionary<string, string?>
        {
            ["AEVO_ENVIRONMENT"] = "development",
            ["AEVO_DATABASE_URL"] = "postgres://database",
            ["AEVO_IDENTITY_PLATFORM_PROJECT_ID"] = "local-development",
            ["AEVO_SESSION_SECRET"] = "session-secret",
            ["AEVO_ACCOUNTS_API_ORIGIN"] = "http://accounts.test",
            ["AEVO_ACCOUNTS_SERVICE_SECRET"] = "accounts-secret",
            ["AEVO_CORE_API_SERVICE_SECRET"] = "core-secret"
        })).Evaluate();

        Assert.True(result.Ready);
        Assert.Empty(result.MissingConfiguration);
    }

    [Fact]
    public void ProductionReadinessFailsClosedWhenFeedSecretsAreMissingOrWeak()
    {
        var result = new ReadinessState(Configuration(new Dictionary<string, string?>
        {
            ["AEVO_ENVIRONMENT"] = "production",
            ["AEVO_DATABASE_URL"] = "postgres://database",
            ["AEVO_IDENTITY_PLATFORM_PROJECT_ID"] = "production-project",
            ["AEVO_SESSION_SECRET"] = "session-secret",
            ["AEVO_ACCOUNTS_API_ORIGIN"] = "https://accounts.test",
            ["AEVO_ACCOUNTS_SERVICE_SECRET"] = "accounts-secret",
            ["AEVO_CORE_API_SERVICE_SECRET"] = "core-secret",
            ["AEVO_FEED_CURSOR_SECRET"] = "too-short",
            ["AEVO_FEED_EVENT_WORKER_TOKEN"] = "replace-with-a-separate-feed-event-worker-token"
        })).Evaluate();

        Assert.False(result.Ready);
        Assert.Equal(FeedSecretConfigurationNames, result.MissingConfiguration);
    }

    [Fact]
    public void ProductionReadinessPassesWithSeparateStrongFeedSecrets()
    {
        var result = new ReadinessState(Configuration(new Dictionary<string, string?>
        {
            ["AEVO_ENVIRONMENT"] = "production",
            ["AEVO_DATABASE_URL"] = "postgres://database",
            ["AEVO_IDENTITY_PLATFORM_PROJECT_ID"] = "production-project",
            ["AEVO_SESSION_SECRET"] = "session-secret",
            ["AEVO_ACCOUNTS_API_ORIGIN"] = "https://accounts.test",
            ["AEVO_ACCOUNTS_SERVICE_SECRET"] = "accounts-secret",
            ["AEVO_CORE_API_SERVICE_SECRET"] = "core-secret",
            ["AEVO_FEED_CURSOR_SECRET"] = "feed-cursor-secret-with-at-least-32-bytes",
            ["AEVO_FEED_EVENT_WORKER_TOKEN"] = "feed-event-worker-token-with-at-least-32-bytes"
        })).Evaluate();

        Assert.True(result.Ready);
        Assert.Empty(result.MissingConfiguration);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
