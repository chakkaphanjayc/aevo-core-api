using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed class PlaceMutationConflictException(string message) : Exception(message);

public sealed class PlaceMutationNotFoundException(string message) : Exception(message);

public sealed partial class CoreDataStore
{
    private static readonly JsonSerializerOptions PlaceWriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PlaceAdminPlaceResourceContract?> ReadAdminPlaceAsync(
        Guid actorId,
        string? platformRole,
        Guid placeId,
        CancellationToken cancellationToken)
    {
        if (placeId == Guid.Empty) throw new PlaceMutationRequestException("PLACE_ID_INVALID", "Place id must be a non-empty UUID.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.revision,
                   r.source_revision,
                   r.canonical_geometry,
                   r.bounding_geometry,
                   rev.source_kind,
                   rev.source_id,
                   rev.evidence_reference,
                   rev.snapshot->'summary' as summary,
                   p.projection_version,
                   p.projection_status,
                   p.freshness_state,
                   p.generated_at
            from aevo_place_registry r
            left join lateral (
              select source_kind, source_id, evidence_reference, snapshot
              from aevo_place_revisions
              where place_id = r.place_id
              order by revision_number desc
              limit 1
            ) rev on true
            left join aevo_place_public_projections p on p.place_id = r.place_id
            where r.place_id = @place_id
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);

        try
        {
            PlaceAdminPlaceResourceContract? result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    result = null;
                }
                else
                {
                    var summaryJson = reader.IsDBNull(7) ? null : reader.GetString(7);
                    if (string.IsNullOrWhiteSpace(summaryJson))
                    {
                        throw new CoreDatabaseException("The Place has no editable canonical revision snapshot.");
                    }

                    var summary = JsonSerializer.Deserialize<PlaceSummaryContract>(summaryJson, PlaceWriteJsonOptions)
                        ?? throw new CoreDatabaseException("The Place canonical revision snapshot is invalid.");
                    var canonicalGeometry = DeserializeNullableJson<PlaceGeometryContract>(reader, 2);
                    var boundingGeometry = DeserializeNullableJson<PlaceGeometryContract>(reader, 3);
                    result = new PlaceAdminPlaceResourceContract(
                        true,
                        placeId,
                        reader.GetInt64(0),
                        summary,
                        canonicalGeometry,
                        boundingGeometry,
                        reader.IsDBNull(4) ? "aevo_admin" : reader.GetString(4),
                        NullableString(reader, 5),
                        NullableString(reader, 6),
                        reader.GetString(1),
                        NullableString(reader, 8),
                        NullableString(reader, 9),
                        NullableString(reader, 10),
                        NullableDateTimeOffset(reader, 11));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place admin detail is not available.", error);
        }
    }

    public async Task<PlaceAdminMutationResponseContract> UpsertAdminPlaceAsync(
        Guid actorId,
        string? platformRole,
        string requestId,
        PlaceAdminPlaceMutationRequestContract request,
        CancellationToken cancellationToken)
    {
        var placeId = PlaceRegistryMutationValidation.ValidateUpsert(request);
        var summary = request.Summary with
        {
            Id = placeId.ToString("D"),
            Slug = request.Summary.Slug.Trim(),
            Name = request.Summary.Name.Trim(),
            Status = request.Summary.Status.Trim().ToLowerInvariant()
        };
        var projectionVersion = string.IsNullOrWhiteSpace(request.ProjectionVersion)
            ? "places-public-v1"
            : request.ProjectionVersion.Trim();
        var sourceRevision = request.SourceRevision.Trim();
        var observedAt = DateTimeOffset.UtcNow;
        var shouldPublish = summary.Status is not ("removed" or "merged");
        var projection = shouldPublish
            ? PlaceProjectionBuilder.Build(summary, projectionVersion, sourceRevision, observedAt, TimeSpan.FromMinutes(5))
            : null;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);

