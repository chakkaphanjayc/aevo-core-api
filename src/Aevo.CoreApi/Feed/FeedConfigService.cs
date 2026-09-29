using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;

namespace Aevo.CoreApi.Feed;

public sealed class FeedConfigNotFoundException() : Exception("Feed config revision was not found.");

public sealed class FeedConfigNotValidatedException() : Exception("Feed config revision has not passed the server validator.");

public sealed class FeedConfigIdempotencyConflictException() : Exception("The idempotency key was already used for a different Feed config operation.");

public sealed class FeedConfigVersionConflictException(long actualVersion) : Exception("Feed config active version changed before this operation completed.")
{
    public long ActualVersion { get; } = actualVersion;
}

public sealed record FeedConfigRuntimeSnapshot(
    string Source,
    string Status,
    Guid? RevisionId,
    long? Version,
    long PointerVersion,
    FeedRuntimeConfig Config,
    JsonElement RawConfig,
    bool Degraded,
    string? ErrorCode,
    DateTimeOffset ObservedAt);

public sealed class FeedConfigService(CoreDataStore database)
{
    private FeedConfigRuntimeSnapshot? lastKnownGoodSnapshot;

    public async Task<FeedConfigActiveResponse> GetAdminSnapshotAsync(string requestId, CancellationToken cancellationToken)
    {
        var publication = await database.GetFeedConfigPublicationAsync(cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        var runtime = await ResolveRuntimeSnapshotAsync(publication, cancellationToken);
        var revision = runtime.RevisionId.HasValue
            ? await database.GetFeedConfigRevisionAsync(runtime.RevisionId.Value, cancellationToken)
            : null;
        var validation = revision is null
            ? BaselineValidation()
            : ToValidationSummary(revision.Validation);

        return new FeedConfigActiveResponse(
            FeedConfigContract.ScopeKey,
            runtime.Source,
            runtime.Status,
            runtime.RevisionId?.ToString(),
            runtime.Version,
            runtime.PointerVersion,
            FeedConfigContract.SchemaVersion,
            runtime.RawConfig,
            validation,
            runtime.Degraded,
            runtime.ErrorCode,
            runtime.ObservedAt,
            requestId);
    }

    public async Task<IReadOnlyList<FeedConfigRevisionResponse>> ListRevisionsAsync(int limit, CancellationToken cancellationToken)
    {
        var revisions = await database.ListFeedConfigRevisionsAsync(limit, cancellationToken);
        return revisions.Select(ToRevisionResponse).ToArray();
    }

    public async Task<FeedConfigRevisionResponse> CreateDraftAsync(
        Guid actorId,
        string reason,
        string requestId,
        string idempotencyKey,
        JsonElement config,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        if (config.ValueKind != JsonValueKind.Object) throw new FeedConfigRequestException("CONFIG_OBJECT_REQUIRED", "Feed config must be a JSON object.");
        if (config.GetRawText().Length > 32_768) throw new FeedConfigRequestException("CONFIG_TOO_LARGE", "Feed config must be 32 KiB or smaller.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.GetRawText()))).ToLowerInvariant();
        var revision = await database.CreateFeedConfigDraftAsync(actorId, reason.Trim(), requestId, idempotencyKey, config, hash, cancellationToken);
        return ToRevisionResponse(revision);
    }

    public async Task<FeedConfigRevisionResponse> ValidateDraftAsync(
        Guid actorId,
        Guid revisionId,
        string reason,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var revision = await database.GetFeedConfigRevisionAsync(revisionId, cancellationToken) ?? throw new FeedConfigNotFoundException();
        var validation = FeedConfigValidator.Validate(revision.Config);
        var stored = await database.RecordFeedConfigValidationAsync(actorId, revisionId, reason.Trim(), requestId, idempotencyKey, validation, cancellationToken);
        return ToRevisionResponse(revision with
        {
            Status = stored.Valid ? "VALID" : "DRAFT",
            Validation = stored
        });
    }

    public async Task<FeedConfigActiveResponse> PublishAsync(
        Guid actorId,
        Guid revisionId,
        long? expectedActiveVersion,
        string reason,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var publication = await database.PublishFeedConfigAsync(actorId, revisionId, expectedActiveVersion, reason.Trim(), requestId, idempotencyKey, cancellationToken);
        var runtime = await ResolveRuntimeSnapshotAsync(publication, cancellationToken);
        return ToActiveResponse(runtime, requestId);
    }

    public async Task<FeedConfigActiveResponse> RollbackAsync(
        Guid actorId,
        long targetVersion,
        long? expectedActiveVersion,
        string reason,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateReason(reason);
        var publication = await database.RollbackFeedConfigAsync(actorId, targetVersion, expectedActiveVersion, reason.Trim(), requestId, idempotencyKey, cancellationToken);
        var runtime = await ResolveRuntimeSnapshotAsync(publication, cancellationToken);
        return ToActiveResponse(runtime, requestId);
    }

    public async Task<FeedConfigGuardrailsResponse> GetGuardrailsAsync(string requestId, CancellationToken cancellationToken)
    {
        var runtime = await GetRuntimeSnapshotAsync(cancellationToken);
        var effective = FeedConfigValidator.EffectiveRuntimeConfig(runtime.Config);
        return new FeedConfigGuardrailsResponse(
            FeedConfigContract.ScopeKey,
            runtime.Status,
            effective.Enabled,
            effective.KillSwitch,
            effective.Safety.GuardrailsEnabled,
            effective.Safety.MaxEligibilityAgeSeconds,
            effective.Geo.MaxCoarseRadiusMeters,
            effective.Analytics.SamplePercent,
            runtime.Degraded,
            runtime.ErrorCode,
            requestId);
    }

    public async Task<FeedConfigPropagationResponse> GetPropagationAsync(string requestId, CancellationToken cancellationToken)
    {
        var publication = await database.GetFeedConfigPublicationAsync(cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        var propagation = await database.ListFeedConfigPropagationsAsync(cancellationToken);
        var runtime = await ResolveRuntimeSnapshotAsync(publication, cancellationToken);
        var legacyCompatibility = FeedConfigCompatibility.Evaluate(runtime.Config, await database.ReadLegacyFeedFlagsAsync(cancellationToken));
        var runtimes = propagation.Count > 0
            ? propagation.Select(item => new FeedConfigRuntimePropagation(
                item.RuntimeName,
                item.Status,
                item.ObservedRevisionId?.ToString(),
                item.ObservedVersion,
                item.LastCheckedAt,
                item.LatencyMs,
                item.LastErrorCode)).ToArray()
            : new[]
            {
                new FeedConfigRuntimePropagation(
                    "core-api",
                    publication.PropagationStatusForResponse(),
                    publication.ActiveRevisionId.ToString(),
                    publication.ActiveVersion,
                    publication.UpdatedAt,
                    null,
                    publication.LastErrorCode)
            };

        return new FeedConfigPropagationResponse(
            FeedConfigContract.ScopeKey,
            publication.ActiveRevisionId.ToString(),
            publication.ActiveVersion,
            publication.PointerVersion,
            runtimes,
            legacyCompatibility,
            requestId);
    }

    public async Task<FeedConfigRuntimeSnapshot> GetRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var publication = await database.GetFeedConfigPublicationAsync(cancellationToken);
            if (publication is null)
            {
                return CachedOrBaselineSnapshot("FEED_CONFIG_PUBLICATION_MISSING");
            }
            return await ResolveRuntimeSnapshotAsync(publication, cancellationToken);
        }
        catch (CoreDatabaseException)
        {
            return CachedOrBaselineSnapshot("FEED_CONFIG_DATABASE_UNAVAILABLE");
        }
    }

    private async Task<FeedConfigRuntimeSnapshot> ResolveRuntimeSnapshotAsync(FeedConfigPublicationData publication, CancellationToken cancellationToken)
    {
        FeedConfigRevisionData? active = null;
        FeedConfigRevisionData? lastKnownGood = null;
        string? errorCode = null;

        try
        {
            active = await database.GetActiveFeedConfigAsync(cancellationToken);
            if (!IsValidRevision(active))
            {
                errorCode = "FEED_CONFIG_ACTIVE_INVALID";
                lastKnownGood = await database.GetLastValidFeedConfigAsync(cancellationToken);
            }
        }
        catch (CoreDatabaseException)
        {
            errorCode = "FEED_CONFIG_DATABASE_UNAVAILABLE";
        }

        var activeTyped = ValidatedConfig(active);
        var lastKnownGoodTyped = ValidatedConfig(lastKnownGood);
        var selection = FeedConfigFallback.Resolve(
            activeTyped?.Config,
            activeTyped?.RawConfig,
            lastKnownGoodTyped?.Config,
            lastKnownGoodTyped?.RawConfig,
            errorCode);
        var selectedRevision = activeTyped is not null && selection.Source == "ACTIVE_REVISION"
            ? active
            : lastKnownGoodTyped is not null && selection.Source == "LAST_KNOWN_GOOD"
                ? lastKnownGood
                : null;
        var status = selection.Degraded ? "DEGRADED" : "HEALTHY";
        var snapshot = new FeedConfigRuntimeSnapshot(
            selection.Source,
            status,
            selectedRevision?.RevisionId,
            selectedRevision?.Version,
            publication.PointerVersion,
            FeedConfigValidator.EffectiveRuntimeConfig(selection.Config),
            selection.RawConfig,
            selection.Degraded,
            selection.ErrorCode,
            DateTimeOffset.UtcNow);
        if (selectedRevision is not null)
        {
            Volatile.Write(ref lastKnownGoodSnapshot, snapshot);
            return snapshot;
        }

        return selection.Source == "DETERMINISTIC_BASELINE"
            ? CachedOrBaselineSnapshot(selection.ErrorCode ?? "FEED_CONFIG_FALLBACK", publication.PointerVersion)
            : snapshot;
    }

    private static (FeedRuntimeConfig Config, JsonElement RawConfig)? ValidatedConfig(FeedConfigRevisionData? revision)
    {
        if (!IsValidRevision(revision)) return null;
        var validation = FeedConfigValidator.Validate(revision!.Config);
        return validation.Valid && validation.Config is not null ? (validation.Config, revision.Config) : null;
    }

    private static bool IsValidRevision(FeedConfigRevisionData? revision) =>
        revision is not null
        && revision.Validation.Valid
        && revision.Validation.ValidatorVersion == FeedConfigContract.ValidatorVersion;

    private static FeedConfigRuntimeSnapshot BaselineSnapshot(string errorCode)
    {
        return new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "DEGRADED",
            null,
            null,
            0,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            true,
            errorCode,
            DateTimeOffset.UtcNow);
    }

