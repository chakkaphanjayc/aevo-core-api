using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[Collection("Feed database integration")]
public sealed class FeedModerationBoundaryIntegrationTests
{
    [Fact]
    public async Task CoreModerationAdapterIsAtomicIdempotentAndOptimistic()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        var actorId = await ReadActiveUserAsync(connection);
        if (actorId is null) return;

        var traceId = Guid.NewGuid();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertTraceAsync(connection, transaction, traceId, actorId.Value);

        var report = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_report_content(@actor_id, 'TRACE', @entity_id, 'SPAM', 'connected moderation boundary test', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["entity_id"] = traceId,
                ["idempotency_key"] = TestKey("report"),
                ["request_hash"] = TestKey("report-hash")
            });

        var reportId = Guid.Parse(report.GetProperty("reportId").GetString()!);
        Assert.Equal("OPEN", report.GetProperty("status").GetString());

        var duplicateReport = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_report_content(@actor_id, 'TRACE', @entity_id, 'SPAM', 'duplicate connected moderation boundary test', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["entity_id"] = traceId,
                ["idempotency_key"] = TestKey("duplicate-report"),
                ["request_hash"] = TestKey("duplicate-report-hash")
            });

        Assert.Equal(reportId, Guid.Parse(duplicateReport.GetProperty("reportId").GetString()!));
        Assert.False(duplicateReport.GetProperty("changed").GetBoolean());

        const string actionKey = "feed-moderation-action-connected";
        const string actionHash = "feed-moderation-action-hash-connected";
        var reviewed = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, 'OPEN', 'REVIEW', 'connected review', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["report_id"] = reportId,
                ["idempotency_key"] = actionKey,
                ["request_hash"] = actionHash
            });

        Assert.Equal("REVIEW", reviewed.GetProperty("action").GetString());
        Assert.Equal("REVIEWING", reviewed.GetProperty("reportStatus").GetString());
        Assert.True(reviewed.GetProperty("changed").GetBoolean());

        var replay = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, 'OPEN', 'REVIEW', 'connected review', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["report_id"] = reportId,
                ["idempotency_key"] = actionKey,
                ["request_hash"] = actionHash
            });

        Assert.Equal(reviewed.GetRawText(), replay.GetRawText());

        var removed = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, 'REVIEWING', 'REMOVE', 'connected removal', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["report_id"] = reportId,
                ["idempotency_key"] = TestKey("remove-action"),
                ["request_hash"] = TestKey("remove-hash")
            });

        Assert.Equal("REMOVED", removed.GetProperty("contentStatus").GetString());
        Assert.Equal("RESOLVED", removed.GetProperty("reportStatus").GetString());
        Assert.Equal("REMOVED", await ReadTraceModerationStatusAsync(connection, transaction, traceId));

        var restored = await ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, 'RESOLVED', 'RESTORE', 'connected restore', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["report_id"] = reportId,
                ["idempotency_key"] = TestKey("restore-action"),
                ["request_hash"] = TestKey("restore-hash")
            });

        Assert.Equal("VISIBLE", restored.GetProperty("contentStatus").GetString());
        Assert.Equal("VISIBLE", await ReadTraceModerationStatusAsync(connection, transaction, traceId));

        // Keep the expected conflict last: PostgreSQL aborts the current
        // transaction after a raised exception, and the test rolls the whole
        // fixture back immediately afterwards.
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => ExecuteJsonAsync(
            connection,
            transaction,
            "select public.tracedee_moderate_report_if_status(@actor_id, @report_id, 'OPEN', 'DISMISS', 'stale status', @idempotency_key, @request_hash, 'integration-test', 'feed-moderation-boundary')",
            new Dictionary<string, object?>
            {
                ["actor_id"] = actorId.Value,
                ["report_id"] = reportId,
                ["idempotency_key"] = TestKey("stale-action"),
                ["request_hash"] = TestKey("stale-hash")
            }));

        Assert.Equal("P0001", conflict.SqlState);
        Assert.Equal("MODERATION_VERSION_CONFLICT", conflict.MessageText);
        await transaction.RollbackAsync();
    }

    private static async Task<JsonElement> ExecuteJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyDictionary<string, object?> values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync();
        return JsonDocument.Parse(result as string ?? Convert.ToString(result) ?? "{}").RootElement.Clone();
    }

    private static async Task InsertTraceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid traceId,
        Guid creatorId)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.tracedee_traces
              (id, creator_id, slug, title, description, status, visibility, revision, area, topic_tags, published_at, created_at, updated_at, moderation_status)
            values
              (@id, @creator_id, @slug, 'Connected moderation boundary test', 'Connected moderation boundary test', 'PUBLISHED', 'PUBLIC', 1, 'Bangkok', @topic_tags, now(), now(), now(), 'VISIBLE')
            """,
            connection,
            transaction);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = traceId;
        command.Parameters.Add("creator_id", NpgsqlDbType.Uuid).Value = creatorId;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = $"feed-moderation-boundary-{traceId:N}";
        command.Parameters.Add("topic_tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = new[] { "integration", "moderation" };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadTraceModerationStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid traceId)
    {
        await using var command = new NpgsqlCommand(
            "select moderation_status from public.tracedee_traces where id = @id",
            connection,
            transaction);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = traceId;
        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }

    private static async Task<Guid?> ReadActiveUserAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select u.id from auth.users u join public.user_profiles p on p.id = u.id and p.status = 'ACTIVE' order by u.created_at asc, u.id asc limit 1",
            connection);
        var value = await command.ExecuteScalarAsync();
        return value is Guid id ? id : null;
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var raw = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Database integration is not configured.");
        var connection = new NpgsqlConnection(NormalizeDatabaseConnectionString(raw));
        await connection.OpenAsync();
        return connection;
    }

    private static bool DatabaseConfigured() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVO_DATABASE_URL"));

    private static string NormalizeDatabaseConnectionString(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "postgres" and not "postgresql"))
        {
            throw new InvalidOperationException("The database integration URL is not PostgreSQL.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ? "postgres" : uri.AbsolutePath.Trim('/')
        };
        var userInfo = uri.UserInfo;
        if (!string.IsNullOrWhiteSpace(userInfo))
        {
            var separator = userInfo.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(separator >= 0 ? userInfo[..separator] : userInfo);
            if (separator >= 0) builder.Password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
        }

        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = Uri.UnescapeDataString(pair[0]).Trim().ToLowerInvariant();
            if (key != "sslmode") continue;
            builder.SslMode = Uri.UnescapeDataString(pair[1]).Trim().ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("The database URL contains an unsupported sslmode.")
            };
        }

        return builder.ConnectionString;
    }

    private static string TestKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