        try
        {
            var current = await ReadPlaceStateAsync(connection, transaction, placeId, cancellationToken);
            if (request.ExpectedRevision is null)
            {
                if (current is not null) throw new PlaceMutationConflictException("The Place already exists; an expected revision is required to edit it.");
            }
            else if (current is null || current.Revision != request.ExpectedRevision.Value)
            {
                throw new PlaceMutationConflictException("The Place revision is stale; reload the registry before editing.");
            }

            var revision = (current?.Revision ?? 0) + 1;
            if (current is null)
            {
                await InsertPlaceAsync(connection, transaction, summary, request, revision, cancellationToken);
            }
            else
            {
                await UpdatePlaceAsync(connection, transaction, summary, request, revision, cancellationToken);
            }

            var snapshot = JsonSerializer.SerializeToElement(
                new
                {
                    summary,
                    canonicalGeometry = request.CanonicalGeometry,
                    boundingGeometry = request.BoundingGeometry,
                    sourceRevision,
                    projectionVersion
                },
                PlaceWriteJsonOptions);
            await InsertRevisionAsync(
                connection,
                transaction,
                placeId,
                revision,
                current is null ? "created" : "updated",
                snapshot,
                request.SourceKind.Trim().ToLowerInvariant(),
                request.SourceId,
                request.EvidenceReference,
                actorId,
                cancellationToken);

            if (projection is null)
            {
                await DisableProjectionAsync(connection, transaction, placeId, request.Reason.Trim(), cancellationToken);
            }
            else
            {
                await UpsertProjectionAsync(connection, transaction, projection, cancellationToken);
            }

            var after = JsonSerializer.SerializeToElement(
                new { placeId, revision, status = summary.Status, projectionVersion, sourceRevision },
                PlaceWriteJsonOptions);
            await InsertAuditAsync(
                connection,
                transaction,
                actorId,
                "ADMIN",
                current is null ? "PLACE_CREATED" : "PLACE_UPDATED",
                "place",
                placeId.ToString("D"),
                request.Reason.Trim(),
                current is null ? null : current.ToAuditState(placeId),
                after,
                requestId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PlaceAdminMutationResponseContract(
                true,
                placeId,
                revision,
                current is null ? "created" : "updated",
                summary.Status,
                projectionVersion,
                sourceRevision,
                projection is not null,
                requestId);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place registry mutation failed.", error);
        }
    }