    private FeedConfigRuntimeSnapshot CachedOrBaselineSnapshot(string errorCode, long pointerVersion = 0)
    {
        var cached = Volatile.Read(ref lastKnownGoodSnapshot);
        return cached is null
            ? BaselineSnapshot(errorCode) with { PointerVersion = pointerVersion }
            : cached with
            {
                Source = "LAST_KNOWN_GOOD",
                Status = "DEGRADED",
                Degraded = true,
                ErrorCode = errorCode,
                PointerVersion = pointerVersion == 0 ? cached.PointerVersion : pointerVersion,
                ObservedAt = DateTimeOffset.UtcNow
            };
    }

    private static FeedConfigActiveResponse ToActiveResponse(FeedConfigRuntimeSnapshot runtime, string requestId)
    {
        var validation = runtime.RevisionId.HasValue
            ? new FeedConfigValidationSummary(true, FeedConfigContract.ValidatorVersion, Array.Empty<FeedConfigValidationIssue>(), Array.Empty<FeedConfigValidationIssue>(), null)
            : BaselineValidation();
        return new FeedConfigActiveResponse(
            FeedConfigContract.ScopeKey,
            runtime.Source,
            runtime.Status,
            runtime.RevisionId?.ToString(),
            runtime.Version,
            runtime.PointerVersion,
            FeedConfigContract.SchemaVersion,
            runtime.RawConfig,
            validation,
            runtime.Degraded,
            runtime.ErrorCode,
            runtime.ObservedAt,
            requestId);
    }

