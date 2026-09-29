using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

internal sealed record PlaceProjectionRow(
    Guid PlaceId,
    string SortKey,
    double? QueryDistanceMeters,
    string ProjectionVersion,
    string SourceRevision,
    string FreshnessState,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ExpiresAt,
    JsonElement Payload,
    DateTimeOffset GeneratedAt);

internal sealed record PlaceRedirectLookup(
    PlaceReferenceResolution Resolution);

public sealed partial class CoreDataStore
{
    internal async Task<PlaceRedirectLookup> ResolvePlaceRedirectAsync(
        Guid requestedPlaceId,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolvePlaceReferencesAsync(
            new[]
            {
                new PlaceReference(
                    PlaceReferenceSurface.Url,
                    PlaceReferenceResolver.CanonicalNamespace,
                    requestedPlaceId.ToString("D"))
            },
            cancellationToken);
        return new PlaceRedirectLookup(resolution[0]);
    }

    internal async Task<IReadOnlyList<PlaceProjectionRow>> ReadPlaceProjectionRowsAsync(
        string? query,
        string? area,
        PlaceBoundsContract? bounds,
        IReadOnlyList<string>? categoryIds,
        int limit,
        PlaceCursorAnchor? after,
        PlaceGeoPointContract? distanceOrigin,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? savedPlaceIds = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetPlaceAccessAsync(connection, transaction, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            with candidates as (
              select
                p.place_id,
                lower(r.name) as sort_key,
                case when @has_origin then
                  6371000 * 2 * asin(least(1.0, sqrt(
                    power(sin(radians(coalesce(r.display_latitude, r.centroid_latitude) - @origin_latitude) / 2), 2)
                    + cos(radians(@origin_latitude)) * cos(radians(coalesce(r.display_latitude, r.centroid_latitude)))
                    * power(sin(radians(coalesce(r.display_longitude, r.centroid_longitude) - @origin_longitude) / 2), 2)
                  )))
                else null end as query_distance_meters,
                case when p.projection_status = 'failed' and p.last_known_valid_projection_version is not null
                  then p.last_known_valid_projection_version else p.projection_version end as projection_version,
                p.source_revision,
                case when p.projection_status = 'failed' then 'stale' else p.freshness_state end as freshness_state,
                p.observed_at,
                p.expires_at,
                case when p.projection_status = 'failed' and p.last_known_valid_payload is not null
                  then p.last_known_valid_payload else p.payload end as payload,
                p.generated_at
              from aevo_place_public_projections p
              join aevo_place_registry r on r.place_id = p.place_id
              where r.status in ('visible', 'limited')
                and p.projection_status in ('active', 'stale', 'failed')
                and (p.projection_status <> 'failed' or p.last_known_valid_payload is not null)
                and (@query is null or lower(r.name) like '%' || lower(@query) || '%' or lower(r.slug) like '%' || lower(@query) || '%')
                and (@area is null or lower(coalesce(r.address->>'neighborhood', '')) = lower(@area) or lower(coalesce(r.address->>'locality', '')) = lower(@area))
                and (@category_ids is null or r.category->>'id' = any(@category_ids))
                and (@saved_only = false or p.place_id = any(@saved_place_ids))
                and (@has_bounds = false or (
                  coalesce(r.display_latitude, r.centroid_latitude) is not null
                  and coalesce(r.display_longitude, r.centroid_longitude) is not null
                  and coalesce(r.display_longitude, r.centroid_longitude) >= @west
                  and coalesce(r.display_longitude, r.centroid_longitude) <= @east
                  and coalesce(r.display_latitude, r.centroid_latitude) >= @south
                  and coalesce(r.display_latitude, r.centroid_latitude) <= @north
                ))
                and (@has_origin = false or (coalesce(r.display_latitude, r.centroid_latitude) is not null and coalesce(r.display_longitude, r.centroid_longitude) is not null))
            )
            select place_id, sort_key, query_distance_meters, projection_version,
                   source_revision, freshness_state, observed_at, expires_at,
                   payload, generated_at
            from candidates
            where @cursor_surface is null
              or (@cursor_surface = 'search' and (
                sort_key > @after_sort_key
                or (sort_key = @after_sort_key and place_id > @after_place_id)
              ))
              or (@cursor_surface = 'nearby' and (
                query_distance_meters > @after_distance_meters
                or (query_distance_meters = @after_distance_meters and place_id > @after_place_id)
              ))
            order by query_distance_meters nulls last, sort_key asc, place_id asc
            limit @limit
            """,
            connection,
            transaction)
        {
            CommandTimeout = 2
        };
        AddNullableTextParameter(command, "query", query);
        AddNullableTextParameter(command, "area", area);
        command.Parameters.Add("category_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = categoryIds is null ? DBNull.Value : categoryIds.ToArray();
        command.Parameters.AddWithValue("has_bounds", bounds is not null);
        AddNullableDoubleParameter(command, "west", bounds?.West);
        AddNullableDoubleParameter(command, "south", bounds?.South);
        AddNullableDoubleParameter(command, "east", bounds?.East);
        AddNullableDoubleParameter(command, "north", bounds?.North);
        command.Parameters.AddWithValue("has_origin", distanceOrigin is not null);
        AddNullableDoubleParameter(command, "origin_longitude", distanceOrigin?.Longitude);
        AddNullableDoubleParameter(command, "origin_latitude", distanceOrigin?.Latitude);
        command.Parameters.AddWithValue("saved_only", savedPlaceIds is not null);
        command.Parameters.Add(new NpgsqlParameter("saved_place_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = savedPlaceIds is null ? DBNull.Value : savedPlaceIds.Distinct().ToArray()
        });
        AddNullableTextParameter(command, "cursor_surface", after?.Surface);
        AddNullableTextParameter(command, "after_sort_key", after?.SortKey);
        command.Parameters.Add(new NpgsqlParameter("after_place_id", NpgsqlDbType.Uuid) { Value = (object?)after?.PlaceId ?? DBNull.Value });
        AddNullableDoubleParameter(command, "after_distance_meters", after?.DistanceMeters);
        command.Parameters.AddWithValue("limit", limit);

        var rows = new List<PlaceProjectionRow>(limit);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadProjectionRow(reader));
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return rows;
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place public projection is not available.", error);
        }
    }

    internal async Task<PlaceProjectionRow?> ReadPlaceProjectionDetailAsync(
        Guid placeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetPlaceAccessAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              p.place_id,
              lower(r.name) as sort_key,
              null::double precision as query_distance_meters,
              case when p.projection_status = 'failed' and p.last_known_valid_projection_version is not null
                then p.last_known_valid_projection_version else p.projection_version end as projection_version,
              p.source_revision,
              case when p.projection_status = 'failed' then 'stale' else p.freshness_state end as freshness_state,
              p.observed_at,
              p.expires_at,
              case when p.projection_status = 'failed' and p.last_known_valid_payload is not null
                then p.last_known_valid_payload else p.payload end as payload,
              p.generated_at
            from aevo_place_public_projections p
            join aevo_place_registry r on r.place_id = p.place_id
            where p.place_id = @place_id
              and r.status in ('visible', 'limited')
              and p.projection_status in ('active', 'stale', 'failed')
              and (p.projection_status <> 'failed' or p.last_known_valid_payload is not null)
            limit 1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("place_id", placeId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var row = await reader.ReadAsync(cancellationToken) ? ReadProjectionRow(reader) : null;
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return row;
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place public projection is not available.", error);
        }
    }

    private static async Task SetPlaceAccessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('aevo.place_access', 'internal', true)",
            connection,
            transaction);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static PlaceProjectionRow ReadProjectionRow(NpgsqlDataReader reader)
    {
        var rawPayload = reader.GetString(8);
        try
        {
            using var document = JsonDocument.Parse(rawPayload);
            return new PlaceProjectionRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<double>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                document.RootElement.Clone(),
                reader.GetFieldValue<DateTimeOffset>(9));
        }
        catch (JsonException error)
        {
            throw new CoreDatabaseException("Place public projection payload is invalid.", error);
        }
    }

    private static void AddNullableTextParameter(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
    }

    private static void AddNullableDoubleParameter(NpgsqlCommand command, string name, double? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Double) { Value = (object?)value ?? DBNull.Value });
    }
}
