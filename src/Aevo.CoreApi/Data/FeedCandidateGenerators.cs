using System.Diagnostics;
using System.Globalization;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Logging;

namespace Aevo.CoreApi.Data;

public sealed record FeedGeneratorContext(
    FeedSessionContext Session,
    FeedCandidateSourceConfig SourceConfig,
    int Budget);

public sealed record FeedGeneratorOutput(
    string Generator,
    IReadOnlyList<FeedCandidateRecord> Candidates,
    int CandidateCountBeforeEligibility,
    int EligibleCount,
    int DedupeCount,
    bool Exhausted);

public interface IFeedCandidateGenerator
{
    string Name { get; }

    Task<FeedGeneratorOutput> GenerateAsync(
        FeedGeneratorContext context,
        CancellationToken cancellationToken);
}

public sealed class TraceFeedCandidateGenerator(FeedCanonicalDataStore dataStore) : IFeedCandidateGenerator
{
    public string Name => "trace-v1";

    public async Task<FeedGeneratorOutput> GenerateAsync(
        FeedGeneratorContext context,
        CancellationToken cancellationToken)
    {
        if (context.Budget <= 0)
        {
            return new FeedGeneratorOutput(Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true);
        }

        var area = context.Session.Area ?? context.Session.CoarseLocation?.Area;
        if (context.Session.Tab == "nearby" && string.IsNullOrWhiteSpace(area))
        {
            return new FeedGeneratorOutput(Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true);
        }

        var rows = await dataStore.ReadTraceCandidatesAsync(
            context.Session,
            context.Session.Query,
            area,
            context.Session.DiscoveryIntent?.Vibe,
            context.Session.Tab == "following",
            context.Budget,
            cancellationToken,
            context.Session.DiscoveryIntent?.Category);
        var candidates = rows
            .Select(row => new FeedCandidateRecord(
                "TRACE",
                row.Id.ToString(),
                "tracedee-trace",
                row.PublishedAt,
                row.Id.ToString("N"),
                $"trace:{row.Id:N}",
                row.Id.ToString(),
                0,
                row.Features))
            .ToArray();

        return new FeedGeneratorOutput(
            Name,
            candidates,
            rows.Count,
            candidates.Length,
            0,
            rows.Count < context.Budget);
    }
}

public sealed class PlaceFeedCandidateGenerator(FeedCanonicalDataStore dataStore) : IFeedCandidateGenerator
{
    public string Name => "place-v1";

    public async Task<FeedGeneratorOutput> GenerateAsync(
        FeedGeneratorContext context,
        CancellationToken cancellationToken)
    {
        if (context.Budget <= 0
            || context.Session.Tab == "following"
            || string.Equals(context.Session.DiscoveryIntent?.Category, "trace", StringComparison.Ordinal))
        {
            return new FeedGeneratorOutput(Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true);
        }

        var area = context.Session.Area ?? context.Session.CoarseLocation?.Area;
        var geo = FeedGeoResolver.Resolve(context.Session);
        if (context.Session.Tab == "nearby" && !geo.Spatial && area is null)
        {
            return new FeedGeneratorOutput(Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true);
        }

        var query = new FeedPlaceQuery(
            context.Session.Tab,
            context.Session.Query,
            area,
            context.Session.Tab == "nearby",
            geo.Spatial,
            context.Session.Tab == "nearby" && !geo.Spatial && area is not null,
            geo.Latitude,
            geo.Longitude,
            geo.RadiusMeters,
            context.Session.CandidateCutoffAt,
            Math.Min(Math.Max(context.Budget * 2, context.Budget), 72),
            context.Session.UserId,
            context.Session.DiscoveryIntent?.Vibe,
            context.Session.DiscoveryIntent?.Category);
        var rows = await dataStore.ReadPlaceCandidatesAsync(query, cancellationToken);
        var candidates = rows
            .Select(row => new FeedCandidateRecord(
                "PLACE",
                row.EntityId.ToString(),
                row.Source,
                row.PublishedAt,
                row.CanonicalIdentity,
                row.CanonicalIdentity,
                row.HydrationId.ToString(),
                row.SourcePriority,
                row.Features))
            .ToArray();
        var deduplicated = FeedSessionOrdering.Deduplicate(candidates)
            .Take(context.Budget)
            .ToArray();

        return new FeedGeneratorOutput(
            Name,
            deduplicated,
            rows.Count,
            deduplicated.Length,
            candidates.Length - deduplicated.Length,
            rows.Count < query.Limit);
    }
}

