using System.Globalization;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Aevo.CoreApi.Tests;

[CollectionDefinition("Feed database integration", DisableParallelization = true)]
public sealed class FeedDatabaseIntegrationGroup;

[Collection("Feed database integration")]
public sealed class FeedDatabaseIntegrationTests
{
    private static readonly Guid BaselineRevisionId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly IReadOnlySet<string> PropagationStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "HEALTHY", "PENDING", "DEGRADED", "FALLBACK", "NOT_REPORTED"
    };
    private static readonly IReadOnlyList<string> ForYouModuleOrder = new[] { "FOR_YOU" };
    private const string BaselineContentHash = "980c4fa41dbc86a0dcd70d59c4196d81f93d739787f8e66a16de6eb681d9ce02";

    [Fact]
    public async Task FeedControlPlaneSchemaAndLifecycleUseTheRealDatabase()
    {
        if (!DatabaseConfigured()) return;

        await AssertSchemaAsync();

        await using var database = CreateDatabase();
        Assert.True(await database.CanConnectAsync(CancellationToken.None));
        var service = new FeedConfigService(database);
        var actorId = Guid.NewGuid();
        var originalPublication = await database.GetFeedConfigPublicationAsync(CancellationToken.None);
        Assert.NotNull(originalPublication);
        var original = originalPublication!;
        var originalRevision = await database.GetFeedConfigRevisionByVersionAsync(original.ActiveVersion, CancellationToken.None);
        Assert.NotNull(originalRevision);

        var draftIdempotencyKey = TestKey("draft");
        var draftRequestId = TestKey("request");
        var draftConfig = TestConfig(31, customizedDiscovery: true);
        var draft = await service.CreateDraftAsync(
            actorId,
            "Database Feed integration draft",
            draftRequestId,
            draftIdempotencyKey,
            draftConfig,
            CancellationToken.None);

        Assert.Equal("DRAFT", draft.Status);
        Assert.Equal(FeedConfigContract.SchemaVersion, draft.SchemaVersion);
        Assert.False(draft.Validation.Valid);

        var duplicateDraft = await service.CreateDraftAsync(
            actorId,
            "Database Feed integration draft",
            draftRequestId,
            draftIdempotencyKey,
            draftConfig,
            CancellationToken.None);
        Assert.Equal(draft.RevisionId, duplicateDraft.RevisionId);
        Assert.Equal(draft.Version, duplicateDraft.Version);

        await Assert.ThrowsAsync<FeedConfigIdempotencyConflictException>(() => service.CreateDraftAsync(
            actorId,
            "Database Feed integration draft",
            draftRequestId,
            draftIdempotencyKey,
            TestConfig(32),
            CancellationToken.None));

        var validationIdempotencyKey = TestKey("validate");
        var validated = await service.ValidateDraftAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            "Database Feed integration validation",
            TestKey("validate-request"),
            validationIdempotencyKey,
            CancellationToken.None);
        Assert.True(validated.Validation.Valid);
        Assert.Equal(FeedConfigContract.ValidatorVersion, validated.Validation.ValidatorVersion);

        var duplicateValidation = await service.ValidateDraftAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            "Database Feed integration validation",
            TestKey("validate-request-retry"),
            validationIdempotencyKey,
            CancellationToken.None);
        Assert.True(duplicateValidation.Validation.Valid);
        Assert.Equal(validated.RevisionId, duplicateValidation.RevisionId);

        var publishIdempotencyKey = TestKey("publish");
        var published = await service.PublishAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            original.ActiveVersion,
            "Database Feed integration publish",
            TestKey("publish-request"),
            publishIdempotencyKey,
            CancellationToken.None);

        Assert.Equal(draft.Version, published.Version);
        Assert.Equal(original.PointerVersion + 1, published.PointerVersion);
        Assert.Equal("ACTIVE_REVISION", published.Source);
        Assert.Equal("HEALTHY", published.Status);
        Assert.False(published.Degraded);
        Assert.Equal(31, published.Config.GetProperty("budgets").GetProperty("cacheTtlSeconds").GetInt32());
        Assert.Equal("EXPLICIT_FIRST", published.Config.GetProperty("discovery").GetProperty("intentPrecedence").GetString());
        Assert.Equal(0.55m, published.Config.GetProperty("discovery").GetProperty("intentMatchWeight").GetDecimal());
        Assert.Equal(6, published.Config.GetProperty("discovery").GetProperty("modules").GetProperty("maximumItems").GetInt32());
        Assert.False(published.Config.GetProperty("discovery").GetProperty("safety").GetProperty("publicEvidenceEnabled").GetBoolean());

        var runtime = await service.GetRuntimeSnapshotAsync(CancellationToken.None);
        Assert.Equal("ACTIVE_REVISION", runtime.Source);
        Assert.Equal(draft.Version, runtime.Version);
        Assert.False(runtime.Degraded);
        Assert.Equal(FeedConfigContract.DeterministicRankingVersion, runtime.Config.Ranking.Version);
        Assert.Equal(0.55m, runtime.Config.Discovery!.IntentMatchWeight);
        Assert.Equal(6, runtime.Config.Discovery.Modules.MaximumItems);
        Assert.False(runtime.Config.Discovery.Safety.PublicMediaEnabled);

        var cursorFactory = new FeedSessionContextFactory(
            new FeedCursorSigner("feed-db-integration-cursor-secret-0123456789"),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        var cursorPrincipal = FeedPrincipalFactory.Anonymous(FeedPrincipalFactory.CreateAnonymousKey());
        var cursorRequest = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var cursorSession = cursorFactory.Create(cursorPrincipal, cursorRequest, runtime, DateTimeOffset.UtcNow);
        Assert.True(cursorSession.IsValid);
        Assert.Equal(draft.Version.ToString(CultureInfo.InvariantCulture), cursorSession.Context!.ConfigVersion);
        Assert.Equal(runtime.Config.Ranking.Version, cursorSession.Context.RankingVersion);
        var cursor = cursorFactory.CreateCursor(cursorSession.Context);
        Assert.True(cursorFactory.Create(cursorPrincipal, cursorRequest with { Cursor = cursor }, runtime, DateTimeOffset.UtcNow).IsValid);

        var duplicatePublish = await service.PublishAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            original.ActiveVersion,
            "Database Feed integration publish",
            TestKey("publish-request-retry"),
            publishIdempotencyKey,
            CancellationToken.None);
        Assert.Equal(published.Version, duplicatePublish.Version);
        Assert.Equal(published.PointerVersion, duplicatePublish.PointerVersion);

        await Assert.ThrowsAsync<FeedConfigIdempotencyConflictException>(() => service.PublishAsync(
            actorId,
            original.ActiveRevisionId,
            original.ActiveVersion,
            "Database Feed integration publish",
            TestKey("publish-request-conflict"),
            publishIdempotencyKey,
            CancellationToken.None));

        await Assert.ThrowsAsync<FeedConfigVersionConflictException>(() => service.PublishAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            original.ActiveVersion,
            "Database Feed stale pointer check",
            TestKey("stale-publish"),
            TestKey("stale-publish-idempotency"),
            CancellationToken.None));

        Assert.Equal(1, await CountFeedAuditAsync(draftIdempotencyKey));
        Assert.Equal(1, await CountFeedAuditAsync(validationIdempotencyKey));
        Assert.Equal(1, await CountFeedAuditAsync(publishIdempotencyKey));
        Assert.Equal(1, await CountOutboxAsync("FEED_CONFIG_PUBLISHED", draft.RevisionId));

        var rollbackOutboxBefore = await CountOutboxAsync("FEED_CONFIG_ROLLED_BACK", original.ActiveRevisionId.ToString());
        var rollbackIdempotencyKey = TestKey("rollback");
        var rolledBack = await service.RollbackAsync(
            actorId,
            original.ActiveVersion,
            published.Version,
            "Database Feed integration rollback",
            TestKey("rollback-request"),
            rollbackIdempotencyKey,
            CancellationToken.None);

        Assert.Equal(original.ActiveVersion, rolledBack.Version);
        Assert.Equal(published.PointerVersion + 1, rolledBack.PointerVersion);
        Assert.Equal("ACTIVE_REVISION", rolledBack.Source);
        Assert.False(rolledBack.Degraded);

        var rolledBackRuntime = await service.GetRuntimeSnapshotAsync(CancellationToken.None);
        var oldCursorAfterRollback = cursorFactory.Create(
            cursorPrincipal,
            cursorRequest with { Cursor = cursor },
            rolledBackRuntime,
            DateTimeOffset.UtcNow);
        Assert.False(oldCursorAfterRollback.IsValid);
        Assert.Equal("FEED_CURSOR_INVALID", oldCursorAfterRollback.ErrorCode);

        var duplicateRollback = await service.RollbackAsync(
            actorId,
            original.ActiveVersion,
            published.Version,
            "Database Feed integration rollback",
            TestKey("rollback-request-retry"),
            rollbackIdempotencyKey,
            CancellationToken.None);
        Assert.Equal(rolledBack.Version, duplicateRollback.Version);
        Assert.Equal(rolledBack.PointerVersion, duplicateRollback.PointerVersion);

        await Assert.ThrowsAsync<FeedConfigIdempotencyConflictException>(() => service.RollbackAsync(
            actorId,
            published.Version!.Value,
            published.Version,
            "Database Feed integration rollback",
            TestKey("rollback-request-conflict"),
            rollbackIdempotencyKey,
            CancellationToken.None));

        var finalPublication = await database.GetFeedConfigPublicationAsync(CancellationToken.None);
        Assert.NotNull(finalPublication);
        Assert.Equal(original.ActiveVersion, finalPublication!.ActiveVersion);
        Assert.Equal(published.PointerVersion + 1, finalPublication.PointerVersion);
        Assert.NotNull(await database.GetFeedConfigRevisionAsync(Guid.Parse(draft.RevisionId), CancellationToken.None));
        Assert.Equal(1, await CountFeedAuditAsync(rollbackIdempotencyKey));
        Assert.Equal(rollbackOutboxBefore + 1, await CountOutboxAsync("FEED_CONFIG_ROLLED_BACK", original.ActiveRevisionId.ToString()));
    }

    [Fact]
    public async Task FeedEventsAreIdempotentAndConsumerStatsAreDurable()
    {
        if (!DatabaseConfigured()) return;

        await using var database = CreateDatabase();
        Assert.True(await database.CanConnectAsync(CancellationToken.None));
        var configService = new FeedConfigService(database);
        var runtime = await configService.GetRuntimeSnapshotAsync(CancellationToken.None);
        var principal = FeedPrincipalFactory.Anonymous(new string('e', 43));
        var signer = new FeedCursorSigner("feed-event-db-integration-secret-0123456789");
        var sessionFactory = new FeedSessionContextFactory(
            signer,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        var now = DateTimeOffset.UtcNow;
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var session = sessionFactory.Create(principal, request, runtime, now).Context!;
        var traceId = Guid.NewGuid().ToString("N");
        var itemToken = signer.CreateItemToken(session, "TRACE", traceId, 0);
        var eventId = TestKey("feed-event");
        var service = new FeedEventService(database, signer, configService);
        var candidate = new FeedEventContract(
            FeedApiContract.SchemaVersion,
            eventId,
            "impression",
            session.FeedSessionId,
            itemToken,
            now,
            0,
            "TRACE",
            new FeedEventMetadataContract("web", "explore", "for_you", "NEW_TRACE", 0.5, 1_000));

        try
        {
            var accepted = await service.AcceptBatchAsync(
                principal,
                new FeedEventBatchContract(new[] { candidate }),
                TestKey("request"),
                now,
                CancellationToken.None);
            Assert.Equal(1, accepted.Accepted);
            Assert.Equal("ACCEPTED", Assert.Single(accepted.Results).Status);

            var duplicate = await service.AcceptBatchAsync(
                principal,
                new FeedEventBatchContract(new[] { candidate }),
                TestKey("duplicate-request"),
                now,
                CancellationToken.None);
            Assert.Equal(1, duplicate.Duplicates);
            Assert.Equal("DUPLICATE", Assert.Single(duplicate.Results).Status);

            var conflictingToken = signer.CreateItemToken(session, "TRACE", Guid.NewGuid().ToString("N"), 0);
            var conflict = await service.AcceptBatchAsync(
                principal,
                new FeedEventBatchContract(new[] { candidate with { ItemToken = conflictingToken } }),
                TestKey("conflict-request"),
                now,
                CancellationToken.None);
            Assert.Equal(1, conflict.Rejected);
            Assert.Equal("EVENT_IDEMPOTENCY_CONFLICT", Assert.Single(conflict.Results).ErrorCode);

            var consumed = await service.ConsumePendingAsync("test-feed-events", 10, CancellationToken.None);
            Assert.Equal(1, consumed.Claimed);
            Assert.Equal(1, consumed.Processed);
            Assert.Equal(0, consumed.Failed);

            var health = await service.GetHealthAsync(TestKey("health"), CancellationToken.None);
            Assert.Equal(0, health.Pending);
            Assert.True(health.ProcessedLast24Hours >= 1);
            Assert.Equal("feed-events-v1", health.ConsumerVersion);

            await using var connection = await OpenDirectConnectionAsync();
            Assert.Equal(1L, await ScalarLongAsync(
                connection,
                "select event_count from aevo_feed_event_stats_daily where item_type = 'TRACE' and item_id = @item_id and event_name = 'impression'",
                ("item_id", traceId)));
        }
        finally
        {
            await using var cleanup = await OpenDirectConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                delete from aevo_feed_event_stats_daily where item_id = @item_id;
                delete from aevo_feed_event_inbox where principal_binding = @principal_binding and event_id = @event_id;
                delete from aevo_feed_event_outbox where principal_binding = @principal_binding and event_id = @event_id;
                delete from aevo_feed_event_intake where principal_binding = @principal_binding and event_id = @event_id;
                """,
                cleanup);
            command.Parameters.AddWithValue("item_id", traceId);
            command.Parameters.AddWithValue("principal_binding", principal.CursorBinding);
            command.Parameters.AddWithValue("event_id", eventId);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DiscoveryOutcomeEventsRemainTypedAndProjectToDailyStats()
    {
        if (!DatabaseConfigured()) return;

        await using var database = CreateDatabase();
        Assert.True(await database.CanConnectAsync(CancellationToken.None));
        var configService = new FeedConfigService(database);
        var runtime = await configService.GetRuntimeSnapshotAsync(CancellationToken.None);
        var identity = await ReadExistingIdentityAsync();
        var principal = FeedPrincipalFactory.FromGoSession(new CoreSession(
            Guid.NewGuid(),
            identity.UserId,
            "GO",
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            null,
            identity.Email,
            identity.DisplayName,
            null,
            null));
        var signer = new FeedCursorSigner("feed-outcome-db-integration-secret-0123456789");
        var sessionFactory = new FeedSessionContextFactory(
            signer,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300"
            }).Build());
        var now = DateTimeOffset.UtcNow;
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var session = sessionFactory.Create(principal, request, runtime, now).Context!;
        var placeId = Guid.NewGuid().ToString();
        var eventNames = new[] { "impression", "open", "place_open", "booking_click" };
        var events = eventNames
            .Select((eventName, index) => new FeedEventContract(
                FeedApiContract.SchemaVersion,
                TestKey($"discovery-outcome-{index}"),
                eventName,
                session.FeedSessionId,
                signer.CreateItemToken(session, "PLACE", placeId, 0),
                now,
                0,
                "PLACE",
                eventName == "impression"
                    ? new FeedEventMetadataContract("web", "explore", "for_you", "POPULAR_PLACE", 0.8, 2_000)
                    : null))
            .ToArray();
        var eventIds = events.Select(item => item.EventId).ToArray();
        var service = new FeedEventService(database, signer, configService);

        try
        {
            var accepted = await service.AcceptBatchAsync(
                principal,
                new FeedEventBatchContract(events),
                TestKey("discovery-outcome-request"),
                now,
                CancellationToken.None);

            Assert.Equal(eventNames.Length, accepted.Accepted);
            Assert.Equal(0, accepted.Rejected);

            var consumed = await service.ConsumePendingAsync("test-discovery-outcomes", 20, CancellationToken.None);
            Assert.Equal(eventNames.Length, consumed.Claimed);
            Assert.Equal(eventNames.Length, consumed.Processed);
            Assert.Equal(0, consumed.Failed);

            await using var connection = await OpenDirectConnectionAsync();
            foreach (var eventName in eventNames)
            {
                Assert.Equal(1L, await ScalarLongAsync(
                    connection,
                    "select event_count from aevo_feed_event_stats_daily where item_type = 'PLACE' and item_id = @item_id and event_name = @event_name",
                    ("item_id", placeId),
                    ("event_name", eventName)));
            }

            var evaluation = await service.GetDiscoveryEvaluationAsync(
                1,
                TestKey("discovery-outcome-evaluation-request"),
                CancellationToken.None);
            Assert.Equal("INGESTION_ONLY_NO_CAUSAL_ATTRIBUTION", evaluation.EvaluationStatus);
            Assert.Equal(runtime.Config.Ranking.Mode, evaluation.RankingMode);
            Assert.Equal(
                runtime.Config.Enabled && !runtime.Config.KillSwitch && runtime.Config.Ranking.Mode == "LIVE",
                evaluation.LiveServing);
            Assert.True(evaluation.Outcomes.Impressions >= 1);
            Assert.True(evaluation.Outcomes.Opens >= 1);
            Assert.True(evaluation.Outcomes.PlaceOpens >= 1);
            Assert.True(evaluation.Outcomes.BookingClicks >= 1);
            Assert.NotNull(evaluation.OpenRate);
            Assert.NotNull(evaluation.BookingClickRate);
            Assert.InRange(evaluation.OpenRate!.Value, 0, 1);
            Assert.InRange(evaluation.BookingClickRate!.Value, 0, 1);
        }
        finally
        {
            await using var cleanup = await OpenDirectConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                delete from aevo_feed_event_stats_daily where item_type = 'PLACE' and item_id = @item_id and event_name = any(@event_names);
                delete from aevo_feed_event_inbox where principal_binding = @principal_binding and event_id = any(@event_ids);
                delete from aevo_feed_event_outbox where principal_binding = @principal_binding and event_id = any(@event_ids);
                delete from aevo_feed_event_intake where principal_binding = @principal_binding and event_id = any(@event_ids);
                """,
                cleanup);
            command.Parameters.AddWithValue("item_id", placeId);
            command.Parameters.AddWithValue("event_names", eventNames);
            command.Parameters.AddWithValue("principal_binding", principal.CursorBinding);
            command.Parameters.AddWithValue("event_ids", eventIds);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentPublishesHonorTheActivePointerVersion()
    {
        if (!DatabaseConfigured()) return;

        await using var database = CreateDatabase();
        Assert.True(await database.CanConnectAsync(CancellationToken.None));
        var service = new FeedConfigService(database);
        var actorId = Guid.NewGuid();
        var original = await database.GetFeedConfigPublicationAsync(CancellationToken.None);
        Assert.NotNull(original);
        var originalPublication = original!;

        var first = await CreateValidatedDraftAsync(database, service, actorId, 33, "concurrent-first");
        var second = await CreateValidatedDraftAsync(database, service, actorId, 34, "concurrent-second");
        var firstAttempt = PublishAttemptAsync(service, actorId, first, originalPublication.ActiveVersion, "concurrent-publish-first");
        var secondAttempt = PublishAttemptAsync(service, actorId, second, originalPublication.ActiveVersion, "concurrent-publish-second");
        var attempts = await Task.WhenAll(firstAttempt, secondAttempt);

        Assert.Equal(1, attempts.Count(attempt => attempt.Publication is not null));
        Assert.Equal(1, attempts.Count(attempt => attempt.VersionConflict));
        var winner = attempts.Single(attempt => attempt.Publication is not null).Publication!;

        var rollback = await service.RollbackAsync(
            actorId,
            originalPublication.ActiveVersion,
            winner.Version,
            "Database Feed concurrent publish cleanup",
            TestKey("concurrent-rollback-request"),
            TestKey("concurrent-rollback"),
            CancellationToken.None);
        Assert.Equal(originalPublication.ActiveVersion, rollback.Version);
    }

    [Fact]
    public async Task AppScopedDatabaseSessionsRejectCrossApplicationAndExpiredTokens()
    {
        if (!DatabaseConfigured()) return;

        await using var database = CreateDatabase();
        Assert.True(await database.CanConnectAsync(CancellationToken.None));
        var identity = await ReadExistingIdentityAsync();
        var goUserId = identity.UserId;
        var goOrganizationId = Guid.NewGuid();
        var goStoreId = Guid.NewGuid();
        var go = await database.CreateSessionAsync(
            goUserId,
            identity.Email,
            identity.DisplayName,
            "GO",
            goOrganizationId,
            goStoreId,
            300,
            900,
            CancellationToken.None);

        var resolvedGo = await database.ResolveSessionAsync(go.SessionToken, "GO", CancellationToken.None);
        Assert.NotNull(resolvedGo);
        Assert.Equal(goUserId, resolvedGo!.UserId);
        Assert.Equal("GO", resolvedGo.AppCode);
        Assert.Equal(goOrganizationId, resolvedGo.OrganizationId);
        Assert.Equal(goStoreId, resolvedGo.StoreId);
        Assert.Null(await database.ResolveSessionAsync(go.SessionToken, "ADMIN", CancellationToken.None));
        Assert.Null(await database.ResolveSessionAsync(go.SessionToken, "HUB", CancellationToken.None));
        Assert.Null(await database.ResolveSessionAsync(Tamper(go.SessionToken), "GO", CancellationToken.None));

        var admin = await database.CreateSessionAsync(
            identity.UserId,
            identity.Email,
            identity.DisplayName,
            "ADMIN",
            null,
            null,
            300,
            900,
            CancellationToken.None);
        var hub = await database.CreateSessionAsync(
            identity.UserId,
            identity.Email,
            identity.DisplayName,
            "HUB",
            null,
            null,
            300,
            900,
            CancellationToken.None);
        Assert.Null(await database.ResolveSessionAsync(admin.SessionToken, "GO", CancellationToken.None));
        Assert.Null(await database.ResolveSessionAsync(hub.SessionToken, "GO", CancellationToken.None));

        await ExpireSessionAsync(go.SessionId);
        Assert.Null(await database.ResolveSessionAsync(go.SessionToken, "GO", CancellationToken.None));

        await database.RevokeSessionAsync(admin.SessionToken, "ADMIN", CancellationToken.None);
        await database.RevokeSessionAsync(hub.SessionToken, "HUB", CancellationToken.None);
    }

    private static async Task AssertSchemaAsync()
    {
        await using var connection = await OpenDirectConnectionAsync();
        foreach (var table in new[]
        {
            "aevo_feed_config_revisions",
            "aevo_feed_config_validation_results",
            "aevo_feed_config_publications",
            "aevo_feed_config_audit_events",
            "aevo_feed_config_propagations",
            "aevo_feed_event_intake",
            "aevo_feed_event_outbox",
            "aevo_feed_event_inbox",
            "aevo_feed_event_stats_daily"
        })
        {
            Assert.Equal(1L, await ScalarLongAsync(connection, "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = @name", ("name", table)));
            Assert.True(await ScalarBoolAsync(connection, "select c.relrowsecurity from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = @name", ("name", table)));
        }

        Assert.Equal(1L, await ScalarLongAsync(connection, "select count(*) from pg_trigger where tgname = 'aevo_feed_config_revisions_immutable_trigger' and not tgisinternal"));
        Assert.Equal(1L, await ScalarLongAsync(connection, "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'public' and c.relname = 'aevo_outbox_events'"));

        await using (var baseline = new NpgsqlCommand(
            """
            select r.version, r.schema_version, r.status, r.content_hash,
                   p.active_version, p.pointer_version, p.propagation_status
            from aevo_feed_config_revisions r
            join aevo_feed_config_publications p on p.active_revision_id = r.revision_id
            where r.revision_id = @revision_id and p.scope_key = 'GO_PUBLIC_FEED'
            """,
            connection))
        {
            baseline.Parameters.AddWithValue("revision_id", BaselineRevisionId);
            await using var reader = await baseline.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("1", reader.GetInt64(0).ToString(CultureInfo.InvariantCulture));
            Assert.Equal("1", reader.GetString(1));
            Assert.Equal("ACTIVE", reader.GetString(2));
            Assert.Equal(BaselineContentHash, reader.GetString(3));
            Assert.Equal(reader.GetInt64(0), reader.GetInt64(4));
            Assert.True(reader.GetInt64(5) > 0);
            Assert.Contains(reader.GetString(6), PropagationStatuses);
        }

        var policyNames = await ReadPolicyNamesAsync(connection);
        foreach (var policy in new[]
        {
            "aevo_feed_config_revisions_platform_read_policy",
            "aevo_feed_config_revisions_platform_write_policy",
            "aevo_feed_config_validation_platform_read_policy",
            "aevo_feed_config_validation_platform_insert_policy",
            "aevo_feed_config_validation_platform_update_policy",
            "aevo_feed_config_publications_platform_read_policy",
            "aevo_feed_config_publications_platform_write_policy",
            "aevo_feed_config_audit_platform_read_policy",
            "aevo_feed_config_audit_platform_write_policy",
            "aevo_feed_config_propagations_platform_read_policy",
            "aevo_feed_config_propagations_platform_insert_policy",
            "aevo_feed_config_propagations_platform_update_policy"
        })
        {
            Assert.Contains(policy, policyNames);
        }

        await AssertRevisionWriteRejectedAsync(connection, "update aevo_feed_config_revisions set reason = reason where revision_id = @revision_id", BaselineRevisionId);
        await AssertRevisionWriteRejectedAsync(connection, "delete from aevo_feed_config_revisions where revision_id = @revision_id", BaselineRevisionId);

        var role = await ReadDatabaseRoleAsync(connection);
        if (!role.BypassesRowLevelSecurity && !role.OwnsFeedRevisionTable)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("select set_config('aevo.platform_role', '', true)", connection, transaction);
            await context.ExecuteScalarAsync();
            var count = await ScalarLongAsync(connection, "select count(*) from aevo_feed_config_revisions", transaction: transaction);
            Assert.Equal(0L, count);
            await transaction.RollbackAsync();
        }

        if (await ScalarLongAsync(connection, "select count(*) from pg_roles where rolname = 'authenticated'") == 1)
        {
            await AssertBrowserRoleCannotMutateAsync(connection);
        }
    }

    private static async Task<FeedConfigRevisionResponse> CreateValidatedDraftAsync(
        CoreDataStore database,
        FeedConfigService service,
        Guid actorId,
        int cacheTtlSeconds,
        string label)
    {
        var draft = await service.CreateDraftAsync(
            actorId,
            $"Database Feed {label} draft",
            TestKey($"{label}-request"),
            TestKey($"{label}-draft"),
            TestConfig(cacheTtlSeconds),
            CancellationToken.None);
        return await service.ValidateDraftAsync(
            actorId,
            Guid.Parse(draft.RevisionId),
            $"Database Feed {label} validation",
            TestKey($"{label}-validate-request"),
            TestKey($"{label}-validate"),
            CancellationToken.None);
    }

    private static async Task<PublishAttempt> PublishAttemptAsync(
        FeedConfigService service,
        Guid actorId,
        FeedConfigRevisionResponse revision,
        long expectedActiveVersion,
        string label)
    {
        try
        {
            var publication = await service.PublishAsync(
                actorId,
                Guid.Parse(revision.RevisionId),
                expectedActiveVersion,
                $"Database Feed {label}",
                TestKey($"{label}-request"),
                TestKey(label),
                CancellationToken.None);
            return new PublishAttempt(publication, false);
        }
        catch (FeedConfigVersionConflictException)
        {
            return new PublishAttempt(null, true);
        }
    }

    private static async Task<long> CountFeedAuditAsync(string idempotencyKey)
    {
        await using var connection = await OpenDirectConnectionAsync();
        return await ScalarLongAsync(connection, "select count(*) from aevo_feed_config_audit_events where app_code = 'ADMIN' and idempotency_key = @idempotency_key", ("idempotency_key", idempotencyKey));
    }

    private static async Task<long> CountOutboxAsync(string eventType, string aggregateId)
    {
        await using var connection = await OpenDirectConnectionAsync();
        return await ScalarLongAsync(connection, "select count(*) from aevo_outbox_events where event_type = @event_type and aggregate_id = @aggregate_id", ("event_type", eventType), ("aggregate_id", aggregateId));
    }

    private static async Task ExpireSessionAsync(Guid sessionId)
    {
        await using var connection = await OpenDirectConnectionAsync();
        await using var command = new NpgsqlCommand(
            "update aevo_app_sessions set expires_at = now() - interval '1 second', idle_expires_at = now() - interval '1 second' where id = @id",
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<ExistingIdentity> ReadExistingIdentityAsync()
    {
        await using var connection = await OpenDirectConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select id, email, coalesce(display_name, '') from public.user_profiles order by created_at limit 1",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "An existing auth-backed user profile is required for session integration.");
        return new ExistingIdentity(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<HashSet<string>> ReadPolicyNamesAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select policyname from pg_policies where schemaname = 'public' and tablename in ('aevo_feed_config_revisions', 'aevo_feed_config_validation_results', 'aevo_feed_config_publications', 'aevo_feed_config_audit_events', 'aevo_feed_config_propagations')",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task AssertRevisionWriteRejectedAsync(NpgsqlConnection connection, string sql, Guid revisionId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var context = new NpgsqlCommand("select set_config('aevo.platform_role', 'platform_admin', true)", connection, transaction))
            {
                await context.ExecuteScalarAsync();
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("revision_id", revisionId);
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
            Assert.Fail("Feed config revisions accepted an update or delete.");
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<(bool BypassesRowLevelSecurity, bool OwnsFeedRevisionTable)> ReadDatabaseRoleAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select r.rolsuper, r.rolbypassrls, exists (select 1 from pg_class c where c.relowner = r.oid and c.relname = 'aevo_feed_config_revisions') from pg_roles r where r.rolname = current_user",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0) || reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private static async Task AssertBrowserRoleCannotMutateAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand("set local role authenticated", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        try
        {
            var visibleRows = await ScalarLongAsync(connection, "select count(*) from aevo_feed_config_revisions", transaction: transaction);
            Assert.Equal(0L, visibleRows);
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
            return;
        }

        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into aevo_feed_config_revisions (revision_id, schema_version, status, config, content_hash, author_id, author_app_code, reason) values (@revision_id, '1', 'DRAFT', '{}'::jsonb, repeat('0', 64), @author_id, 'ADMIN', 'browser role probe')",
                connection,
                transaction);
            insert.Parameters.AddWithValue("revision_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("author_id", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
            Assert.Fail("The browser-equivalent authenticated role mutated Feed config.");
        }
        catch (PostgresException)
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        return await ScalarLongAsync(connection, sql, null, parameters);
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection,
        string sql,
        NpgsqlTransaction? transaction,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<NpgsqlConnection> OpenDirectConnectionAsync()
    {
        var url = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Database integration is not configured.");
        var connection = new NpgsqlConnection(NormalizeDatabaseConnectionString(url));
        await connection.OpenAsync();
        return connection;
    }

    private static CoreDataStore CreateDatabase()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        return new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance);
    }

    private static bool DatabaseConfigured() => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEVO_DATABASE_URL"));

    private static JsonElement TestConfig(int cacheTtlSeconds, bool customizedDiscovery = false) => FeedConfigValidator.Serialize(
        FeedConfigDefaults.Config with
        {
            Budgets = FeedConfigDefaults.Config.Budgets with { CacheTtlSeconds = cacheTtlSeconds },
            Discovery = customizedDiscovery
                ? FeedConfigDefaults.Config.Discovery! with
                {
                    IntentMatchWeight = 0.55m,
                    Modules = new FeedDiscoveryModulesConfig(true, 1, 6, ForYouModuleOrder)
                }
                : FeedConfigDefaults.Config.Discovery
        });

    private static string TestKey(string label) => $"feed-db-test-{label}-{Guid.NewGuid():N}";

    private static string Tamper(string value) => value[..^1] + (value[^1] == 'a' ? 'b' : 'a');

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

    private sealed record PublishAttempt(FeedConfigActiveResponse? Publication, bool VersionConflict);
    private sealed record ExistingIdentity(Guid UserId, string Email, string DisplayName);
}
