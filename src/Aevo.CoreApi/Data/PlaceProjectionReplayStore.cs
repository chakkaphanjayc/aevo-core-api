using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed class PlaceProjectionReplayConflictException(string message) : Exception(message);

public sealed class PlaceProjectionReplayAuthorizationException(string message) : Exception(message);

public sealed partial class CoreDataStore
{
    private const int MaxProjectionReplayFixtures = 500;
    private const int MaxProjectionReplayTtlSeconds = 30 * 24 * 60 * 60;

    public async Task<PlaceProjectionReplayResponseContract> ApplyPlaceProjectionReplayAsync(
        string requestId,
        PlaceProjectionReplayRequestContract request,
        CancellationToken cancellationToken)
    {
        ValidatePlaceProjectionReplayRequest(request);

        var observedAt = request.ObservedAt.ToUniversalTime();
        var report = PlaceProjectionReplayValidator.Validate(
            request.Fixtures.Select(fixture => fixture is null
                ? new PlaceProjectionReplayFixture(string.Empty, null)
                : new PlaceProjectionReplayFixture(fixture.Label, fixture.Summary)),
            new PlaceProjectionReplayOptions(
                request.ProjectionVersion.Trim(),
                request.SourceRevision.Trim(),
                observedAt,
                TimeSpan.FromSeconds(request.TtlSeconds)));

        if (!report.IsValid)
        {
            return ToReplayResponse(
                request.RunId,
                report,
                "rejected",
                success: false,
                publishedCount: 0,
                failedCount: report.FailedCount,
                missingPlaceCount: 0,
                rejectedPlaceCount: 0,
                failureCode: "PLACE_PROJECTION_REPLAY_INVALID",
                alreadyApplied: false,
                requestId,
                report.Failures);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var platformRole = await ReadActivePlatformRoleAsync(connection, transaction, request.ActorId, cancellationToken);
        if (platformRole is null)
        {
            throw new PlaceProjectionReplayAuthorizationException("The replay actor is not an active privileged platform user.");
        }

        await ApplyAdminPlaceContextAsync(connection, transaction, request.ActorId, platformRole, cancellationToken);

        try
        {
            var existingRun = await ReadProjectionRunAsync(connection, transaction, request.RunId, cancellationToken);
            if (existingRun is not null)
            {
                if (existingRun.Status == "completed"
                    && string.Equals(existingRun.ReplayFingerprint, report.ReplayFingerprint, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return ToReplayResponse(
                        request.RunId,
                        report,
                        "already_applied",
                        success: true,
                        publishedCount: existingRun.RowsPublished,
                        failedCount: existingRun.RowsFailed,
                        missingPlaceCount: 0,
                        rejectedPlaceCount: 0,
                        failureCode: null,
                        alreadyApplied: true,
                        requestId,
                        report.Failures);
                }

                throw new PlaceProjectionReplayConflictException(
                    $"Projection replay run {request.RunId:D} already exists with status {existingRun.Status}.");
            }

            await InsertProjectionRunStartedAsync(
                connection,
                transaction,
                request,
                report.ReplayFingerprint,
                observedAt,
                cancellationToken);

            var registryRows = await ReadReplayRegistryRowsAsync(
                connection,
                transaction,
                report.Projections.Select(projection => projection.PlaceId).ToArray(),
                cancellationToken);
            var missingPlaceIds = report.Projections
                .Select(projection => projection.PlaceId)
                .Where(placeId => !registryRows.ContainsKey(placeId))
                .ToArray();
            var nonPublicPlaceIds = registryRows
                .Where(pair => pair.Value.Status is not ("visible" or "limited"))
                .Select(pair => pair.Key)
                .ToArray();
            var sourceRevisionMismatchIds = registryRows
                .Where(pair => string.Equals(pair.Value.SourceRevision, request.SourceRevision.Trim(), StringComparison.Ordinal) is false)
                .Select(pair => pair.Key)
                .ToArray();

            var registryFailures = new List<PlaceProjectionReplayFailure>();
            registryFailures.AddRange(missingPlaceIds.Select(placeId => new PlaceProjectionReplayFailure(
                placeId.ToString("D"),
                "place-not-found",
                $"Canonical Place {placeId:D} does not exist in the registry.")));
            registryFailures.AddRange(nonPublicPlaceIds.Select(placeId => new PlaceProjectionReplayFailure(
                placeId.ToString("D"),
                "place-not-public",
                $"Canonical Place {placeId:D} is not visible or limited.")));
            registryFailures.AddRange(sourceRevisionMismatchIds.Select(placeId => new PlaceProjectionReplayFailure(
                placeId.ToString("D"),
                "source-revision-mismatch",
                $"Canonical Place {placeId:D} does not have source revision {request.SourceRevision.Trim()}.")));

            if (registryFailures.Count > 0)
            {
                var failureCode = missingPlaceIds.Length > 0
                    ? "PLACE_PROJECTION_REPLAY_PLACE_NOT_FOUND"
                    : nonPublicPlaceIds.Length > 0
                        ? "PLACE_PROJECTION_REPLAY_PLACE_NOT_PUBLIC"
                        : "PLACE_PROJECTION_REPLAY_SOURCE_REVISION_MISMATCH";
                await MarkProjectionRunFailedAsync(
                    connection,
                    transaction,
                    request.RunId,
                    report.InputCount,
                    registryFailures.Count,
                    failureCode,
                    string.Join("; ", registryFailures.Select(failure => failure.Message).Take(8)),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                var failures = report.Failures.Concat(registryFailures).ToArray();
                return ToReplayResponse(
                    request.RunId,
                    report,
                    "rejected",
                    success: false,
                    publishedCount: 0,
                    failedCount: report.FailedCount + registryFailures.Count,
                    missingPlaceCount: missingPlaceIds.Length,
                    rejectedPlaceCount: registryFailures.Count - missingPlaceIds.Length,
                    failureCode,
                    alreadyApplied: false,
                    requestId,
                    failures);
            }

            foreach (var projection in report.Projections)
            {
                await UpsertProjectionAsync(connection, transaction, projection, cancellationToken);
            }

            await MarkProjectionRunCompletedAsync(
                connection,
                transaction,
                request.RunId,
                report.InputCount,
                report.PublishedCount,
                report.ReplayFingerprint,
                cancellationToken);

            await InsertAuditAsync(
                connection,
                transaction,
                request.ActorId,
                "ADMIN",
                "PLACE_PROJECTION_REPLAY_APPLIED",
                "place_projection_run",
                request.RunId.ToString("D"),
                request.Reason.Trim(),
                null,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        runId = request.RunId,
                        inputCount = report.InputCount,
                        publishedCount = report.PublishedCount,
                        replayFingerprint = report.ReplayFingerprint,
                        projectionVersion = request.ProjectionVersion.Trim(),
                        sourceRevision = request.SourceRevision.Trim()
                    },
                    PlaceWriteJsonOptions),
                requestId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ToReplayResponse(
                request.RunId,
                report,
                "completed",
                success: true,
                publishedCount: report.PublishedCount,
                failedCount: 0,
                missingPlaceCount: 0,
                rejectedPlaceCount: 0,
                failureCode: null,
                alreadyApplied: false,
                requestId,
                report.Failures);
        }
        catch (PlaceProjectionReplayConflictException)
        {
            throw;
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place projection replay failed.", error);
        }
    }

    private static void ValidatePlaceProjectionReplayRequest(PlaceProjectionReplayRequestContract request)
    {
        if (request.RunId == Guid.Empty)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_RUN_ID_INVALID", "A non-empty replay run id is required.");
        }
        if (request.ActorId == Guid.Empty)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_ACTOR_INVALID", "A non-empty privileged actor id is required.");
        }
        if (request.ProjectionVersion.Trim().Length is < 1 or > 128)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_PROJECTION_VERSION_INVALID", "Projection version must be between 1 and 128 characters.");
        }
        if (request.SourceRevision.Trim().Length is < 1 or > 256)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_SOURCE_REVISION_INVALID", "Source revision must be between 1 and 256 characters.");
        }
        if (request.ObservedAt == default || request.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_OBSERVED_AT_INVALID", "ObservedAt must be set and cannot be more than five minutes in the future.");
        }
        if (request.TtlSeconds is < 1 or > MaxProjectionReplayTtlSeconds)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_TTL_INVALID", "Projection TTL must be between one second and thirty days.");
        }
        if (request.Fixtures is null || request.Fixtures.Count is < 1 or > MaxProjectionReplayFixtures)
        {
            throw new PlaceMutationRequestException("PLACE_REPLAY_BATCH_INVALID", $"A projection replay batch must contain between one and {MaxProjectionReplayFixtures} fixtures.");
        }
        if (request.Reason.Trim().Length is < 3 or > 500)
        {
            throw new PlaceMutationRequestException("REASON_REQUIRED", "A replay reason between 3 and 500 characters is required.");
        }
    }

    private static async Task<string?> ReadActivePlatformRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select role_code from aevo_platform_roles where user_id = @user_id and status = 'active' and lower(role_code) in ('super_admin', 'platform_owner', 'platform_admin') limit 1",
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", actorId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<PlaceProjectionRunState?> ReadProjectionRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select status, replay_fingerprint, rows_published, rows_failed from aevo_place_projection_runs where run_id = @run_id for update",
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PlaceProjectionRunState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task InsertProjectionRunStartedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaceProjectionReplayRequestContract request,
        string replayFingerprint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_projection_runs
              (run_id, projection_version, source_revision, status, rows_seen,
               rows_published, rows_failed, replay_fingerprint, reason, started_at)
            values
              (@run_id, @projection_version, @source_revision, 'started', 0,
               0, 0, @replay_fingerprint, @reason, @started_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", request.RunId);
        command.Parameters.AddWithValue("projection_version", request.ProjectionVersion.Trim());
        command.Parameters.AddWithValue("source_revision", request.SourceRevision.Trim());
        command.Parameters.AddWithValue("replay_fingerprint", replayFingerprint);
        command.Parameters.AddWithValue("reason", request.Reason.Trim());
        command.Parameters.AddWithValue("started_at", observedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkProjectionRunFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        int rowsSeen,
        int rowsFailed,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_place_projection_runs set status = 'failed', rows_seen = @rows_seen, rows_published = 0, rows_failed = @rows_failed, error_code = @error_code, error_message = @error_message, completed_at = now() where run_id = @run_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("rows_seen", rowsSeen);
        command.Parameters.AddWithValue("rows_failed", rowsFailed);
        command.Parameters.AddWithValue("error_code", errorCode);
        command.Parameters.AddWithValue("error_message", errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkProjectionRunCompletedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        int rowsSeen,
        int rowsPublished,
        string replayFingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_place_projection_runs set status = 'completed', rows_seen = @rows_seen, rows_published = @rows_published, rows_failed = 0, replay_fingerprint = @replay_fingerprint, completed_at = now(), error_code = null, error_message = null where run_id = @run_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("rows_seen", rowsSeen);
        command.Parameters.AddWithValue("rows_published", rowsPublished);
        command.Parameters.AddWithValue("replay_fingerprint", replayFingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<Guid, PlaceReplayRegistryRow>> ReadReplayRegistryRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> placeIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select place_id, status, source_revision from aevo_place_registry where place_id = any(@place_ids) for update",
            connection,
            transaction);
        command.Parameters.Add("place_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = placeIds.ToArray();
        var rows = new Dictionary<Guid, PlaceReplayRegistryRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetGuid(0)] = new PlaceReplayRegistryRow(
                reader.GetString(1).Trim().ToLowerInvariant(),
                reader.GetString(2));
        }
        return rows;
    }

    private static PlaceProjectionReplayResponseContract ToReplayResponse(
        Guid runId,
        PlaceProjectionReplayReport report,
        string status,
        bool success,
        int publishedCount,
        int failedCount,
        int missingPlaceCount,
        int rejectedPlaceCount,
        string? failureCode,
        bool alreadyApplied,
        string requestId,
        IEnumerable<PlaceProjectionReplayFailure> failures)
    {
        return new PlaceProjectionReplayResponseContract(
            success,
            runId,
            status,
            report.InputCount,
            publishedCount,
            failedCount,
            missingPlaceCount,
            rejectedPlaceCount,
            failureCode,
            report.ReplayFingerprint,
            report.IsDeterministic,
            report.IsWithinPayloadBudget,
            alreadyApplied,
            failures.Select(failure => new PlaceProjectionReplayFailureContract(failure.Label, failure.Code, failure.Message)).ToArray(),
            requestId);
    }

    private sealed record PlaceProjectionRunState(
        string Status,
        string? ReplayFingerprint,
        int RowsPublished,
        int RowsFailed);

    private sealed record PlaceReplayRegistryRow(
        string Status,
        string SourceRevision);
}
