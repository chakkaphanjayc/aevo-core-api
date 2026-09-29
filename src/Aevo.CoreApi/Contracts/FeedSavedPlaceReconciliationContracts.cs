namespace Aevo.CoreApi.Contracts;

public static class FeedSavedPlaceReconciliationContract
{
    public const string Route = "/api/v1/admin/go/feed/saved-places/reconciliation";
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal)
    {
        "DRY_RUN",
        "MIGRATE",
        "MIGRATE_AND_DELETE"
    };
}

public sealed record FeedSavedPlaceReconciliationRequestContract(
    string? Mode = null,
    Guid? CustomerId = null,
    Guid? StoreId = null,
    int? Limit = null,
    bool ConfirmLegacyDelete = false,
    string? IdempotencyKey = null,
    string? Reason = null);

public sealed record FeedSavedPlaceReconciliationResponseContract(
    Guid RunId,
    string Mode,
    string Status,
    Guid? CustomerId,
    Guid? StoreId,
    int RequestedLimit,
    int Scanned,
    int Mapped,
    int Unmapped,
    int Ambiguous,
    int IdentityMissing,
    int NotPublic,
    int AlreadySaved,
    int Inserted,
    int DeletedLegacy,
    bool Truncated,
    string Reason,
    string? RequestId = null);
