using System.Diagnostics;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[Collection("Feed database integration")]
public sealed class FeedServingProjectionIntegrationTests
{
    private const string CursorSecret = "feed-projection-integration-cursor-secret-0123456789";

    [Fact]
    public async Task ProjectionRebuildRollbackStaleReadAndIndexedLoadStayBounded()
    {
        if (!DatabaseConfigured()) return;

        await using var connection = await OpenConnectionAsync();
        await AssertProjectionSchemaAsync(connection);
        var initial = await ReadControlAsync(connection);
        Assert.NotNull(initial);

        var createdRuns = new List<Guid>();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AEVO_FEED_PROJECTION_MODE"] = "active",
                    ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
                })
                .Build();
            await using var dataStore = new FeedCanonicalDataStore(configuration);

            var first = await dataStore.RebuildFeedServingProjectionAsync(CancellationToken.None);
            createdRuns.Add(first.RunId);
            Assert.Equal("completed", first.Status);
            Assert.Equal(FeedServingProjection.Name, first.ProjectionName);
            Assert.Equal(FeedServingProjection.Version, first.ProjectionVersion);
            Assert.True(first.RowsPublished >= 0);

            var second = await dataStore.RebuildFeedServingProjectionAsync(CancellationToken.None);
            createdRuns.Add(second.RunId);
            var active = await dataStore.GetFeedProjectionHealthAsync(CancellationToken.None);
            Assert.NotNull(active);
            Assert.Equal(second.RunId.ToString("N"), active!.ActiveRunId);
            Assert.Equal(first.RunId.ToString("N"), active.PreviousRunId);
            Assert.True(active.Fresh);

            Assert.True(await dataStore.RollbackFeedServingProjectionAsync(CancellationToken.None));
            var rolledBack = await dataStore.GetFeedProjectionHealthAsync(CancellationToken.None);
            Assert.NotNull(rolledBack);
            Assert.Equal(first.RunId.ToString("N"), rolledBack!.ActiveRunId);
            Assert.Equal(second.RunId.ToString("N"), rolledBack.PreviousRunId);

            var plan = await ReadProjectionLookupPlanAsync(connection, first.RunId);
            if (plan is not null)
            {
                Assert.Contains("Index", plan, StringComparison.OrdinalIgnoreCase);
            }

            var loadWatch = Stopwatch.StartNew();
            var candidateReads = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => dataStore.ReadTraceCandidatesAsync(
                        CreateSessionContext(),
                        null,
                        null,
                        null,
                        false,
                        24,
                        CancellationToken.None)));
            loadWatch.Stop();
            Assert.Equal(8, candidateReads.Length);
            Assert.True(loadWatch.Elapsed < TimeSpan.FromSeconds(15), $"Concurrent projection reads exceeded the test bound: {loadWatch.Elapsed}.");

            var placeReads = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => dataStore.ReadPlaceCandidatesAsync(
                        new FeedPlaceQuery(
                            "explore",
                            null,
                            null,
                            false,
                            false,
                            false,
                            null,
                            null,
                            5_000,
                            DateTimeOffset.UtcNow,
                            24),
                        CancellationToken.None)));
            Assert.Equal(8, placeReads.Length);

            await SetProjectionFreshnessAsync(connection, first.RunId, 1, DateTimeOffset.UtcNow.AddMinutes(-5));
            var stale = await dataStore.GetFeedProjectionHealthAsync(CancellationToken.None);
            Assert.NotNull(stale);
            Assert.False(stale!.Fresh);
        }
        finally
        {
            await RestoreProjectionControlAsync(connection, initial!, createdRuns);
        }
    }

    private static async Task AssertProjectionSchemaAsync(NpgsqlConnection connection)
    {
        foreach (var table in new[]
                 {
                     "aevo_feed_projection_runs",
                     "aevo_feed_projection_control",
                     "aevo_feed_item_metrics_projection"
                 })
        {
            await using var command = new NpgsqlCommand(
                "select c.relrowsecurity from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = current_schema() and c.relname = @table",
                connection);
            command.Parameters.AddWithValue("table", table);
            var rlsEnabled = await command.ExecuteScalarAsync();
            Assert.True(rlsEnabled is true, $"RLS is not enabled for {table}.");
        }

        await using var indexCommand = new NpgsqlCommand(
            "select to_regclass('aevo_feed_item_metrics_lookup_idx') is not null",
            connection);
        Assert.True((bool?)await indexCommand.ExecuteScalarAsync());
    }

    private static async Task<ProjectionControlState?> ReadControlAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select active_run_id, previous_run_id, max_age_seconds from aevo_feed_projection_control where projection_name = @projection_name",
            connection);
        command.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ProjectionControlState(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetInt32(2));
    }

    private static async Task<string?> ReadProjectionLookupPlanAsync(NpgsqlConnection connection, Guid runId)
    {
        await using var itemCommand = new NpgsqlCommand(
            "select item_type, item_id from aevo_feed_item_metrics_projection where run_id = @run_id limit 1",
            connection);
        itemCommand.Parameters.AddWithValue("run_id", runId);
        await using var itemReader = await itemCommand.ExecuteReaderAsync();
        var itemType = "TRACE";
        var itemId = Guid.NewGuid();
        if (await itemReader.ReadAsync())
        {
            itemType = itemReader.GetString(0);
            itemId = itemReader.GetGuid(1);
        }
        await itemReader.CloseAsync();

        await using var disableSeqScan = new NpgsqlCommand("set local enable_seqscan = off", connection);
        await using var transaction = await connection.BeginTransactionAsync();
        disableSeqScan.Transaction = transaction;
        await disableSeqScan.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand(
            """
            explain (format json, costs false)
            select p.item_id, p.quality_score, p.save_count
            from aevo_feed_projection_control c
            join aevo_feed_item_metrics_projection p
              on p.run_id = c.active_run_id
             and p.item_type = @item_type
             and p.item_id = @item_id
             and p.freshness_state = 'fresh'
            where c.projection_name = 'feed-item-metrics'
            """,
            connection,
            transaction);
        explain.Parameters.AddWithValue("item_type", itemType);
        explain.Parameters.AddWithValue("item_id", itemId);
        var lines = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        }
        await transaction.CommitAsync();
        return string.Join('\n', lines);
    }

    private static async Task SetProjectionFreshnessAsync(
        NpgsqlConnection connection,
        Guid runId,
        int maxAgeSeconds,
        DateTimeOffset completedAt)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_feed_projection_control set max_age_seconds = @max_age_seconds where projection_name = @projection_name; update aevo_feed_projection_runs set completed_at = @completed_at where run_id = @run_id",
            connection);
        command.Parameters.AddWithValue("max_age_seconds", maxAgeSeconds);
        command.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
        command.Parameters.AddWithValue("completed_at", completedAt);
        command.Parameters.AddWithValue("run_id", runId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RestoreProjectionControlAsync(
        NpgsqlConnection connection,
        ProjectionControlState initial,
        List<Guid> createdRuns)
    {
        await using (var restore = new NpgsqlCommand(
            "update aevo_feed_projection_control set active_run_id = @active_run_id, previous_run_id = @previous_run_id, max_age_seconds = @max_age_seconds, last_error_code = null, last_error_message = null, updated_at = now() where projection_name = @projection_name",
            connection))
        {
            restore.Parameters.AddWithValue("active_run_id", (object?)initial.ActiveRunId ?? DBNull.Value);
            restore.Parameters.AddWithValue("previous_run_id", (object?)initial.PreviousRunId ?? DBNull.Value);
            restore.Parameters.AddWithValue("max_age_seconds", initial.MaxAgeSeconds);
            restore.Parameters.AddWithValue("projection_name", FeedServingProjection.Name);
            await restore.ExecuteNonQueryAsync();
        }

        if (createdRuns.Count == 0) return;
        await using var delete = new NpgsqlCommand(
            "delete from aevo_feed_projection_runs where run_id = any(@run_ids)",
            connection);
        delete.Parameters.Add("run_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = createdRuns.ToArray();
        await delete.ExecuteNonQueryAsync();
    }

    private static FeedSessionContext CreateSessionContext()
    {
        var now = DateTimeOffset.UtcNow;
        var principal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
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
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        return factory.Create(
            principal,
            new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24),
            runtime,
            now).Context!;
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
            if (pair.Length != 2 || !string.Equals(Uri.UnescapeDataString(pair[0]), "sslmode", StringComparison.OrdinalIgnoreCase)) continue;
            builder.SslMode = Uri.UnescapeDataString(pair[1]).ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("The database integration URL has an unsupported sslmode.")
            };
        }
        return builder.ConnectionString;
    }

    private sealed record ProjectionControlState(Guid? ActiveRunId, Guid? PreviousRunId, int MaxAgeSeconds);
}
