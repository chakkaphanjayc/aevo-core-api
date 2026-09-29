using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevo.CoreApi.Security;

public sealed record ApplicationHandshakeDecision(
    string Status,
    string Reason,
    string? AppCode,
    string? Protocol,
    string? ProtocolVersion,
    string? ContractVersion,
    string? Environment,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset CheckedAt)
{
    public bool Compatible => Status == ApplicationHandshakeStatus.Compatible;
}

public static class ApplicationHandshakeStatus
{
    public const string Compatible = "COMPATIBLE";
    public const string Incompatible = "INCOMPATIBLE";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string NotConfigured = "NOT_CONFIGURED";
    public const string Stale = "STALE";
    public const string Timeout = "TIMEOUT";
    public const string Offline = "OFFLINE";
    public const string HealthOnly = "HEALTH_ONLY";
}

/// <summary>
/// Versioned, tenant-free app compatibility handshake. The request is signed
/// by Core and the app verifies it with the app/service secret. Health remains
/// a liveness signal only and never becomes a compatibility decision.
/// </summary>
public static class ApplicationHandshake
{
    public const string Path = "/.well-known/aevo-handshake";
    public const string Protocol = "aevo.application-handshake";
    public const string ProtocolVersion = "1";
    public const int MaxClockSkewSeconds = 300;
    public const int MaxResponseBytes = 32 * 1024;

    public static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string CreateSignature(
        string secret,
        string timestamp,
        string method,
        string path,
        string nonce,
        string appCode)
    {
        var payload = string.Join("\n", timestamp, method.ToUpperInvariant(), path, nonce, appCode.ToUpperInvariant());
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool IsFresh(string timestamp, DateTimeOffset now)
        => long.TryParse(timestamp, out var unixSeconds)
            && Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) <= MaxClockSkewSeconds;

    public static ApplicationHandshakeDecision Evaluate(
        string expectedAppCode,
        JsonElement payload,
        DateTimeOffset now)
    {
        var normalizedAppCode = expectedAppCode.Trim().ToUpperInvariant();
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Decision(ApplicationHandshakeStatus.Incompatible, "HANDSHAKE_INVALID_RESPONSE", normalizedAppCode, now);
        }

        var protocol = StringProperty(payload, "protocol");
        var protocolVersion = StringProperty(payload, "protocolVersion");
        var appCode = StringProperty(payload, "appCode")?.Trim().ToUpperInvariant();
        var contractVersion = StringProperty(payload, "contractVersion");
        var environment = StringProperty(payload, "environment");
        var status = StringProperty(payload, "status")?.Trim().ToLowerInvariant();
        if (!DateTimeOffset.TryParse(StringProperty(payload, "checkedAt"), out var checkedAt)
            || !HasValidCapabilities(payload))
        {
            return Decision(ApplicationHandshakeStatus.Incompatible, "HANDSHAKE_INVALID_RESPONSE", normalizedAppCode, now);
        }
        var capabilities = StringArray(payload, "capabilities");

        if (!string.Equals(protocol, Protocol, StringComparison.Ordinal)
            || !string.Equals(protocolVersion, ProtocolVersion, StringComparison.Ordinal)
            || !string.Equals(appCode, normalizedAppCode, StringComparison.Ordinal)
            || !string.Equals(contractVersion, "v1", StringComparison.Ordinal))
        {
            return new ApplicationHandshakeDecision(
                ApplicationHandshakeStatus.Incompatible,
                "HANDSHAKE_CONTRACT_MISMATCH",
                appCode,
                protocol,
                protocolVersion,
                contractVersion,
                environment,
                capabilities,
                checkedAt);
        }

        if (checkedAt < now.AddSeconds(-MaxClockSkewSeconds))
        {
            return new ApplicationHandshakeDecision(
                ApplicationHandshakeStatus.Stale,
                "HANDSHAKE_STALE",
                appCode,
                protocol,
                protocolVersion,
                contractVersion,
                environment,
                capabilities,
                checkedAt);
        }

        if (status is not ("ready" or "healthy"))
        {
            return new ApplicationHandshakeDecision(
                ApplicationHandshakeStatus.Incompatible,
                "HANDSHAKE_APP_NOT_READY",
                appCode,
                protocol,
                protocolVersion,
                contractVersion,
                environment,
                capabilities,
                checkedAt);
        }

        return new ApplicationHandshakeDecision(
            ApplicationHandshakeStatus.Compatible,
            "HANDSHAKE_COMPATIBLE",
            appCode,
            protocol,
            protocolVersion,
            contractVersion,
            environment,
            capabilities,
            checkedAt);
    }

    private static ApplicationHandshakeDecision Decision(
        string status,
        string reason,
        string appCode,
        DateTimeOffset now)
        => new(status, reason, appCode, null, null, null, null, Array.Empty<string>(), now);

    private static string? StringProperty(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string[] StringArray(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return property.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Where(value => value.Length is > 0 and <= 64)
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
    }

    private static bool HasValidCapabilities(JsonElement payload)
    {
        if (!payload.TryGetProperty("capabilities", out var property) || property.ValueKind != JsonValueKind.Array) return false;
        var values = property.EnumerateArray().ToArray();
        return values.Length <= 64
            && values.All(value => value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 64 });
    }
}