internal sealed record FeedGeoResolution(
    bool Spatial,
    double? Latitude,
    double? Longitude,
    int RadiusMeters);

internal static class FeedGeoResolver
{
    public static FeedGeoResolution Resolve(FeedSessionContext session)
    {
        var configuredMaximum = Math.Clamp(
            session.RuntimeConfig.Geo.MaxCoarseRadiusMeters,
            FeedApiContract.MinCoarseRadiusMeters,
            FeedApiContract.MaxCoarseRadiusMeters);
        var requestedRadius = session.CoarseLocation?.RadiusMeters ?? Math.Min(configuredMaximum, 5_000);
        var radius = Math.Clamp(requestedRadius, FeedApiContract.MinCoarseRadiusMeters, configuredMaximum);

        if (session.CoarseLocation?.Geohash is not null
            && TryDecodeGeohash(session.CoarseLocation.Geohash, out var latitude, out var longitude))
        {
            return new FeedGeoResolution(true, latitude, longitude, radius);
        }

        return new FeedGeoResolution(false, null, null, radius);
    }

    private static bool TryDecodeGeohash(string value, out double latitude, out double longitude)
    {
        const string alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";
        latitude = 0;
        longitude = 0;
        if (value.Length is < 1 or > 12) return false;

        var latitudeRange = new[] { -90d, 90d };
        var longitudeRange = new[] { -180d, 180d };
        var evenBit = true;
        foreach (var character in value.ToLowerInvariant())
        {
            var index = alphabet.IndexOf(character);
            if (index < 0) return false;
            for (var mask = 16; mask > 0; mask >>= 1)
            {
                if (evenBit)
                {
                    Refine(longitudeRange, (index & mask) != 0);
                }
                else
                {
                    Refine(latitudeRange, (index & mask) != 0);
                }
                evenBit = !evenBit;
            }
        }

        latitude = (latitudeRange[0] + latitudeRange[1]) / 2;
        longitude = (longitudeRange[0] + longitudeRange[1]) / 2;
        return true;
    }

    private static void Refine(double[] range, bool upperHalf)
    {
        var midpoint = (range[0] + range[1]) / 2;
        if (upperHalf) range[0] = midpoint;
        else range[1] = midpoint;
    }
}

