using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace Aevo.CoreApi.Feed;

public sealed record FeedCursorPayload(
    string Version,
    string FeedSessionId,
    string ApplicationCode,
    string PrincipalBinding,
    string Surface,
    string Tab,
    string RequestFingerprint,
    string ConfigVersion,
    string RankingVersion,
    string PolicyVersion,
    string RankingEpoch,
    string ExperimentSeed,
    int PageSize,
    long StartedAtUnixMilliseconds,
    long CandidateCutoffAtUnixMilliseconds,
    long ExpiresAtUnixMilliseconds,
    string? OrderingAnchorHash,
    string? OrderingAnchorKey = null,
    IReadOnlyList<string>? SeenEntityKeys = null);

public static class FeedSeenKeyPolicy
{
    // Keep the signed cursor compact enough for URL transport; the ordering
    // anchor remains the primary cross-page keyset boundary for older keys.
    public const int MaximumKeys = 32;
    public const int MaximumKeyLength = 160;

    public static bool IsValid(IReadOnlyList<string>? keys)
    {
        if (keys is null) return true;
        if (keys.Count > MaximumKeys) return false;

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        return keys.All(key => IsValidKey(key) && distinct.Add(key));
    }

    public static IReadOnlySet<string> Normalize(IEnumerable<string>? keys)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        if (keys is null) return normalized;

        foreach (var key in keys.Take(MaximumKeys))
        {
            if (IsValidKey(key)) normalized.Add(key);
        }

        return normalized;
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength) return false;
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator == key.Length - 1) return false;
        if (key[..separator] is not ("TRACE" or "PLACE")) return false;

        var entityId = key[(separator + 1)..];
        return entityId.Length <= 128
            && entityId.All(character => character >= 0x21 && character <= 0x7e && character is not ':' and not '|');
    }
}

public sealed record FeedCursorVerification(
    bool IsValid,
    FeedCursorPayload? Payload = null);

public sealed record FeedItemTokenPayload(
    string Version,
    string FeedSessionId,
    string PrincipalBinding,
    string EntityType,
    string EntityId,
    int Position,
    long ExpiresAtUnixMilliseconds,
    string? ConfigVersion,
    string? RankingVersion,
    string? ExperimentVariant)
{
    public bool IsLegacy => ExpiresAtUnixMilliseconds <= 0
        || string.IsNullOrWhiteSpace(ConfigVersion)
        || string.IsNullOrWhiteSpace(RankingVersion);
}

public sealed record FeedItemTokenVerification(
    bool IsValid,
    FeedItemTokenPayload? Payload = null,
    bool IsLegacy = false);

public sealed class FeedCursorSigner
{
    public const string Version = "feed-cursor-v1";
    private const int MaximumTokenLength = 4096;
    private const int MinimumSecretBytes = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly byte[] secret;

    public FeedCursorSigner(IConfiguration configuration)
    {
        var configured = configuration["AEVO_FEED_CURSOR_SECRET"]?.Trim();
        if (!IsUsableConfiguredSecret(configured)
            && !RequiresManagedFeedRuntime(configuration["AEVO_ENVIRONMENT"]))
        {
            configured = configuration["AEVO_SESSION_SECRET"]?.Trim();
        }

        if (!IsUsableConfiguredSecret(configured)) configured = null;
        secret = string.IsNullOrWhiteSpace(configured) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(configured);
    }

