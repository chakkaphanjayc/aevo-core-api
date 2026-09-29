using System.Text.Json;

namespace Aevo.CoreApi.Contracts;

public static class FeedModerationContract
{
    public const string QueueRoute = "/api/v1/admin/go/feed/moderation";
    public const string ActionRoute = "/api/v1/admin/go/feed/moderation/{reportId}/actions";
}

public static class FeedModerationStatuses
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "OPEN",
        "REVIEWING",
        "RESOLVED",
        "DISMISSED"
    };
}

public static class FeedModerationActions
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "REVIEW",
        "LIMIT",
        "REMOVE",
        "RESTORE",
        "DISMISS"
    };
}

public sealed record FeedModerationReport(
    string ReportId,
    string EntityType,
    string EntityId,
    string ReporterId,
    string ReporterName,
    string Reason,
    string Details,
    string ReportStatus,
    JsonElement EvidenceSnapshot,
    JsonElement CurrentSnapshot,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ResolutionCode,
    string ResolutionNote,
    DateTimeOffset CreatedAt);

public sealed record FeedModerationQueueResponse(
    string Status,
    IReadOnlyList<FeedModerationReport> Reports,
    string? NextCursor,
    string? RequestId = null);

public sealed record FeedModerationActionRequest(
    string Action,
    string ExpectedReportStatus,
    string Reason,
    string? IdempotencyKey = null);

public sealed record FeedModerationActionResponse(
    bool Success,
    string ReportId,
    string EntityType,
    string EntityId,
    string Action,
    string ReportStatus,
    string? ContentStatus,
    string AuditId,
    bool Changed,
    string EventId,
    string? CorrelationId = null,
    string? RequestId = null);