    public async Task<PlaceAdminMutationResponseContract> SoftDeleteAdminPlaceAsync(
        Guid actorId,
        string? platformRole,
        string requestId,
        Guid placeId,
        PlaceAdminDeleteRequestContract request,
        CancellationToken cancellationToken)
    {
        PlaceRegistryMutationValidation.ValidateDelete(request);
        if (placeId == Guid.Empty) throw new PlaceMutationRequestException("PLACE_ID_INVALID", "Place id must be a non-empty UUID.");

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ApplyAdminPlaceContextAsync(connection, transaction, actorId, platformRole, cancellationToken);

        try
        {
            var current = await ReadPlaceStateAsync(connection, transaction, placeId, cancellationToken);
            if (current is null) throw new PlaceMutationNotFoundException("The Place does not exist.");
            if (current.Revision != request.ExpectedRevision!.Value)
            {
                throw new PlaceMutationConflictException("The Place revision is stale; reload the registry before deleting it.");
            }

            var revision = current.Revision + 1;
            await using (var update = new NpgsqlCommand(
                "update aevo_place_registry set status = 'removed', revision = @revision, updated_at = now() where place_id = @place_id",
                connection,
                transaction))
            {
                update.Parameters.AddWithValue("revision", revision);
                update.Parameters.AddWithValue("place_id", placeId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new PlaceMutationNotFoundException("The Place does not exist.");
                }
            }

            var snapshot = JsonSerializer.SerializeToElement(
                new { placeId, revision, status = "removed", reason = request.Reason.Trim() },
                PlaceWriteJsonOptions);
            await InsertRevisionAsync(
                connection,
                transaction,
                placeId,
                revision,
                "updated",
                snapshot,
                "aevo_admin",
                placeId.ToString("D"),
                null,
                actorId,
                cancellationToken);
            await DisableProjectionAsync(connection, transaction, placeId, request.Reason.Trim(), cancellationToken);

            var after = JsonSerializer.SerializeToElement(
                new { placeId, revision, status = "removed", projectionStatus = "disabled" },
                PlaceWriteJsonOptions);
            await InsertAuditAsync(
                connection,
                transaction,
                actorId,
                "ADMIN",
                "PLACE_SOFT_DELETED",
                "place",
                placeId.ToString("D"),
                request.Reason.Trim(),
                current.ToAuditState(placeId),
                after,
                requestId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new PlaceAdminMutationResponseContract(
                true,
                placeId,
                revision,
                "soft_deleted",
                "removed",
                "disabled",
                current.SourceRevision,
                false,
                requestId);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place soft delete failed.", error);
        }
    }

    private static async Task ApplyAdminPlaceContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        string? platformRole,
        CancellationToken cancellationToken)
    {
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            actorId,
            null,
            null,
            "ADMIN",
            platformRole?.Trim().ToLowerInvariant(),
            cancellationToken);
        await using var access = new NpgsqlCommand(
            "select set_config('aevo.place_access', 'internal', true)",
            connection,
            transaction);
        await access.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<PlaceWriteState?> ReadPlaceStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid placeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select revision, status, source_revision from aevo_place_registry where place_id = @place_id for update",
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PlaceWriteState(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task InsertPlaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaceSummaryContract summary,
        PlaceAdminPlaceMutationRequestContract request,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_registry
              (place_id, slug, name, localized_names, category, status,
               canonical_geometry, display_longitude, display_latitude,
               label_longitude, label_latitude, centroid_longitude,
               centroid_latitude, bounding_geometry, address, parent_place_id,
               revision, source_revision, published_at)
            values
              (@place_id, @slug, @name, @localized_names, @category, @status,
               @canonical_geometry, @display_longitude, @display_latitude,
               @label_longitude, @label_latitude, @centroid_longitude,
               @centroid_latitude, @bounding_geometry, @address, @parent_place_id,
               @revision, @source_revision,
               case when @is_public then now() else null end)
            """,
            connection,
            transaction);
        AddRegistryParameters(command, summary, request, revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePlaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaceSummaryContract summary,
        PlaceAdminPlaceMutationRequestContract request,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update aevo_place_registry
            set slug = @slug,
                name = @name,
                localized_names = @localized_names,
                category = @category,
                status = @status,
                canonical_geometry = @canonical_geometry,
                display_longitude = @display_longitude,
                display_latitude = @display_latitude,
                label_longitude = @label_longitude,
                label_latitude = @label_latitude,
                centroid_longitude = @centroid_longitude,
                centroid_latitude = @centroid_latitude,
                bounding_geometry = @bounding_geometry,
                address = @address,
                parent_place_id = @parent_place_id,
                revision = @revision,
                source_revision = @source_revision,
                published_at = case when @is_public and published_at is null then now() else published_at end,
                updated_at = now()
            where place_id = @place_id
            """,
            connection,
            transaction);
        AddRegistryParameters(command, summary, request, revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PlaceMutationNotFoundException("The Place does not exist.");
        }
    }

    private static void AddRegistryParameters(
        NpgsqlCommand command,
        PlaceSummaryContract summary,
        PlaceAdminPlaceMutationRequestContract request,
        long revision)
    {
        command.Parameters.Add("place_id", NpgsqlDbType.Uuid).Value = Guid.Parse(summary.Id);
        command.Parameters.AddWithValue("slug", summary.Slug);
        command.Parameters.AddWithValue("name", summary.Name);
        command.Parameters.Add("localized_names", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(summary.LocalizedNames, PlaceWriteJsonOptions);
        command.Parameters.Add("category", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(summary.Category, PlaceWriteJsonOptions);
        command.Parameters.AddWithValue("status", summary.Status);
        AddNullableJson(command, "canonical_geometry", request.CanonicalGeometry);
        AddNullablePoint(command, "display_longitude", "display_latitude", summary.DisplayPoint);
        AddNullablePoint(command, "label_longitude", "label_latitude", summary.LabelPoint);
        AddNullablePoint(command, "centroid_longitude", "centroid_latitude", summary.DisplayPoint);
        AddNullableJson(command, "bounding_geometry", request.BoundingGeometry);
        AddNullableJson(command, "address", summary.Address);
        command.Parameters.Add("parent_place_id", NpgsqlDbType.Uuid).Value = ParseNullableGuid(summary.ParentPlaceId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("source_revision", request.SourceRevision.Trim());
        command.Parameters.AddWithValue("is_public", summary.Status is "visible" or "limited");
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid placeId,
        long revision,
        string operation,
        JsonElement snapshot,
        string sourceKind,
        string? sourceId,
        string? evidenceReference,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_revisions
              (place_id, revision_number, operation, changed_fields, snapshot,
               source_kind, source_id, evidence_reference, actor_id)
            values
              (@place_id, @revision_number, @operation, @changed_fields, @snapshot,
               @source_kind, @source_id, @evidence_reference, @actor_id)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);
        command.Parameters.AddWithValue("revision_number", revision);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.Add("changed_fields", NpgsqlDbType.Jsonb).Value = "[\"summary\"]";
        command.Parameters.Add("snapshot", NpgsqlDbType.Jsonb).Value = snapshot.GetRawText();
        command.Parameters.AddWithValue("source_kind", sourceKind);
        command.Parameters.AddWithValue("source_id", (object?)sourceId?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("evidence_reference", (object?)evidenceReference?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("actor_id", actorId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaceProjectionBuildResult projection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_place_public_projections
              (place_id, projection_version, source_revision, freshness_state,
               observed_at, expires_at, payload, projection_status,
               generated_at, rebuilt_at)
            values
              (@place_id, @projection_version, @source_revision, @freshness_state,
               @observed_at, @expires_at, @payload, 'active', now(), now())
            on conflict (place_id) do update set
              last_known_valid_projection_version = case
                when aevo_place_public_projections.projection_status in ('active', 'stale')
                then aevo_place_public_projections.projection_version
                else aevo_place_public_projections.last_known_valid_projection_version
              end,
              last_known_valid_payload = case
                when aevo_place_public_projections.projection_status in ('active', 'stale')
                then aevo_place_public_projections.payload
                else aevo_place_public_projections.last_known_valid_payload
              end,
              last_known_valid_at = case
                when aevo_place_public_projections.projection_status in ('active', 'stale')
                then now()
                else aevo_place_public_projections.last_known_valid_at
              end,
              projection_version = excluded.projection_version,
              source_revision = excluded.source_revision,
              freshness_state = excluded.freshness_state,
              observed_at = excluded.observed_at,
              expires_at = excluded.expires_at,
              payload = excluded.payload,
              projection_status = 'active',
              invalidation_reason = null,
              generated_at = now(),
              rebuilt_at = now(),
              invalidated_at = null
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", projection.PlaceId);
        command.Parameters.AddWithValue("projection_version", projection.ProjectionVersion);
        command.Parameters.AddWithValue("source_revision", projection.SourceRevision);
        command.Parameters.AddWithValue("freshness_state", projection.FreshnessState);
        command.Parameters.AddWithValue("observed_at", projection.ObservedAt);
        command.Parameters.AddWithValue("expires_at", projection.ExpiresAt);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = projection.Payload.GetRawText();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DisableProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid placeId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update aevo_place_public_projections set projection_status = 'disabled', freshness_state = 'stale', invalidation_reason = @reason, invalidated_at = now() where place_id = @place_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullableJson(NpgsqlCommand command, string name, object? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value = value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, PlaceWriteJsonOptions);
    }

    private static void AddNullablePoint(
        NpgsqlCommand command,
        string longitudeName,
        string latitudeName,
        PlaceGeoPointContract? point)
    {
        command.Parameters.Add(longitudeName, NpgsqlDbType.Double).Value = point?.Longitude ?? (object)DBNull.Value;
        command.Parameters.Add(latitudeName, NpgsqlDbType.Double).Value = point?.Latitude ?? (object)DBNull.Value;
    }

    private static object ParseNullableGuid(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : Guid.Parse(value);

    private static T? DeserializeNullableJson<T>(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        var raw = reader.GetString(ordinal);
        return JsonSerializer.Deserialize<T>(raw, PlaceWriteJsonOptions);
    }

    private sealed record PlaceWriteState(long Revision, string Status, string SourceRevision)
    {
        public JsonElement ToAuditState(Guid placeId) =>
            JsonSerializer.SerializeToElement(
                new { placeId, revision = Revision, status = Status, sourceRevision = SourceRevision },
                PlaceWriteJsonOptions);
    }
}
