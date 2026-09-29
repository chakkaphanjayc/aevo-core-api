using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[Collection("Feed database integration")]
public sealed class FeedNegativeFeedbackIntegrationTests
{
    private const string CursorSecret = "feed-negative-feedback-integration-cursor-secret-0123456789";

    [Fact]
    public async Task AuthenticatedHideIsBoundToTokenIsIdempotentAndSuppressesFutureCandidates()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        await AssertRlsSchemaAsync(connection);
        var actorId = await ReadActiveUserAsync(connection);
        if (actorId is null) return;

        var traceId = Guid.NewGuid();
        var idempotencyKey = $"feed-feedback-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        try
        {
            await RemoveTestResidueAsync(connection);
            await InsertTraceAsync(connection, traceId, actorId.Value, now.AddMinutes(-1));

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
                })
                .Build();
            await using var database = new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
            await using var canonical = new FeedCanonicalDataStore(configuration);
            var signer = new FeedCursorSigner(CursorSecret);
            var principal = FeedPrincipalFactory.FromGoSession(new CoreSession(
                Guid.NewGuid(),
                actorId.Value,
                "GO",
                now.AddHours(1),
                null,
                null,
                "feed-feedback@example.test",
                "Feed Feedback Test",
                null,
                null));
            var context = CreateContext(principal, now);
            var itemToken = signer.CreateItemToken(context, "TRACE", traceId.ToString(), 0);
            var service = new FeedNegativeFeedbackService(database, signer);
            var request = new FeedNegativeFeedbackRequestContract(
                FeedApiContract.SchemaVersion,
                context.FeedSessionId,
                itemToken,
                "hide",
                "TOO_FAR");

            var first = await service.ApplyAsync(
                principal,
                request,
                idempotencyKey,
                "feedback-request-1",
                now,
                CancellationToken.None);

            Assert.Equal("TRACE", first.ItemType);
            Assert.Equal(traceId.ToString(), first.ItemId);
            Assert.Equal("HIDE", first.Action);
            Assert.True(first.Active);
            Assert.Equal("feedback-request-1", first.RequestId);
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "select count(*) from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_id = @item_id",
                ("actor_id", actorId.Value),
                ("item_id", traceId.ToString())));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "select count(*) from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_id = @item_id and active = true and reason_code = 'TOO_FAR'",
                ("actor_id", actorId.Value),
                ("item_id", traceId.ToString())));

            var replay = await service.ApplyAsync(
                principal,
                request,
                idempotencyKey,
                "feedback-request-retry",
                now,
                CancellationToken.None);

            Assert.Equal(first, replay);

            await Assert.ThrowsAsync<FeedNegativeFeedbackIdempotencyConflictException>(() =>
                service.ApplyAsync(
                    principal,
                    request with { ReasonCode = "OTHER" },
                    idempotencyKey,
                    "feedback-request-conflict",
                    now,
                    CancellationToken.None));

            var candidates = await canonical.ReadTraceCandidatesAsync(
                context,
                null,
                null,
                null,
                false,
                20,
                CancellationToken.None);
            Assert.DoesNotContain(candidates, candidate => candidate.Id == traceId);

            var invalidRestore = await Assert.ThrowsAsync<FeedNegativeFeedbackRequestException>(() =>
                service.ApplyAsync(
                    principal,
                    request with { Action = "unhide", ReasonCode = "OTHER" },
                    $"feed-feedback-invalid-restore-{Guid.NewGuid():N}",
                    "feedback-request-invalid-restore",
                    now,
                    CancellationToken.None));
            Assert.Equal("FEEDBACK_REASON_NOT_ALLOWED", invalidRestore.Code);

            var restored = await service.ApplyAsync(
                principal,
                request with { Action = "unhide", ReasonCode = null },
                $"feed-feedback-restore-{Guid.NewGuid():N}",
                "feedback-request-restore",
                now,
                CancellationToken.None);

            Assert.Equal("TRACE", restored.ItemType);
            Assert.Equal(traceId.ToString(), restored.ItemId);
            Assert.Equal("HIDE", restored.Action);
            Assert.False(restored.Active);
            Assert.Equal(2L, await ScalarLongAsync(
                connection,
                "select count(*) from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_id = @item_id",
                ("actor_id", actorId.Value),
                ("item_id", traceId.ToString())));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "select count(*) from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_id = @item_id and reason_code = 'TOO_FAR'",
                ("actor_id", actorId.Value),
                ("item_id", traceId.ToString())));
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "select count(*) from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_id = @item_id and active = false",
                ("actor_id", actorId.Value),
                ("item_id", traceId.ToString())));

            var restoredCandidates = await canonical.ReadTraceCandidatesAsync(
                context,
                null,
                null,
                null,
                false,
                20,
                CancellationToken.None);
            Assert.Contains(restoredCandidates, candidate => candidate.Id == traceId);

            var history = await service.ListHistoryAsync(
                principal,
                2,
                "feedback-history-request",
                CancellationToken.None);
            Assert.Equal("feedback-history-request", history.RequestId);
            Assert.Equal(2, history.Entries.Count);
            Assert.Contains(history.Entries, entry =>
                entry.ItemType == "TRACE"
                && entry.ItemId == traceId.ToString()
                && entry.Action == "HIDE"
                && entry.Active
                && entry.ReasonCode == "TOO_FAR");
            Assert.Contains(history.Entries, entry =>
                entry.ItemType == "TRACE"
                && entry.ItemId == traceId.ToString()
                && entry.Action == "HIDE"
                && !entry.Active
                && entry.ReasonCode is null);

            var limitedHistory = await service.ListHistoryAsync(
                principal,
                1,
                "feedback-history-limited",
                CancellationToken.None);
            Assert.Single(limitedHistory.Entries);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                "delete from aevo_feed_negative_feedback_history where actor_id = @actor_id and item_type = 'TRACE' and item_id = @item_id; delete from aevo_feed_negative_feedback_idempotency where actor_id = @actor_id and idempotency_key = @idempotency_key; delete from aevo_feed_negative_feedback where actor_id = @actor_id and item_type = 'TRACE' and item_id = @item_id; delete from public.tracedee_traces where id = @trace_id;",
                connection);
            cleanup.Parameters.Add("actor_id", NpgsqlDbType.Uuid).Value = actorId.Value;
            cleanup.Parameters.Add("item_id", NpgsqlDbType.Text).Value = traceId.ToString();
            cleanup.Parameters.Add("trace_id", NpgsqlDbType.Uuid).Value = traceId;
            cleanup.Parameters.Add("idempotency_key", NpgsqlDbType.Text).Value = idempotencyKey;
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static FeedSessionContext CreateContext(FeedPrincipal principal, DateTimeOffset now)
    {
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            Guid.NewGuid(),
            1,
            1,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            now);
        var factory = new FeedSessionContextFactory(
            new FeedCursorSigner(CursorSecret),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
                })
                .Build());
        return factory.Create(principal, request, runtime, now).Context!;
    }

    private static async Task InsertTraceAsync(
        NpgsqlConnection connection,
        Guid traceId,
        Guid creatorId,
        DateTimeOffset publishedAt)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.tracedee_traces
              (id, creator_id, slug, title, description, status, visibility, revision, area, topic_tags, published_at, created_at, updated_at, moderation_status)
            values
              (@id, @creator_id, @slug, 'Feed feedback test', 'Feed feedback test', 'PUBLISHED', 'PUBLIC', 1, 'Bangkok', @topic_tags, @published_at, @published_at, @published_at, 'VISIBLE')
            """,
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = traceId;
        command.Parameters.Add("creator_id", NpgsqlDbType.Uuid).Value = creatorId;
        command.Parameters.Add("slug", NpgsqlDbType.Text).Value = $"feed-feedback-{traceId:N}";
        command.Parameters.Add("topic_tags", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = new[] { "feed-feedback", "integration" };
        command.Parameters.Add("published_at", NpgsqlDbType.TimestampTz).Value = publishedAt;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveTestResidueAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "delete from aevo_feed_negative_feedback_history where item_id in (select id::text from public.tracedee_traces where slug like 'feed-feedback-%'); delete from aevo_feed_negative_feedback_idempotency where idempotency_key like 'feed-feedback-%'; delete from aevo_feed_negative_feedback where item_id in (select id::text from public.tracedee_traces where slug like 'feed-feedback-%'); delete from public.tracedee_traces where slug like 'feed-feedback-%';",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRlsSchemaAsync(NpgsqlConnection connection)
    {
        await using (var table = new NpgsqlCommand(
            "select relrowsecurity from pg_class where oid = 'aevo_feed_negative_feedback'::regclass",
            connection))
        {
            Assert.True(Convert.ToBoolean(await table.ExecuteScalarAsync()));
        }

        await using (var historyTable = new NpgsqlCommand(
            "select relrowsecurity from pg_class where oid = 'aevo_feed_negative_feedback_history'::regclass",
            connection))
        {
            Assert.True(Convert.ToBoolean(await historyTable.ExecuteScalarAsync()));
        }

        await using var policies = new NpgsqlCommand(
            "select count(*) from pg_policies where schemaname = 'public' and tablename = 'aevo_feed_negative_feedback' and policyname in ('aevo_feed_negative_feedback_owner_read_policy', 'aevo_feed_negative_feedback_owner_insert_policy', 'aevo_feed_negative_feedback_owner_update_policy')",
            connection);
        Assert.Equal(3L, Convert.ToInt64(await policies.ExecuteScalarAsync()));

        await using var historyPolicies = new NpgsqlCommand(
            "select count(*) from pg_policies where schemaname = 'public' and tablename = 'aevo_feed_negative_feedback_history' and policyname in ('aevo_feed_negative_feedback_history_owner_read_policy', 'aevo_feed_negative_feedback_history_owner_insert_policy')",
            connection);
        Assert.Equal(2L, Convert.ToInt64(await historyPolicies.ExecuteScalarAsync()));

        await using var historyIndex = new NpgsqlCommand(
            "select count(*) from pg_indexes where schemaname = 'public' and tablename = 'aevo_feed_negative_feedback_history' and indexname = 'aevo_feed_negative_feedback_history_actor_created_idx'",
            connection);
        Assert.Equal(1L, Convert.ToInt64(await historyIndex.ExecuteScalarAsync()));
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<Guid?> ReadActiveUserAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select u.id from auth.users u join public.user_profiles p on p.id = u.id and p.status = 'ACTIVE' order by u.created_at asc, u.id asc limit 1",
            connection);
        return await command.ExecuteScalarAsync() as Guid?;
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
            || uri.Scheme is not "postgres" and not "postgresql")
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
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), "sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = Uri.UnescapeDataString(pair[1]).ToLowerInvariant() switch
                {
                    "disable" => SslMode.Disable,
                    "allow" => SslMode.Allow,
                    "prefer" => SslMode.Prefer,
                    "require" => SslMode.Require,
                    "verify-ca" => SslMode.VerifyCA,
                    "verify-full" => SslMode.VerifyFull,
                    _ => throw new InvalidOperationException("The database integration URL contains an unsupported sslmode.")
                };
            }
        }
        return builder.ConnectionString;
    }
}
