using System.Security.Cryptography;
using System.Text;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed class FeedNegativeFeedbackService(
    CoreDataStore database,
    FeedCursorSigner cursorSigner)
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.Ordinal)
    {
        "HIDE",
        "UNHIDE"
    };

    private static readonly HashSet<string> SupportedReasons = new(StringComparer.Ordinal)
    {
        "NOT_RELEVANT",
        "ALREADY_SEEN",
        "TOO_FAR",
        "OTHER"
    };

    public async Task<FeedNegativeFeedbackResponseContract> ApplyAsync(
        FeedPrincipal principal,
        FeedNegativeFeedbackRequestContract request,
        string idempotencyKey,
        string requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!principal.IsAuthenticated || principal.UserId is null)
        {
            throw new FeedNegativeFeedbackRequestException(
                "AUTHENTICATION_REQUIRED",
                "An authenticated Go session is required to persist Feed feedback.",
                401);
        }

        if (!string.Equals(request.SchemaVersion, FeedApiContract.SchemaVersion, StringComparison.Ordinal))
        {
            throw Invalid("FEEDBACK_SCHEMA_UNSUPPORTED", "The Feed feedback schema is unsupported.");
        }

        if (!IsSafeIdempotencyKey(idempotencyKey))
        {
            throw Invalid("IDEMPOTENCY_KEY_INVALID", "A valid Feed feedback idempotency key is required.");
        }

        if (!Guid.TryParseExact(request.FeedSessionId, "N", out var feedSessionId))
        {
            throw Invalid("FEED_SESSION_INVALID", "The Feed session is invalid.");
        }

        var action = request.Action.Trim().ToUpperInvariant();
        if (!SupportedActions.Contains(action))
        {
            throw Invalid("FEEDBACK_ACTION_UNSUPPORTED", "The Feed feedback action is unsupported.");
        }

        var reasonCode = string.IsNullOrWhiteSpace(request.ReasonCode)
            ? null
            : request.ReasonCode.Trim().ToUpperInvariant();
        if (reasonCode is not null
            && (reasonCode.Length > FeedApiContract.MaxFeedbackReasonLength
                || !SupportedReasons.Contains(reasonCode)))
        {
            throw Invalid("FEEDBACK_REASON_INVALID", "The Feed feedback reason is invalid.");
        }

        var active = action == "HIDE";
        if (!active && reasonCode is not null)
        {
            throw Invalid("FEEDBACK_REASON_NOT_ALLOWED", "A reason is only accepted when hiding a Feed item.");
        }

        if (string.IsNullOrWhiteSpace(request.ItemToken)
            || request.ItemToken.Length > FeedApiContract.MaxEventItemTokenLength)
        {
            throw Invalid("ITEM_TOKEN_INVALID", "The Feed item token is invalid.");
        }

        var verification = cursorSigner.VerifyItemToken(request.ItemToken, now);
        if (!verification.IsValid || verification.Payload is null)
        {
            throw Invalid("ITEM_TOKEN_INVALID", "The Feed item token is invalid or expired.");
        }

        if (verification.IsLegacy || verification.Payload.IsLegacy)
        {
            throw Invalid("ITEM_TOKEN_VERSION_UNSUPPORTED", "The Feed item token version is unsupported.");
        }

        var token = verification.Payload;
        if (!string.Equals(token.FeedSessionId, feedSessionId.ToString("N"), StringComparison.Ordinal))
        {
            throw Invalid("FEED_SESSION_INVALID", "The Feed session does not match the item token.");
        }

        if (!string.Equals(token.PrincipalBinding, principal.CursorBinding, StringComparison.Ordinal))
        {
            throw Invalid("ITEM_TOKEN_PRINCIPAL_MISMATCH", "The Feed item token is not bound to this session.");
        }

        var itemTokenHash = Hash(request.ItemToken);
        var requestHash = Hash(string.Join(
            '|',
            principal.CursorBinding,
            token.FeedSessionId,
            token.EntityType,
            token.EntityId,
            itemTokenHash,
            action,
            reasonCode ?? string.Empty));

        var result = await database.UpsertFeedNegativeFeedbackAsync(
            principal.CursorBinding,
            principal.UserId.Value,
            feedSessionId,
            token.EntityType,
            token.EntityId,
            itemTokenHash,
            "HIDE",
            reasonCode,
            active,
            idempotencyKey.Trim(),
            requestHash,
            requestId,
            cancellationToken);

        return new FeedNegativeFeedbackResponseContract(
            result.ItemType,
            result.ItemId,
            result.Action,
            result.Active,
            result.UpdatedAt,
            result.RequestId);
    }

    public async Task<FeedNegativeFeedbackHistoryResponseContract> ListHistoryAsync(
        FeedPrincipal principal,
        int limit,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!principal.IsAuthenticated || principal.UserId is null)
        {
            throw new FeedNegativeFeedbackRequestException(
                "AUTHENTICATION_REQUIRED",
                "An authenticated Go session is required to read Feed feedback history.",
                401);
        }

        if (limit is < 1 or > FeedApiContract.MaxFeedbackHistoryLimit)
        {
            throw Invalid(
                "FEEDBACK_HISTORY_LIMIT_INVALID",
                $"Feed feedback history limit must be between 1 and {FeedApiContract.MaxFeedbackHistoryLimit}.");
        }

        var records = await database.ListFeedNegativeFeedbackHistoryAsync(
            principal.CursorBinding,
            principal.UserId.Value,
            limit,
            cancellationToken);

        return new FeedNegativeFeedbackHistoryResponseContract(
            records.Select(record => new FeedNegativeFeedbackHistoryEntryContract(
                record.ItemType,
                record.ItemId,
                record.Action,
                record.ReasonCode,
                record.Active,
                record.CreatedAt)).ToArray(),
            requestId);
    }

    private static FeedNegativeFeedbackRequestException Invalid(string code, string message) =>
        new(code, message, 400);

    private static bool IsSafeIdempotencyKey(string value) =>
        value.Trim() is { Length: >= 8 and <= 200 } normalized
        && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class FeedNegativeFeedbackRequestException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
