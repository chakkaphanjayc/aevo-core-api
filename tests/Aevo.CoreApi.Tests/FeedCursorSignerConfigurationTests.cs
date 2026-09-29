using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedCursorSignerConfigurationTests
{
    [Fact]
    public void ProductionLikeEnvironmentDoesNotFallbackToSessionSecret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_ENVIRONMENT"] = "production",
                ["AEVO_SESSION_SECRET"] = "session-secret-with-at-least-32-bytes"
            })
            .Build();

        Assert.False(new FeedCursorSigner(configuration).IsConfigured);
    }

    [Fact]
    public void DevelopmentEnvironmentKeepsTheLocalSessionFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_ENVIRONMENT"] = "development",
                ["AEVO_SESSION_SECRET"] = "session-secret-with-at-least-32-bytes"
            })
            .Build();

        Assert.True(new FeedCursorSigner(configuration).IsConfigured);
    }
}
