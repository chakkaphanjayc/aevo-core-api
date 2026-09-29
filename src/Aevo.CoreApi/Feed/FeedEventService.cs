using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed class FeedEventService(
    CoreDataStore database,
    FeedCursorSigner cursorSigner,
    FeedConfigService configService)
{
    public async Task<FeedEventBatchResponseContract> AcceptBatchAsync(
        FeedPrincipal principal,
        FeedEventBatchContract batch,
        string requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var events = batch.Events ?? Array.Empty<FeedEventContract>();
        if (events.Count is < 1 or > FeedApiContract.MaxEventBatchSize)
        {
            throw new FeedEventRequestException("EVENT_BATCH_INVALID", $"A Feed event batch must contain between 1 and {FeedApiContract.MaxEventBatchSize} events.");
        }

        var runtime = await configService.GetRuntimeSnapshotAsync(cancellationToken);
        var results = new FeedEventResultContract[events.Count];
        var toPersist = new List<(int Index, ValidatedFeedEvent Event, string Status)>();
        var batchHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < events.Count; index++)
        {
            var candidate = events[index];
            var verification = cursorSigner.VerifyItemToken(candidate.ItemToken ?? string.Empty, now);
            var validation = FeedEventValidator.Validate(candidate, verification, principal, now);
            if (!validation.IsValid)
            {
                results[index] = new FeedEventResultContract(
                    SafeEventId(candidate.EventId, index),
                    "REJECTED",
                    validation.ErrorCode);
                continue;
            }

            var validated = validation.Event!;
            if (batchHashes.TryGetValue(validated.EventId, out var existingHash))
            {
                results[index] = string.Equals(existingHash, validated.EventHash, StringComparison.Ordinal)
                    ? new FeedEventResultContract(validated.EventId, "DUPLICATE")
                    : new FeedEventResultContract(validated.EventId, "REJECTED", "EVENT_IDEMPOTENCY_CONFLICT");
                continue;
            }

            batchHashes[validated.EventId] = validated.EventHash;
            var persistenceStatus = runtime.Config.Analytics.Enabled
                && FeedEventValidator.ShouldSample(
                    principal.CursorBinding,
                    validated.EventId,
                    runtime.Config.Analytics.SamplePercent)
                ? "ACCEPTED"
                : "SAMPLED_OUT";
            toPersist.Add((index, validated, persistenceStatus));
        }

        if (toPersist.Count > 0)
        {
            var persisted = await database.PersistFeedEventsAsync(
                principal.CursorBinding,
                principal.UserId,
                toPersist.Select(item => ToIntakeRecord(principal, item.Event, item.Status)).ToArray(),
                cancellationToken);
            var persistedById = persisted.ToDictionary(item => item.EventId, StringComparer.Ordinal);
            foreach (var item in toPersist)
            {
                var result = persistedById[item.Event.EventId];
                results[item.Index] = new FeedEventResultContract(result.EventId, result.Status, result.ErrorCode);
            }
        }

        return new FeedEventBatchResponseContract(
            results.Count(result => result.Status == "ACCEPTED"),
            results.Count(result => result.Status == "DUPLICATE"),
            results.Count(result => result.Status == "SAMPLED_OUT"),
            results.Count(result => result.Status == "REJECTED"),
            results,
            requestId);
    }

    public async Task<FeedEventHealthContract> GetHealthAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var health = await database.GetFeedEventHealthAsync(cancellationToken);
        return new FeedEventHealthContract(
            health.Pending,
            health.Processing,
            health.Failed,
            health.DeadLetter,
            health.AcceptedLast24Hours,
            health.ProcessedLast24Hours,
            health.LastProcessedAt,
            health.ConsumerVersion,
            requestId);
    }

    public async Task<FeedDiscoveryEvaluationContract> GetDiscoveryEvaluationAsync(
        int requestedDays,
        string requestId,
        CancellationToken cancellationToken)
    {
        var days = Math.Clamp(requestedDays, 1, 90);
        var data = await database.GetFeedDiscoveryEvaluationAsync(days, cancellationToken);
        var runtime = await configService.GetRuntimeSnapshotAsync(cancellationToken);
        var liveServing = runtime.Config.Enabled
            && !runtime.Config.KillSwitch
            && runtime.Config.Ranking.Mode == "LIVE";

        return new FeedDiscoveryEvaluationContract(
            days,
            "INGESTION_ONLY_NO_CAUSAL_ATTRIBUTION",
            runtime.Config.Ranking.Mode,
            liveServing,
            new FeedDiscoveryOutcomeSummaryContract(
                data.Impressions,
                data.Opens,
                data.PlaceOpens,
                data.Saves,
                data.TraceStarts,
                data.TraceCompletes,
                data.BookingClicks),
            new FeedDiscoveryGuardrailSummaryContract(
                data.CurrentActiveHides,
                data.HideTransitions,
                data.EventDeadLetters),
            data.Impressions > 0 ? (double)data.Opens / data.Impressions : null,
            data.PlaceOpens > 0 ? (double)data.BookingClicks / data.PlaceOpens : null,
            DateTimeOffset.UtcNow,
            requestId);
    }

    public Task<FeedEventConsumeResult> ConsumePendingAsync(
        string consumerName,
        int limit,
        CancellationToken cancellationToken,
        string? eventId = null) =>
        database.ConsumeFeedEventsAsync(consumerName, limit, cancellationToken, eventId);

    public Task<int> ReplayDeadLettersAsync(int limit, CancellationToken cancellationToken) =>
        database.ReplayFeedEventDeadLettersAsync(limit, cancellationToken);

    public Task<long> PurgeExpiredAsync(int retentionDays, CancellationToken cancellationToken) =>
        database.PurgeFeedEventsAsync(retentionDays, cancellationToken);

    private static FeedEventIntakeRecord ToIntakeRecord(FeedPrincipal principal, ValidatedFeedEvent item, string status) =>
        new(
            principal.CursorBinding,
            principal.UserId,
            item.EventId,
            item.EventName,
            item.FeedSessionId,
            item.ItemToken,
            item.ItemType,
            item.ItemId,
            item.Position,
            item.Source,
            FeedApiContract.SchemaVersion,
            item.OccurredAt,
            item.ConfigVersion,
            item.RankingVersion,
            item.ExperimentVariant,
            item.MetadataJson,
            item.EventHash,
            status);

    private static string SafeEventId(string? eventId, int index)
    {
        if (!string.IsNullOrWhiteSpace(eventId) && eventId.Length <= FeedApiContract.MaxEventIdLength)
        {
            return eventId;
        }
        return $"invalid-{index}";
    }
}

public sealed class FeedEventRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
