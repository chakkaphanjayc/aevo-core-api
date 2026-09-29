using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed record PlaceCursorAnchor(
    string Surface,
    string RequestFingerprint,
    string SortKey,
    double? DistanceMeters,
    Guid PlaceId);

public sealed record PlaceCursorVerification(
    bool IsValid,
    PlaceCursorAnchor? Anchor = null);

public sealed class InvalidPlaceCursorException(string message) : Exception(message);

/// <summary>
/// Signs public Place cursors so a client cannot alter the request scope or
/// ordering anchor. Cursors are intentionally short-lived and bound to the
/// exact query shape that produced them.
/// </summary>
public sealed class PlaceCursorSigner
{
    public const string Version = "place-cursor-v1";
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

    public PlaceCursorSigner(IConfiguration configuration)
    {
        var configured = configuration["AEVO_PLACE_CURSOR_SECRET"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured)) configured = configuration["AEVO_SESSION_SECRET"]?.Trim();
        secret = string.IsNullOrWhiteSpace(configured) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(configured);
    }

    public PlaceCursorSigner(string secret)
    {
        this.secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool IsConfigured => secret.Length >= MinimumSecretBytes;

    internal string CreateSearchCursor(PlaceSearchRequestContract request, PlaceProjectionRow row, string? principalBinding = null)
    {
        var anchor = new PlaceCursorAnchor(
            "search",
            Fingerprint(request, principalBinding),
            row.SortKey,
            null,
            row.PlaceId);
        return Sign(anchor);
    }

    internal string CreateNearbyCursor(PlaceNearbyRequestContract request, PlaceProjectionRow row, double distanceMeters)
    {
        var anchor = new PlaceCursorAnchor(
            "nearby",
            Fingerprint(request),
            string.Empty,
            distanceMeters,
            row.PlaceId);
        return Sign(anchor);
    }

    public PlaceCursorVerification Verify(string token, DateTimeOffset now)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
        {
            return new PlaceCursorVerification(false);
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || !TryDecode(parts[0], out var payloadBytes) || !TryDecode(parts[1], out var signatureBytes))
        {
            return new PlaceCursorVerification(false);
        }

        using var hmac = new HMACSHA256(secret);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
        if (expectedSignature.Length != signatureBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            return new PlaceCursorVerification(false);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PlaceCursorPayload>(payloadBytes, SerializerOptions);
            if (payload is null
                || !IsValidPayload(payload)
                || payload.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
            {
                return new PlaceCursorVerification(false);
            }

            return new PlaceCursorVerification(
                true,
                new PlaceCursorAnchor(
                    payload.Surface,
                    payload.RequestFingerprint,
                    payload.SortKey,
                    payload.DistanceMeters,
                    Guid.Parse(payload.PlaceId)));
        }
        catch (JsonException)
        {
            return new PlaceCursorVerification(false);
        }
        catch (FormatException)
        {
            return new PlaceCursorVerification(false);
        }
    }

    public static string Fingerprint(PlaceSearchRequestContract request, string? principalBinding = null)
    {
        var canonical = string.Join(
            "|",
            "search",
            request.Query?.Trim() ?? string.Empty,
            request.Area?.Trim() ?? string.Empty,
            Bounds(request.Bounds),
            Categories(request.CategoryIds),
            request.Sort?.Trim() ?? "relevance",
            (request.Limit ?? PlaceApiContract.DefaultListLimit).ToString(CultureInfo.InvariantCulture),
            request.SavedOnly ? "saved-only" : "all-places",
            principalBinding?.Trim() ?? string.Empty);
        return Hash(canonical);
    }

    public static string Fingerprint(PlaceNearbyRequestContract request)
    {
        var canonical = string.Join(
            "|",
            "nearby",
            request.Origin.Longitude.ToString("R", CultureInfo.InvariantCulture),
            request.Origin.Latitude.ToString("R", CultureInfo.InvariantCulture),
            request.RadiusMeters.ToString(CultureInfo.InvariantCulture),
            Categories(request.CategoryIds),
            (request.Limit ?? PlaceApiContract.DefaultListLimit).ToString(CultureInfo.InvariantCulture));
        return Hash(canonical);
    }

    public string Sign(PlaceCursorAnchor anchor)
    {
        if (!IsConfigured) throw new InvalidOperationException("A Place cursor signing secret is not configured.");

        var payload = new PlaceCursorPayload(
            Version,
            anchor.Surface,
            anchor.RequestFingerprint,
            anchor.SortKey,
            anchor.DistanceMeters,
            anchor.PlaceId.ToString("D"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
        ValidatePayload(payload);

        var payloadPart = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, SerializerOptions)));
        using var hmac = new HMACSHA256(secret);
        var signaturePart = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadPart)));
        return $"{payloadPart}.{signaturePart}";
    }

    private static void ValidatePayload(PlaceCursorPayload payload)
    {
        if (!IsValidPayload(payload)) throw new ArgumentException("The Place cursor payload is invalid.", nameof(payload));
    }

    private static bool IsValidPayload(PlaceCursorPayload payload)
    {
        return string.Equals(payload.Version, Version, StringComparison.Ordinal)
            && payload.Surface is "search" or "nearby"
            && IsSha256Hex(payload.RequestFingerprint)
            && payload.SortKey.Length <= 500
            && Guid.TryParse(payload.PlaceId, out _)
            && payload.ExpiresAtUnixMilliseconds > 0
            && (payload.Surface == "search"
                ? payload.DistanceMeters is null && payload.SortKey.Length > 0
                : payload.DistanceMeters is >= 0 and <= 20_100_000 && payload.SortKey.Length == 0);
    }

    private static string Bounds(PlaceBoundsContract? bounds) => bounds is null
        ? string.Empty
        : string.Join(
            ",",
            bounds.West.ToString("R", CultureInfo.InvariantCulture),
            bounds.South.ToString("R", CultureInfo.InvariantCulture),
            bounds.East.ToString("R", CultureInfo.InvariantCulture),
            bounds.North.ToString("R", CultureInfo.InvariantCulture));

    private static string Categories(IReadOnlyList<string>? categories) =>
        categories is null ? string.Empty : string.Join(",", categories.Order(StringComparer.Ordinal));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSha256Hex(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private sealed record PlaceCursorPayload(
        string Version,
        string Surface,
        string RequestFingerprint,
        string SortKey,
        double? DistanceMeters,
        string PlaceId,
        long ExpiresAtUnixMilliseconds);
}
