using System.Text.Json;
using Aevo.CoreApi.Security;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class ApplicationHandshakeTests
{
    private static readonly string[] Capabilities = ["catalog", "orders"];

    [Fact]
    public void CreatesASignatureThatChangesWithAppAndPath()
    {
        var first = ApplicationHandshake.CreateSignature("secret", "100", "GET", ApplicationHandshake.Path, "nonce", "POS");
        var second = ApplicationHandshake.CreateSignature("secret", "100", "GET", ApplicationHandshake.Path, "nonce", "PLAY");

        Assert.NotEqual(first, second);
        Assert.Equal(first, ApplicationHandshake.CreateSignature("secret", "100", "GET", ApplicationHandshake.Path, "nonce", "POS"));
    }

    [Fact]
    public void AcceptsACurrentMatchingHandshake()
    {
        var now = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            protocol = ApplicationHandshake.Protocol,
            protocolVersion = ApplicationHandshake.ProtocolVersion,
            appCode = "POS",
            contractVersion = "v1",
            environment = "development",
            status = "ready",
            capabilities = Capabilities,
            checkedAt = now
        }));

        var decision = ApplicationHandshake.Evaluate("POS", document.RootElement, now);

        Assert.True(decision.Compatible);
        Assert.Equal("HANDSHAKE_COMPATIBLE", decision.Reason);
        Assert.Equal(Capabilities, decision.Capabilities);
    }

    [Fact]
    public void RejectsWrongContractAndStaleResponses()
    {
        var now = DateTimeOffset.UtcNow;
        using var incompatible = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            protocol = ApplicationHandshake.Protocol,
            protocolVersion = "99",
            appCode = "POS",
            contractVersion = "v1",
            status = "ready",
            capabilities = Array.Empty<string>(),
            checkedAt = now
        }));
        using var stale = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            protocol = ApplicationHandshake.Protocol,
            protocolVersion = ApplicationHandshake.ProtocolVersion,
            appCode = "POS",
            contractVersion = "v1",
            status = "ready",
            capabilities = Array.Empty<string>(),
            checkedAt = now.AddMinutes(-10)
        }));

        Assert.Equal("HANDSHAKE_CONTRACT_MISMATCH", ApplicationHandshake.Evaluate("POS", incompatible.RootElement, now).Reason);
        Assert.Equal(ApplicationHandshakeStatus.Stale, ApplicationHandshake.Evaluate("POS", stale.RootElement, now).Status);
    }
}
