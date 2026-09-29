using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevo.CoreApi.Contracts;

public static class ApplicationCodes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "HUB", "ADMIN", "GO", "PLAY", "POS", "KIOSK", "QUEUE", "DIGITAL_SIGN"
    };
}

public sealed record HealthResponse(
    string Status,
    string Service,
    string Version,
    string Environment,
    string RequestId);

public sealed record ApiError(
    string Code,
    string Message,
    string RequestId,
    object? Details = null);

public sealed record ApiErrorResponse(ApiError Error);

public sealed record AuthenticatedUser(
    string Id,
    string Email,
    string? DisplayName);

public sealed record ApplicationSessionSummary(
    string AppCode,
    string SessionId,
    DateTimeOffset ExpiresAt,
    string? OrganizationId,
    string? StoreId);

public sealed record MeResponse(
    AuthenticatedUser User,
    ApplicationSessionSummary Session);

public sealed record PasswordLoginRequest(
    string Email,
    string Password,
    string? Application = null,
    bool RememberMe = false);

public sealed record InternalSessionIssueRequest(
    Guid UserId,
    string Email,
    string? DisplayName,
    string Application,
    Guid? OrganizationId = null,
    Guid? StoreId = null,
    int? LifetimeSeconds = null,
    bool RememberMe = false);

public sealed record InternalSessionRevokeRequest(string SessionToken, string Application);

public sealed record InternalSessionResolveRequest(
    string SessionToken,
    string Application,
    Guid? OrganizationId = null,
    Guid? StoreId = null,
    string? CsrfToken = null);

public sealed record InternalSessionRefreshRequest(
    string SessionToken,
    string Application,
    string CsrfToken);

public sealed record InternalSyncManifestRequest(
    string SessionToken,
    string Application);

public sealed record InternalHubSubscriptionsRequest(
    string SessionToken,
    string Application,
    Guid? OrganizationId = null,
    Guid? StoreId = null);

public sealed record InternalBillingWebhookRequest(
    string Provider,
    string EventId,
    string EventType,
    JsonElement Payload);

public sealed record InternalEventDeliveryRequest(
    string EventId,
    string EventType,
    string SchemaVersion = "v1",
    string? OrganizationId = null,
    string? StoreId = null);

public sealed record InternalHandoffExchangeRequest(
    string Code,
    string Application,
    string? State = null,
    string? CodeVerifier = null);

public sealed record AccessResponse(
    bool Allowed,
    string Reason,
    string AppCode,
    IReadOnlyList<string> Permissions);

public sealed record ConnectionSummary(
    string AppCode,
    string Label,
    string Status,
    string? BaseUrl,
    DateTimeOffset? CheckedAt,
    int? LatencyMs,
    string? LastErrorCode,
    JsonElement? Metadata = null);

public sealed record AuditLogRecord(
    string Id,
    string ActorId,
    string? ActorEmail,
    string? PlatformRole,
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    IReadOnlyDictionary<string, object?>? BeforeState,
    IReadOnlyDictionary<string, object?>? AfterState,
    string? RequestId,
    DateTimeOffset CreatedAt);

public sealed record PageResponse<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record GoSettingsUpdateRequest(JsonElement Settings, string Reason);

public sealed record GoFeatureFlagUpdateRequest(bool Enabled, int RolloutPercent, string Reason);

public sealed record ApplicationStatusUpdateRequest(string Status, string Reason);

public sealed record ConnectionProbeUpdate(
    string AppCode,
    string Environment,
    string Status,
    string? BaseUrl,
    string HealthPath,
    DateTimeOffset? CheckedAt,
    int? LatencyMs,
    string? LastErrorCode,
    JsonElement? Metadata = null);

public sealed record MigrationAuthoritySummary(
    string Schema,
    string Table,
    string SemanticDomain,
    IReadOnlyList<string> HistoricalMigrationRepos,
    string CurrentOwner,
    string TargetOwner,
    IReadOnlyList<string> RuntimeWriters,
    IReadOnlyList<string> Readers,
    string CurrentWriteMode,
    string MigrationPhase,
    string TransitionState,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset UpdatedAt,
    string? DeleteAfter);