    private static FeedConfigRevisionResponse ToRevisionResponse(FeedConfigRevisionData revision)
    {
        return new FeedConfigRevisionResponse(
            revision.RevisionId.ToString(),
            revision.Version,
            revision.SchemaVersion,
            revision.Status,
            revision.ContentHash,
            revision.Config,
            ToValidationSummary(revision.Validation),
            revision.SourceRevisionId?.ToString(),
            revision.AuthorId,
            revision.Reason,
            revision.CreatedAt);
    }

    private static FeedConfigValidationSummary ToValidationSummary(FeedConfigValidationData validation) =>
        new(validation.Valid, validation.ValidatorVersion, validation.Errors, validation.Warnings, validation.ValidatedAt);

    private static FeedConfigValidationSummary BaselineValidation() =>
        new(true, FeedConfigContract.ValidatorVersion, Array.Empty<FeedConfigValidationIssue>(), Array.Empty<FeedConfigValidationIssue>(), null);

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3 || reason.Trim().Length > 500)
        {
            throw new FeedConfigRequestException("REASON_REQUIRED", "An administrative reason between 3 and 500 characters is required.");
        }
    }
}

public sealed class FeedConfigRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class FeedConfigPublicationDataExtensions
{
    public static string PropagationStatusForResponse(this FeedConfigPublicationData publication) =>
        publication.PropagationStatus switch
        {
            "PENDING" => "PENDING",
            "HEALTHY" => "HEALTHY",
            "FALLBACK" => "FALLBACK",
            "DEGRADED" => "DEGRADED",
            _ => "NOT_REPORTED"
        };
}
