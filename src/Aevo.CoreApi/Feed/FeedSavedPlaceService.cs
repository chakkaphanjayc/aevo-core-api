using System.Security.Cryptography;
using System.Text;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed class FeedSavedPlaceService(CoreDataStore database)
{
    public async Task<FeedSavedPlaceResponseContract> SetAsync(
        FeedPrincipal principal,
        Guid placeId,
        bool saved,
        string idempotencyKey,
        string requestId,
        CancellationToken cancellationToken)
    {
        RequireAuthenticated(principal);
        if (placeId == Guid.Empty)
        {
            throw Invalid("PLACE_ID_INVALID", "A canonical Place ID is required.");
        }

        var normalizedIdempotencyKey = idempotencyKey.Trim();
        if (!IsSafeIdempotencyKey(normalizedIdempotencyKey))
        {
            throw Invalid("IDEMPOTENCY_KEY_INVALID", "A valid canonical Place save idempotency key is required.");
        }

        var requestHash = Hash(string.Join(
            '|',
            principal.CursorBinding,
            placeId.ToString("N"),
            saved ? "save" : "unsave"));
        var result = await database.SetFeedSavedPlaceAsync(
            principal.CursorBinding,
            principal.UserId!.Value,
            placeId,
            saved,
            normalizedIdempotencyKey,
            requestHash,
            requestId,
            cancellationToken);

        return new FeedSavedPlaceResponseContract(
            result.PlaceId,
            result.Saved,
            result.Changed,
            result.UpdatedAt,
            result.RequestId);
    }

    public async Task<FeedSavedPlacesResponseContract> ListAsync(
        FeedPrincipal principal,
        string requestId,
        CancellationToken cancellationToken)
    {
        RequireAuthenticated(principal);
        var records = await database.ListFeedSavedPlacesAsync(
            principal.CursorBinding,
            principal.UserId!.Value,
            cancellationToken);
        return new FeedSavedPlacesResponseContract(
            records.Select(record => new FeedSavedPlaceEntryContract(record.PlaceId, record.SavedAt)).ToArray(),
            requestId);
    }

    private static void RequireAuthenticated(FeedPrincipal principal)
    {
        if (!principal.IsAuthenticated || principal.UserId is null)
        {
            throw new FeedSavedPlaceRequestException(
                "AUTHENTICATION_REQUIRED",
                "An authenticated Go session is required to persist canonical Place saves.",
                401);
        }
    }

    private static FeedSavedPlaceRequestException Invalid(string code, string message) =>
        new(code, message, 400);

    private static bool IsSafeIdempotencyKey(string value) =>
        value.Length is >= 8 and <= 200
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class FeedSavedPlaceRequestException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
