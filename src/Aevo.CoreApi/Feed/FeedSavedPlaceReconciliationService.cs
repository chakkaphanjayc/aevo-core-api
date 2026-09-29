using System.Security.Cryptography;
using System.Text;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed class FeedSavedPlaceReconciliationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class FeedSavedPlaceReconciliationService(CoreDataStore database)
{
    public FeedSavedPlaceReconciliationRequestContract Normalize(
        FeedSavedPlaceReconciliationRequestContract request)
    {
        var mode = string.IsNullOrWhiteSpace(request.Mode)
            ? "DRY_RUN"
            : request.Mode.Trim().ToUpperInvariant();
        if (!FeedSavedPlaceReconciliationContract.Modes.Contains(mode))
        {
            throw Invalid("LEGACY_RECONCILIATION_MODE_INVALID", "Mode must be DRY_RUN, MIGRATE, or MIGRATE_AND_DELETE.");
        }
        if (request.CustomerId == Guid.Empty || request.StoreId == Guid.Empty)
        {
            throw Invalid("LEGACY_RECONCILIATION_SCOPE_INVALID", "CustomerId and StoreId must be valid UUIDs when provided.");
        }
        var limit = request.Limit ?? FeedSavedPlaceReconciliationContract.DefaultLimit;
        if (limit is < 1 or > FeedSavedPlaceReconciliationContract.MaxLimit)
        {
            throw Invalid("LEGACY_RECONCILIATION_LIMIT_INVALID", $"Limit must be between 1 and {FeedSavedPlaceReconciliationContract.MaxLimit}.");
        }
        var idempotencyKey = request.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Length is < 8 or > 200
            || idempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
        {
            throw Invalid("LEGACY_RECONCILIATION_IDEMPOTENCY_INVALID", "A safe idempotency key is required.");
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "Controlled customer favorite reconciliation"
            : request.Reason.Trim();
        if (reason.Length is < 1 or > 240)
        {
            throw Invalid("LEGACY_RECONCILIATION_REASON_INVALID", "Reason must be between 1 and 240 characters.");
        }
        if (mode == "MIGRATE_AND_DELETE" && !request.ConfirmLegacyDelete)
        {
            throw Invalid("LEGACY_RECONCILIATION_DELETE_CONFIRMATION_REQUIRED", "MIGRATE_AND_DELETE requires confirmLegacyDelete=true.");
        }

        return request with
        {
            Mode = mode,
            Limit = limit,
            IdempotencyKey = idempotencyKey,
            Reason = reason
        };
    }

    public Task<FeedSavedPlaceReconciliationResponseContract> RunAsync(
        CoreSession adminSession,
        FeedSavedPlaceReconciliationRequestContract request,
        string requestId,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        var requestHash = Hash(string.Join(
            '|',
            normalized.Mode,
            normalized.CustomerId?.ToString("N") ?? string.Empty,
            normalized.StoreId?.ToString("N") ?? string.Empty,
            normalized.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            normalized.ConfirmLegacyDelete ? "delete-confirmed" : "no-delete",
            normalized.IdempotencyKey,
            normalized.Reason));
        return database.ReconcileLegacyCustomerFavoritesAsync(
            adminSession.UserId,
            adminSession.PlatformRole,
            normalized,
            requestHash,
            requestId,
            cancellationToken);
    }

    public static bool IsMutation(string? mode) =>
        string.Equals(mode, "MIGRATE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, "MIGRATE_AND_DELETE", StringComparison.OrdinalIgnoreCase);

    public static bool IsDestructive(string? mode) =>
        string.Equals(mode, "MIGRATE_AND_DELETE", StringComparison.OrdinalIgnoreCase);

    private static FeedSavedPlaceReconciliationException Invalid(string code, string message) =>
        new(code, message, 400);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