    public FeedCursorSigner(string secret)
    {
        this.secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool IsConfigured => secret.Length >= MinimumSecretBytes;

    private static bool RequiresManagedFeedRuntime(string? environment)
    {
        var normalized = environment?.Trim().ToLowerInvariant();
        return normalized is "nonprod" or "staging" or "production";
    }

    private static bool IsUsableConfiguredSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("replace-with", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("your-project-ref", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("your-secret", StringComparison.OrdinalIgnoreCase)
        && Encoding.UTF8.GetByteCount(value.Trim()) >= MinimumSecretBytes;

    public string Sign(FeedCursorPayload payload)
    {
        if (!IsConfigured) throw new InvalidOperationException("A Feed cursor signing secret is not configured.");
        ValidatePayload(payload);

        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, SerializerOptions)));
        using var hmac = new HMACSHA256(secret);
        var signaturePart = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadPart)));
        return $"{payloadPart}.{signaturePart}";
    }

    public string CreateItemToken(
        FeedSessionContext context,
        string entityType,
        string entityId,
        int position)
    {
        if (!IsConfigured) throw new InvalidOperationException("A Feed item-token signing secret is not configured.");
        var experimentVariant = FeedExperimentBucketing.SelectVariant(context.RuntimeConfig, context.ExperimentSeed);
        if (entityType is not ("TRACE" or "PLACE")
            || string.IsNullOrWhiteSpace(entityId)
            || entityId.Length > 128
            || entityId.Contains('|', StringComparison.Ordinal)
            || position < 0
            || position > 10_000
            || !IsSafeTokenPart(context.ConfigVersion, 128)
            || !IsSafeTokenPart(context.RankingVersion, 128)
            || experimentVariant is not null && !IsSafeTokenPart(experimentVariant, 64))
        {
            throw new ArgumentException("The Feed item token input is invalid.");
        }

        var payload = string.Join(
            '|',
            context.FeedSessionId,
            context.PrincipalBinding,
            entityType,
            entityId,
            position.ToString(CultureInfo.InvariantCulture),
            context.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            context.ConfigVersion,
            context.RankingVersion,
            experimentVariant ?? string.Empty);
        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(payload));
        using var hmac = new HMACSHA256(secret);
        var signaturePart = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadPart)));
        return $"feed-item-v1.{payloadPart}.{signaturePart}";
    }

    public FeedItemTokenVerification VerifyItemToken(string token, DateTimeOffset now)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
        {
            return new FeedItemTokenVerification(false);
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3
            || !string.Equals(parts[0], "feed-item-v1", StringComparison.Ordinal)
            || !TryDecode(parts[1], out var payloadBytes)
            || !TryDecode(parts[2], out var signatureBytes))
        {
            return new FeedItemTokenVerification(false);
        }

        using var hmac = new HMACSHA256(secret);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[1]));
        if (expectedSignature.Length != signatureBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            return new FeedItemTokenVerification(false);
        }

        var payloadParts = Encoding.UTF8.GetString(payloadBytes).Split('|', StringSplitOptions.None);
        if (payloadParts.Length is not (5 or 9)
            || !Guid.TryParseExact(payloadParts[0], "N", out _)
            || !IsSha256Hex(payloadParts[1])
            || payloadParts[2] is not ("TRACE" or "PLACE")
            || !IsSafeTokenPart(payloadParts[3], 128)
            || !int.TryParse(payloadParts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position)
            || position is < 0 or > 10_000)
        {
            return new FeedItemTokenVerification(false);
        }

        var legacy = payloadParts.Length == 5;
        long expiresAt = 0;
        string? configVersion = null;
        string? rankingVersion = null;
        string? experimentVariant = null;
        if (!legacy)
        {
            if (!long.TryParse(payloadParts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresAt)
                || expiresAt <= 0
                || !IsSafeTokenPart(payloadParts[6], 128)
                || !IsSafeTokenPart(payloadParts[7], 128)
                || !IsSafeTokenPart(payloadParts[8], 64))
            {
                return new FeedItemTokenVerification(false);
            }

            configVersion = payloadParts[6];
            rankingVersion = payloadParts[7];
            experimentVariant = string.IsNullOrEmpty(payloadParts[8]) ? null : payloadParts[8];
            if (expiresAt <= now.ToUnixTimeMilliseconds()) return new FeedItemTokenVerification(false);
        }

        var payload = new FeedItemTokenPayload(
            "feed-item-v1",
            payloadParts[0],
            payloadParts[1],
            payloadParts[2],
            payloadParts[3],
            position,
            expiresAt,
            configVersion,
            rankingVersion,
            experimentVariant);
        return new FeedItemTokenVerification(true, payload, legacy);
    }

    public FeedCursorVerification Verify(string token, DateTimeOffset now)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength) return new FeedCursorVerification(false);

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || !TryDecode(parts[0], out var payloadBytes) || !TryDecode(parts[1], out var signatureBytes))
        {
            return new FeedCursorVerification(false);
        }

        using var hmac = new HMACSHA256(secret);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
        if (expectedSignature.Length != signatureBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            return new FeedCursorVerification(false);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<FeedCursorPayload>(payloadBytes, SerializerOptions);
            if (payload is null || !IsValidPayload(payload) || payload.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
            {
                return new FeedCursorVerification(false);
            }

            return new FeedCursorVerification(true, payload);
        }
        catch (JsonException)
        {
            return new FeedCursorVerification(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new FeedCursorVerification(false);
        }
    }

    private static void ValidatePayload(FeedCursorPayload payload)
    {
        if (!IsValidPayload(payload)) throw new ArgumentException("The Feed cursor payload is invalid.", nameof(payload));
    }

    private static bool IsValidPayload(FeedCursorPayload payload)
    {
        return string.Equals(payload.Version, Version, StringComparison.Ordinal)
            && Guid.TryParseExact(payload.FeedSessionId, "N", out _)
            && string.Equals(payload.ApplicationCode, "GO", StringComparison.Ordinal)
            && IsSha256Hex(payload.PrincipalBinding)
            && string.Equals(payload.Surface, "explore", StringComparison.Ordinal)
            && payload.Tab is "for_you" or "following" or "nearby"
            && IsSha256Hex(payload.RequestFingerprint)
            && payload.ConfigVersion is { Length: >= 1 and <= 128 }
            && payload.RankingVersion is { Length: >= 1 and <= 128 }
            && string.Equals(payload.PolicyVersion, FeedSessionContextFactory.PolicyVersion, StringComparison.Ordinal)
            && payload.RankingEpoch is { Length: >= 1 and <= 64 }
            && IsSha256Hex(payload.ExperimentSeed)
            && payload.PageSize is >= 1 and <= 50
            && payload.StartedAtUnixMilliseconds > 0
            && payload.CandidateCutoffAtUnixMilliseconds >= payload.StartedAtUnixMilliseconds
            && payload.ExpiresAtUnixMilliseconds > payload.CandidateCutoffAtUnixMilliseconds
            && (payload.OrderingAnchorHash is null || IsSha256Hex(payload.OrderingAnchorHash))
            && (payload.OrderingAnchorKey is null || payload.OrderingAnchorKey.Length is >= 1 and <= 512)
            && (payload.OrderingAnchorKey is null
                || string.Equals(
                    FeedPrincipalFactory.Hash(payload.OrderingAnchorKey),
                    payload.OrderingAnchorHash,
                    StringComparison.Ordinal))
            && FeedSeenKeyPolicy.IsValid(payload.SeenEntityKeys);
    }

    private static bool IsSha256Hex(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsSafeTokenPart(string? value, int maximumLength)
    {
        return value is not null
            && value.Length <= maximumLength
            && value.All(character => character >= 0x21 && character <= 0x7e && character != '|');
    }

    private static bool TryDecode(string encoded, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length % 4 == 1) return false;

        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return bytes.Length > 0 && string.Equals(Base64Url(bytes), encoded, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
