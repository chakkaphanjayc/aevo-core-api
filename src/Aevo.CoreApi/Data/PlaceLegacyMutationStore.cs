using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;

namespace Aevo.CoreApi.Data;

public sealed partial class CoreDataStore
{
    public async Task<PlaceAdminLegacyDeleteResponseContract> DeleteAdminLegacyMappingAsync(
        Guid actorId,
        string? platformRole,
        string requestId,
        PlaceAdminLegacyMappingDeleteRequestContract request,
        CancellationToken cancellationToken)
    {
        PlaceRegistryMutationValidation.ValidateLegacyMappingDelete(request);
        var @namespace = request.Namespace.Trim();
        var externalId = request.ExternalId.Trim();
        var sourceVersion = request.SourceVersion.Trim();
        var recordKey = $"{@namespace}:{externalId}:{sourceVersion}";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);

        try
        {
            await using var read = new NpgsqlCommand(
                """
                select place_id, match_status, match_reason, match_confidence,
                       mapping_revision, observed_at, updated_at
                from aevo_place_legacy_mappings
                where namespace = @namespace
                  and external_id = @external_id
                  and source_version = @source_version
                for update
                """,
                connection,
                transaction);
            read.Parameters.AddWithValue("namespace", @namespace);
            read.Parameters.AddWithValue("external_id", externalId);
            read.Parameters.AddWithValue("source_version", sourceVersion);

            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new PlaceMutationNotFoundException("The legacy mapping does not exist.");
            }

            var placeId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            var matchStatus = reader.GetString(1);
            var wasLinked = placeId is not null || string.Equals(matchStatus, "linked", StringComparison.OrdinalIgnoreCase);
            if (wasLinked && !request.AllowLinked)
            {
                throw new PlaceMutationConflictException("The legacy mapping is linked; set allowLinked=true for an explicit test purge.");
            }

            var before = JsonSerializer.SerializeToElement(
                new
                {
                    @namespace,
                    externalId,
                    sourceVersion,
                    placeId,
                    matchStatus,
                    matchReason = reader.IsDBNull(2) ? null : reader.GetString(2),
                    matchConfidence = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                    mappingRevision = reader.GetInt64(4),
                    observedAt = reader.GetFieldValue<DateTimeOffset>(5),
                    updatedAt = reader.GetFieldValue<DateTimeOffset>(6)
                },
                PlaceWriteJsonOptions);
            await reader.CloseAsync();

            await using var delete = new NpgsqlCommand(
                """
                delete from aevo_place_legacy_mappings
                where namespace = @namespace
                  and external_id = @external_id
                  and source_version = @source_version
                """,
                connection,
                transaction);
            delete.Parameters.AddWithValue("namespace", @namespace);
            delete.Parameters.AddWithValue("external_id", externalId);
            delete.Parameters.AddWithValue("source_version", sourceVersion);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PlaceMutationNotFoundException("The legacy mapping does not exist.");
            }

            var after = JsonSerializer.SerializeToElement(
                new { deleted = true, recordKind = "legacy_mapping", recordKey, placeId, wasLinked },
                PlaceWriteJsonOptions);
            await InsertAuditAsync(
                connection,
                transaction,
                actorId,
                "ADMIN",
                "PLACE_LEGACY_MAPPING_DELETED",
                "place_legacy_mapping",
                recordKey,
                request.Reason.Trim(),
                before,
                after,
                requestId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PlaceAdminLegacyDeleteResponseContract(
                true,
                "legacy_mapping",
                recordKey,
                @namespace,
                externalId,
                sourceVersion,
                placeId,
                wasLinked,
                requestId);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Legacy mapping deletion failed.", error);
        }
    }

    public async Task<PlaceAdminLegacyDeleteResponseContract> DeleteAdminSourceLinkAsync(
        Guid actorId,
        string? platformRole,
        string requestId,
        Guid sourceLinkId,
        PlaceAdminSourceLinkDeleteRequestContract request,
        CancellationToken cancellationToken)
    {
        PlaceRegistryMutationValidation.ValidateSourceLinkDelete(request);
        if (sourceLinkId == Guid.Empty) throw new PlaceMutationRequestException("SOURCE_LINK_ID_INVALID", "Source link id must be a non-empty UUID.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);

        try
        {
            await using var read = new NpgsqlCommand(
                """
                select place_id, source_kind, namespace, external_id, source_version,
                       match_status, match_confidence, source_url, license, attribution_text,
                       first_observed_at, last_observed_at, source_record_hash,
                       raw_snapshot_ref, provenance::text, created_at, updated_at
                from aevo_place_source_links
                where source_link_id = @source_link_id
                for update
                """,
                connection,
                transaction);
            read.Parameters.AddWithValue("source_link_id", sourceLinkId);

            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new PlaceMutationNotFoundException("The legacy source link does not exist.");
            }

            var placeId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            var sourceKind = reader.GetString(1);
            var @namespace = reader.GetString(2);
            var externalId = reader.GetString(3);
            var sourceVersion = reader.GetString(4);
            var matchStatus = reader.GetString(5);
            var wasLinked = placeId is not null || string.Equals(matchStatus, "linked", StringComparison.OrdinalIgnoreCase);
            if (wasLinked && !request.AllowLinked)
            {
                throw new PlaceMutationConflictException("The legacy source link is linked; set allowLinked=true for an explicit test purge.");
            }

            var before = JsonSerializer.SerializeToElement(
                new
                {
                    sourceLinkId,
                    placeId,
                    sourceKind,
                    @namespace,
                    externalId,
                    sourceVersion,
                    matchStatus,
                    matchConfidence = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
                    sourceUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                    license = reader.IsDBNull(8) ? null : reader.GetString(8),
                    attributionText = reader.IsDBNull(9) ? null : reader.GetString(9),
                    firstObservedAt = reader.GetFieldValue<DateTimeOffset>(10),
                    lastObservedAt = reader.GetFieldValue<DateTimeOffset>(11),
                    sourceRecordHash = reader.IsDBNull(12) ? null : reader.GetString(12),
                    rawSnapshotRef = reader.IsDBNull(13) ? null : reader.GetString(13),
                    provenance = reader.IsDBNull(14) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(14)),
                    createdAt = reader.GetFieldValue<DateTimeOffset>(15),
                    updatedAt = reader.GetFieldValue<DateTimeOffset>(16)
                },
                PlaceWriteJsonOptions);
            await reader.CloseAsync();

            await using var delete = new NpgsqlCommand(
                "delete from aevo_place_source_links where source_link_id = @source_link_id",
                connection,
                transaction);
            delete.Parameters.AddWithValue("source_link_id", sourceLinkId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new PlaceMutationNotFoundException("The legacy source link does not exist.");
            }

            var recordKey = sourceLinkId.ToString("D");
            var after = JsonSerializer.SerializeToElement(
                new { deleted = true, recordKind = "source_link", recordKey, placeId, wasLinked },
                PlaceWriteJsonOptions);
            await InsertAuditAsync(
                connection,
                transaction,
                actorId,
                "ADMIN",
                "PLACE_SOURCE_LINK_DELETED",
                "place_source_link",
                recordKey,
                request.Reason.Trim(),
                before,
                after,
                requestId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PlaceAdminLegacyDeleteResponseContract(
                true,
                "source_link",
                recordKey,
                @namespace,
                externalId,
                sourceVersion,
                placeId,
                wasLinked,
                requestId);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Legacy source-link deletion failed.", error);
        }
    }
}