/// <summary>
/// FEED-004 orchestration for the two independent source generators and their
/// batched typed hydration. The public facade never receives this source
/// metadata.
/// </summary>
public sealed class CanonicalFeedDataPort(
    FeedCanonicalDataStore dataStore,
    IEnumerable<IFeedCandidateGenerator> generators,
    FeedCursorSigner cursorSigner,
    IConfiguration configuration,
    ILogger<CanonicalFeedDataPort> logger,
    CoreDataStore? placeReferenceStore = null) : IFeedDataPort
{
    private const int TraceHardBudget = 96;
    private const int PlaceHardBudget = 72;
    private const int TotalHardBudget = 160;
    private readonly Dictionary<string, IFeedCandidateGenerator> generatorMap =
        generators.ToDictionary(generator => generator.Name, StringComparer.Ordinal);
    private readonly int generatorTimeoutMilliseconds = ReadTimeout(configuration);
    private readonly bool placeReferenceResolutionEnabled = ReadBoolean(configuration, "AEVO_PLACE_REFERENCE_RESOLUTION");
    private readonly CoreDataStore? referenceStore = placeReferenceStore;
    private static readonly Action<ILogger, string, int, int, bool, Exception?> GenerationCompletedLog =
        LoggerMessage.Define<string, int, int, bool>(
            LogLevel.Information,
            new EventId(4101, nameof(GenerationCompletedLog)),
            "Feed candidate generation completed for session {FeedSessionId}: generators={GeneratorCount}, candidates={CandidateCount}, degraded={Degraded}");

    public async Task<FeedCandidatePage> GetCandidatesAsync(
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        var config = session.RuntimeConfig;
        if (!config.Enabled)
        {
            return new FeedCandidatePage(
                Array.Empty<FeedCandidateRecord>(),
                null,
                new[]
                {
                    DisabledTelemetry("trace-v1", "config-disabled"),
                    DisabledTelemetry("place-v1", "config-disabled")
                });
        }

        var jobs = new List<Task<GeneratorRun>>();
        var telemetry = new List<FeedGeneratorTelemetry>();
        StartGenerator(
            jobs,
            telemetry,
            "trace-v1",
            config.CandidateSources.Trace,
            Math.Min(config.CandidateSources.Trace.Budget, TraceHardBudget),
            session,
            cancellationToken);
        StartGenerator(
            jobs,
            telemetry,
            "place-v1",
            config.CandidateSources.Place,
            Math.Min(config.CandidateSources.Place.Budget, PlaceHardBudget),
            session,
            cancellationToken);

        var completed = jobs.Count == 0 ? Array.Empty<GeneratorRun>() : await Task.WhenAll(jobs);
        var allCandidates = completed.SelectMany(run => run.Output.Candidates).ToArray();
        var deduplicated = FeedSessionOrdering.Deduplicate(allCandidates)
            .Take(TotalHardBudget)
            .ToArray();
        var completedTelemetry = completed.Select(run => run.Telemetry)
            .Concat(telemetry)
            .ToArray();
        var degraded = completedTelemetry.Any(item => item.Status is "failed" or "timeout");

        GenerationCompletedLog(
            logger,
            session.FeedSessionId,
            completedTelemetry.Length,
            deduplicated.Length,
            degraded,
            null);

        return new FeedCandidatePage(deduplicated, null, completedTelemetry, degraded);
    }

    public async Task<FeedHydrationResult> HydrateAsync(
        IReadOnlyList<FeedCandidateRecord> candidates,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        var traceCandidates = candidates
            .Where(candidate => candidate.EntityType == "TRACE" && Guid.TryParse(candidate.EffectiveHydrationId, out _))
            .ToArray();
        var tracedeePlaceCandidates = candidates
            .Where(candidate => candidate.EntityType == "PLACE"
                && candidate.Source == "tracedee-place"
                && Guid.TryParse(candidate.EffectiveHydrationId, out _))
            .ToArray();
        var storeProfileCandidates = candidates
            .Where(candidate => candidate.EntityType == "PLACE"
                && candidate.Source == "store-profile"
                && Guid.TryParse(candidate.EffectiveHydrationId, out _))
            .ToArray();

        var traceTask = TryHydrateTracesAsync(traceCandidates, session, cancellationToken);
        var placeTask = TryHydratePlacesAsync(tracedeePlaceCandidates, storeProfileCandidates, session, cancellationToken);
        var placeReferenceTask = ResolvePlaceReferencesAsync(candidates, cancellationToken);
        var traceResult = await traceTask;
        var placeResult = await placeTask;
        var placeReferenceResult = await placeReferenceTask;

        var items = new List<IFeedItemContract>(candidates.Count);
        var traceRows = traceResult.Rows.ToDictionary(row => row.Id.ToString(), StringComparer.Ordinal);
        var tracedeePlaceRows = placeResult.TracedeePlaces.ToDictionary(row => row.Id.ToString(), StringComparer.Ordinal);
        var storeProfileRows = placeResult.StoreProfiles.ToDictionary(row => row.StoreId.ToString(), StringComparer.Ordinal);
        var position = 0;

        foreach (var candidate in candidates)
        {
            var itemToken = cursorSigner.CreateItemToken(session, candidate.EntityType, candidate.EntityId, position);
            if (candidate.EntityType == "TRACE"
                && traceRows.TryGetValue(candidate.EffectiveHydrationId, out var trace))
            {
                var reasonCode = TraceReasonCode(candidate, session, trace);
                items.Add(new FeedTraceItemContract(
                    "TRACE",
                    trace.Id.ToString(),
                    itemToken,
                    trace.Slug,
                    trace.Title,
                    trace.Description,
                    trace.CreatorName,
                    trace.Area,
                    trace.TopicTags,
                    trace.StopCount,
                    reasonCode,
                    trace.PublishedAt));
                position++;
                continue;
            }

            if (candidate.EntityType == "PLACE")
            {
                if (candidate.Source == "store-profile"
                    && storeProfileRows.TryGetValue(candidate.EffectiveHydrationId, out var storeProfile))
                {
                    items.Add(new FeedPlaceItemContract(
                        "PLACE",
                        candidate.EntityId,
                        itemToken,
                        storeProfile.Slug,
                        storeProfile.Name,
                        storeProfile.Description,
                        storeProfile.Area,
                        storeProfile.Category,
                        SanitizeImageUrl(storeProfile.ImageUrl),
                        PlaceReasonCode(candidate, session),
                        placeReferenceResult.ToContract(candidate)));
                    position++;
                    continue;
                }

                if (candidate.Source == "tracedee-place"
                    && tracedeePlaceRows.TryGetValue(candidate.EffectiveHydrationId, out var place))
                {
                    items.Add(new FeedPlaceItemContract(
                        "PLACE",
                        candidate.EntityId,
                        itemToken,
                        place.Slug,
                        place.Name,
                        place.Description,
                        place.Area,
                        place.Category,
                        SanitizeImageUrl(place.ImageUrl),
                        PlaceReasonCode(candidate, session),
                        placeReferenceResult.ToContract(candidate)));
                    position++;
                }
            }
        }

        var degraded = traceResult.Degraded || placeResult.Degraded || placeReferenceResult.Degraded;
        return new FeedHydrationResult(
            items,
            degraded,
            degraded ? "FEED_HYDRATION_DEGRADED" : null);
    }

    private static string TraceReasonCode(
        FeedCandidateRecord candidate,
        FeedSessionContext session,
        TraceHydrationRow trace)
    {
        return candidate.RankingReasonCode switch
        {
            "FOLLOWING_TRACER" or "TASTE_MATCH" or "POPULAR" or "NEW_TRACE" => candidate.RankingReasonCode,
            _ => session.Tab == "following"
                ? "FOLLOWING_TRACER"
                : trace.SaveCount + trace.FollowCount > 0 ? "POPULAR" : "NEW_TRACE"
        };
    }

    private static string PlaceReasonCode(
        FeedCandidateRecord candidate,
        FeedSessionContext session) =>
        candidate.RankingReasonCode is "NEARBY_PLACE" or "POPULAR_PLACE"
            ? candidate.RankingReasonCode
            : session.Tab == "nearby" ? "NEARBY_PLACE" : "POPULAR_PLACE";

    public async Task<FeedDataPortHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        try
        {
            var snapshots = await dataStore.ReadFeedSourceHealthAsync(cancellationToken);
            var sourceHealth = snapshots
                .Select(snapshot => FeedSourceHealthPolicy.Evaluate(
                    snapshot.Source,
                    snapshot.EligibleCount,
                    snapshot.LatestEligibleAt,
                    checkedAt,
                    dataStore.SourceFreshnessWindowSeconds))
                .ToArray();
            return new FeedDataPortHealth(
                true,
                "tracedee-public-discovery",
                null,
                sourceHealth,
                checkedAt);
        }
        catch (FeedSourceDataException error)
        {
            return new FeedDataPortHealth(
                false,
                "tracedee-public-discovery",
                error.Code,
                FeedSourceHealthPolicy.SourceNames(dataStore.SourceFreshnessWindowSeconds, error.Code, checkedAt),
                checkedAt);
        }
        catch (Exception)
        {
            const string failureCode = "FEED_SOURCE_UNAVAILABLE";
            return new FeedDataPortHealth(
                false,
                "tracedee-public-discovery",
                failureCode,
                FeedSourceHealthPolicy.SourceNames(dataStore.SourceFreshnessWindowSeconds, failureCode, checkedAt),
                checkedAt);
        }
    }

    private void StartGenerator(
        ICollection<Task<GeneratorRun>> jobs,
        List<FeedGeneratorTelemetry> telemetry,
        string name,
        FeedCandidateSourceConfig sourceConfig,
        int budget,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        if (!sourceConfig.Enabled || budget <= 0)
        {
            telemetry.Add(DisabledTelemetry(name, "config-disabled"));
            return;
        }

        if (name == "place-v1" && session.Tab == "following")
        {
            telemetry.Add(DisabledTelemetry(name, "surface-not-supported"));
            return;
        }

        if (!generatorMap.TryGetValue(name, out var generator))
        {
            telemetry.Add(new FeedGeneratorTelemetry(name, "failed", 0, 0, 0, 0, true, "FEED_GENERATOR_NOT_REGISTERED"));
            return;
        }

        jobs.Add(RunGeneratorAsync(generator, sourceConfig, budget, session, cancellationToken));
    }

    private async Task<GeneratorRun> RunGeneratorAsync(
        IFeedCandidateGenerator generator,
        FeedCandidateSourceConfig sourceConfig,
        int budget,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(generatorTimeoutMilliseconds);
        try
        {
            var output = await generator.GenerateAsync(
                new FeedGeneratorContext(session, sourceConfig, budget),
                timeout.Token);
            stopwatch.Stop();
            return new GeneratorRun(
                output,
                new FeedGeneratorTelemetry(
                    output.Generator,
                    "succeeded",
                    output.CandidateCountBeforeEligibility,
                    output.EligibleCount,
                    output.DedupeCount,
                    stopwatch.ElapsedMilliseconds,
                    output.Exhausted));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new GeneratorRun(
                new FeedGeneratorOutput(generator.Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true),
                new FeedGeneratorTelemetry(generator.Name, "timeout", 0, 0, 0, stopwatch.ElapsedMilliseconds, true, "FEED_GENERATOR_TIMEOUT"));
        }
        catch (FeedSourceDataException error)
        {
            stopwatch.Stop();
            return new GeneratorRun(
                new FeedGeneratorOutput(generator.Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true),
                new FeedGeneratorTelemetry(generator.Name, "failed", 0, 0, 0, stopwatch.ElapsedMilliseconds, true, error.Code));
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return new GeneratorRun(
                new FeedGeneratorOutput(generator.Name, Array.Empty<FeedCandidateRecord>(), 0, 0, 0, true),
                new FeedGeneratorTelemetry(generator.Name, "failed", 0, 0, 0, stopwatch.ElapsedMilliseconds, true, "FEED_GENERATOR_FAILED"));
        }
    }

    private async Task<TraceHydrationResult> TryHydrateTracesAsync(
        IReadOnlyList<FeedCandidateRecord> candidates,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = candidates
                .Select(candidate => Guid.Parse(candidate.EffectiveHydrationId))
                .Distinct()
                .ToArray();
            return new TraceHydrationResult(
                await dataStore.HydrateTracesAsync(ids, session, cancellationToken),
                false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TraceHydrationResult(Array.Empty<TraceHydrationRow>(), true);
        }
        catch (Exception)
        {
            return new TraceHydrationResult(Array.Empty<TraceHydrationRow>(), true);
        }
    }

    private async Task<PlaceHydrationResult> TryHydratePlacesAsync(
        IReadOnlyList<FeedCandidateRecord> tracedeePlaceCandidates,
        IReadOnlyList<FeedCandidateRecord> storeProfileCandidates,
        FeedSessionContext session,
        CancellationToken cancellationToken)
    {
        try
        {
            var tracedeeIds = tracedeePlaceCandidates
                .Select(candidate => Guid.Parse(candidate.EffectiveHydrationId))
                .Distinct()
                .ToArray();
            var storeIds = storeProfileCandidates
                .Select(candidate => Guid.Parse(candidate.EffectiveHydrationId))
                .Distinct()
                .ToArray();
            var tracedeeTask = dataStore.HydrateTraceDeePlacesAsync(tracedeeIds, session.UserId, cancellationToken);
            var storeTask = dataStore.HydrateStoreProfilesAsync(storeIds, session.UserId, cancellationToken);
            await Task.WhenAll(tracedeeTask, storeTask);
            return new PlaceHydrationResult(await tracedeeTask, await storeTask, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlaceHydrationResult(Array.Empty<TraceDeePlaceHydrationRow>(), Array.Empty<StoreProfileHydrationRow>(), true);
        }
        catch (Exception)
        {
            return new PlaceHydrationResult(Array.Empty<TraceDeePlaceHydrationRow>(), Array.Empty<StoreProfileHydrationRow>(), true);
        }
    }

    private async Task<FeedPlaceReferenceBatch> ResolvePlaceReferencesAsync(
        IReadOnlyList<FeedCandidateRecord> candidates,
        CancellationToken cancellationToken)
    {
        var references = candidates
            .Where(candidate => candidate.EntityType == "PLACE")
            .Select(FeedPlaceReferenceAdapter.ToReference)
            .GroupBy(FeedPlaceReferenceAdapter.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (references.Count == 0)
        {
            return FeedPlaceReferenceBatch.Empty;
        }

        if (!placeReferenceResolutionEnabled || referenceStore is null)
        {
            return new FeedPlaceReferenceBatch(references, new Dictionary<string, PlaceReferenceResolution>(), false, false);
        }

        try
        {
            var resolutions = await referenceStore.ResolvePlaceReferencesAsync(
                references.Values.ToArray(),
                cancellationToken);
            return new FeedPlaceReferenceBatch(
                references,
                resolutions.ToDictionary(resolution => FeedPlaceReferenceAdapter.Key(new PlaceReference(
                    PlaceReferenceSurface.Feed,
                    resolution.Namespace,
                    resolution.ExternalId,
                    resolution.SourceVersion)), StringComparer.Ordinal),
                false,
                true);
        }
        catch (CoreDatabaseException)
        {
            // The legacy Feed item remains readable during a target migration,
            // but no canonical ID is emitted when the resolver state is down.
            return new FeedPlaceReferenceBatch(references, new Dictionary<string, PlaceReferenceResolution>(), true, true);
        }
    }

    private static FeedGeneratorTelemetry DisabledTelemetry(string name, string failureCode) =>
        new(name, "disabled", 0, 0, 0, 0, true, failureCode);

    private static int ReadTimeout(IConfiguration configuration)
    {
        var configured = int.TryParse(
            configuration["AEVO_FEED_GENERATOR_TIMEOUT_MS"],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 1_500;
        return Math.Clamp(configured, 50, 2_000);
    }

    private static bool ReadBoolean(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var value) && value;

    private static string? SanitizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;
    }

    private sealed record GeneratorRun(
        FeedGeneratorOutput Output,
        FeedGeneratorTelemetry Telemetry);

    private sealed record TraceHydrationResult(
        IReadOnlyList<TraceHydrationRow> Rows,
        bool Degraded);

    private sealed record PlaceHydrationResult(
        IReadOnlyList<TraceDeePlaceHydrationRow> TracedeePlaces,
        IReadOnlyList<StoreProfileHydrationRow> StoreProfiles,
        bool Degraded);

    private sealed record FeedPlaceReferenceBatch(
        IReadOnlyDictionary<string, PlaceReference> References,
        IReadOnlyDictionary<string, PlaceReferenceResolution> Resolutions,
        bool Degraded,
        bool ResolverEnabled)
    {
        public static FeedPlaceReferenceBatch Empty { get; } = new(
            new Dictionary<string, PlaceReference>(),
            new Dictionary<string, PlaceReferenceResolution>(),
            false,
            false);

        public FeedPlaceReferenceContract ToContract(FeedCandidateRecord candidate)
        {
            var reference = FeedPlaceReferenceAdapter.ToReference(candidate);
            Resolutions.TryGetValue(FeedPlaceReferenceAdapter.Key(reference), out var resolution);
            return FeedPlaceReferenceAdapter.ToContract(reference, resolution, ResolverEnabled, Degraded);
        }
    }
}
