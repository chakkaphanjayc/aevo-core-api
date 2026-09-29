using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed class FeedModerationRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class FeedModerationCursor
{
    public static string Encode(string status, FeedModerationReport report)
    {
        var payload = string.Join(
            "|",
            status,
            report.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            report.ReportId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecode(string? value, string expectedStatus, out DateTimeOffset createdAt, out Guid reportId)
    {
        createdAt = default;
        reportId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('|');
            return payload.Length == 3
                && string.Equals(payload[0], expectedStatus, StringComparison.Ordinal)
                && DateTimeOffset.TryParse(payload[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt)
                && Guid.TryParse(payload[2], out reportId)
                && reportId != Guid.Empty;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed partial class CoreDataStore
{
    public async Task<FeedModerationQueueResponse> ListFeedModerationQueueAsync(
        string status,
        int limit,
        DateTimeOffset? beforeCreatedAt,
        Guid? beforeReportId,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = status.Trim().ToUpperInvariant();
        if (!FeedModerationStatuses.All.Contains(normalizedStatus))
        {
            throw new FeedModerationRequestException("MODERATION_STATUS_INVALID", "Feed moderation status is invalid.");
        }

        var pageSize = Math.Clamp(limit, 1, 50);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select report_id, entity_type, entity_id, reporter_id, reporter_name,
                   reason, details, report_status, evidence_snapshot, current_snapshot,
                   reviewed_by, reviewed_at, resolution_code, resolution_note, created_at
            from public.tracedee_list_moderation_queue(
              @status,
              @limit,
              @before_created_at,
              @before_report_id)
            """,
            connection)
        {
            CommandTimeout = 3
        };
        command.Parameters.AddWithValue("status", normalizedStatus);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        command.Parameters.Add(new NpgsqlParameter("before_created_at", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)beforeCreatedAt ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("before_report_id", NpgsqlDbType.Uuid)
        {
            Value = (object?)beforeReportId ?? DBNull.Value
        });

        var reports = new List<FeedModerationReport>(pageSize + 1);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reports.Add(new FeedModerationReport(
                    reader.GetGuid(0).ToString("D"),
                    reader.GetString(1),
                    reader.GetGuid(2).ToString("D"),
                    reader.GetGuid(3).ToString("D"),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    JsonValue(reader, 8) ?? JsonSerializer.SerializeToElement(new { }),
                    JsonValue(reader, 9) ?? JsonSerializer.SerializeToElement(new { }),
                    NullableGuid(reader, 10)?.ToString("D"),
                    NullableDateTimeOffset(reader, 11),
                    NullableString(reader, 12),
                    reader.GetString(13),
                    reader.GetFieldValue<DateTimeOffset>(14)));
            }

            var hasMore = reports.Count > pageSize;
            if (hasMore) reports.RemoveAt(reports.Count - 1);
            var nextCursor = hasMore && reports.Count > 0
                ? FeedModerationCursor.Encode(normalizedStatus, reports[^1])
                : null;
            return new FeedModerationQueueResponse(normalizedStatus, reports, nextCursor, requestId);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Feed moderation queue is unavailable.", error);
        }
    }

    public async Task<FeedModerationActionResponse> ApplyFeedModerationActionAsync(
        Guid actorId,
        Guid sessionId,
        Guid reportId,
        FeedModerationActionRequest request,
        string idempotencyKey,
        string requestId,
        CancellationToken cancellationToken)
    {
        var action = request.Action.Trim().ToUpperInvariant();
        var expectedStatus = request.ExpectedReportStatus.Trim().ToUpperInvariant();
        var reason = request.Reason.Trim();
        if (reportId == Guid.Empty
            || !FeedModerationActions.All.Contains(action)
            || !FeedModerationStatuses.All.Contains(expectedStatus)
            || reason.Length is < 3 or > 1000
            || idempotencyKey.Trim().Length is < 8 or > 128)
        {
            throw new FeedModerationRequestException("MODERATION_INPUT_INVALID", "Feed moderation action input is invalid.");
        }

        var requestHash = RequestHash(reportId, action, expectedStatus, reason);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, @expected_status, @action, @reason, @idempotency_key, @request_hash, 'aevo-admin', @session_id)",
            connection)
        {
            CommandTimeout = 5
        };
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("report_id", reportId);
        command.Parameters.AddWithValue("expected_status", expectedStatus);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Trim());
        command.Parameters.AddWithValue("request_hash", requestHash);
        command.Parameters.AddWithValue("session_id", sessionId.ToString("D"));

        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var payload = JsonDocument.Parse(Convert.ToString(result, CultureInfo.InvariantCulture) ?? "{}").RootElement;
            return new FeedModerationActionResponse(
                payload.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                RequiredString(payload, "reportId"),
                RequiredString(payload, "entityType"),
                RequiredString(payload, "entityId"),
                RequiredString(payload, "action"),
                RequiredString(payload, "reportStatus"),
                NullableString(payload, "contentStatus"),
                RequiredString(payload, "auditId"),
                payload.TryGetProperty("changed", out var changed) && changed.GetBoolean(),
                RequiredString(payload, "eventId"),
                NullableString(payload, "correlationId"),
                requestId);
        }
        catch (PostgresException error) when (error.SqlState == "P0001")
        {
            throw ModerationException(error.MessageText);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Feed moderation action is unavailable.", error);
        }
    }

    private static FeedModerationRequestException ModerationException(string code)
    {
        var message = code switch
        {
            "REPORT_NOT_FOUND" => "ไม่พบรายงาน moderation นี้",
            "MODERATION_VERSION_CONFLICT" => "รายงานนี้ถูกเปลี่ยนสถานะแล้ว กรุณาโหลด queue ล่าสุดก่อนดำเนินการต่อ",
            "IDEMPOTENCY_CONFLICT" => "idempotency key นี้ถูกใช้กับ action อื่นแล้ว",
            "IDEMPOTENCY_IN_PROGRESS" => "action นี้กำลังถูกประมวลผลอยู่ กรุณารอสักครู่",
            "MODERATION_ACTION_UNSUPPORTED" => "action นี้ไม่รองรับกับ content ประเภทนี้",
            "MODERATION_INPUT_INVALID" => "ข้อมูล moderation action ไม่ถูกต้อง",
            _ => "Feed moderation action ถูกปฏิเสธ"
        };
        return new FeedModerationRequestException(code, message);
    }

    private static string RequestHash(Guid reportId, string action, string expectedStatus, string reason)
    {
        var payload = $"{reportId:D}|{action}|{expectedStatus}|{reason}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string RequiredString(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new CoreDatabaseException($"Feed moderation response is missing {name}.");
    }

    private static string? NullableString(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
