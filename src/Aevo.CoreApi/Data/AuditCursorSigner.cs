using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevo.CoreApi.Data;

public sealed record AuditCursorPosition(
    DateTimeOffset CreatedAt,
    Guid Id,
    string? ApplicationCode);

/// <summary>
/// Signs short-lived audit-log cursors and binds them to the application
/// filter. The cursor is an ordering anchor, never an authorization grant.
/// </summary>
public sealed class AuditCursorSigner
{
    public const string Version = "audit-cursor-v1";
    private const int MaximumTokenLength = 2048;
    private const int MinimumSecretBytes = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    private readonly byte[] secret;

    public AuditCursorSigner(IConfiguration configuration)
    {
        var configured = configuration["AEVO_AUDIT_CURSOR_SECRET"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured)) configured = configuration["AEVO_SESSION_SECRET"]?.Trim();
        secret = string.IsNullOrWhiteSpace(configured) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(configured);
    }

    public AuditCursorSigner(string secret)
    {
        this.secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool IsConfigured => secret.Length >= MinimumSecretBytes;

    public string Create(AuditCursorPosition position)
    {
        if (!IsConfigured) throw new InvalidOperationException("An audit cursor signing secret is not configured.");

        var payload = new AuditCursorPayload(
            Version,
            position.CreatedAt.ToUnixTimeMilliseconds(),
            position.Id,
            position.ApplicationCode,
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());
        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, SerializerOptions)));
        using var hmac = new HMACSHA256(secret);
        var signaturePart = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadPart)));
        return $"{payloadPart}.{signaturePart}";
    }

    public bool TryVerify(
        string token,
        string? applicationCode,
        DateTimeOffset now,
        out AuditCursorPosition? position)
    {
        position = null;
        if (!IsConfigured || string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength) return false;

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || !TryDecode(parts[0], out var payloadBytes) || !TryDecode(parts[1], out var signatureBytes)) return false;

        using var hmac = new HMACSHA256(secret);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
        if (expectedSignature.Length != signatureBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes)) return false;

        try
        {
            var payload = JsonSerializer.Deserialize<AuditCursorPayload>(payloadBytes, SerializerOptions);
            var normalizedFilter = NormalizeApplication(applicationCode);
            if (payload is null
                || !string.Equals(payload.Version, Version, StringComparison.Ordinal)
                || payload.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds()
                || !string.Equals(NormalizeApplication(payload.ApplicationCode), normalizedFilter, StringComparison.Ordinal)
                || payload.CreatedAtUnixMilliseconds <= 0
                || payload.Id == Guid.Empty)
            {
                return false;
            }

            position = new AuditCursorPosition(
                DateTimeOffset.FromUnixTimeMilliseconds(payload.CreatedAtUnixMilliseconds),
                payload.Id,
                NormalizeApplication(payload.ApplicationCode));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string? NormalizeApplication(string? applicationCode)
    {
        var normalized = applicationCode?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string encoded, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length % 4 == 1) return false;

        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return string.Equals(Base64Url(bytes), encoded, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record AuditCursorPayload(
        string Version,
        long CreatedAtUnixMilliseconds,
        Guid Id,
        string? ApplicationCode,
        long ExpiresAtUnixMilliseconds);
}
