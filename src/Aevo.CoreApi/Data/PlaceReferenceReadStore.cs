using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed partial class CoreDataStore
{
    /// <summary>
    /// Loads the explicit compatibility mappings and the bounded redirect
    /// graph needed by the pure resolver in one transaction. Callers receive
    /// one result per input reference, including unresolved references; an
    /// unresolved result is never inferred from names, slugs, or coordinates.
    /// </summary>
    internal async Task<IReadOnlyList<PlaceReferenceResolution>> ResolvePlaceReferencesAsync(
        IReadOnlyCollection<PlaceReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0) return Array.Empty<PlaceReferenceResolution>();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetPlaceAccessAsync(connection, transaction, cancellationToken);

        try
        {
            var legacyReferences = references
                .Where(reference => !string.Equals(
                    NormalizeReferenceNamespace(reference.Namespace),
                    PlaceReferenceResolver.CanonicalNamespace,
                    StringComparison.Ordinal))
                .Where(reference => !string.IsNullOrWhiteSpace(reference.Namespace)
                    && !string.IsNullOrWhiteSpace(reference.ExternalId))
                .Select(reference => new
                {
                    Namespace = NormalizeReferenceNamespace(reference.Namespace),
                    ExternalId = reference.ExternalId.Trim(),
                    SourceVersion = NormalizeReferenceVersion(reference.SourceVersion)
                })
                .Distinct()
                .ToArray();

            var mappings = new List<PlaceLegacyMappingObservation>(legacyReferences.Length);
            if (legacyReferences.Length > 0)
            {
                var namespaces = legacyReferences.Select(reference => reference.Namespace).Distinct(StringComparer.Ordinal).ToArray();
                var externalIds = legacyReferences.Select(reference => reference.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
                var sourceVersions = legacyReferences.Select(reference => reference.SourceVersion).Distinct(StringComparer.Ordinal).ToArray();

                await using var mappingCommand = new NpgsqlCommand(
                    """
                    select namespace, external_id, source_version, place_id,
                           match_status, match_reason, match_confidence
                    from aevo_place_legacy_mappings
                    where lower(namespace) = any(@namespaces)
                      and external_id = any(@external_ids)
                      and source_version = any(@source_versions)
                    """,
                    connection,
                    transaction)
                {
                    CommandTimeout = 2
                };
                mappingCommand.Parameters.Add("namespaces", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = namespaces;
                mappingCommand.Parameters.Add("external_ids", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = externalIds;
                mappingCommand.Parameters.Add("source_versions", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = sourceVersions;

                {
                    await using var mappingReader = await mappingCommand.ExecuteReaderAsync(cancellationToken);
                    while (await mappingReader.ReadAsync(cancellationToken))
                    {
                        mappings.Add(new PlaceLegacyMappingObservation(
                            mappingReader.GetString(0),
                            mappingReader.GetString(1),
                            mappingReader.GetString(2),
                            mappingReader.IsDBNull(3) ? null : mappingReader.GetGuid(3),
                            mappingReader.GetString(4),
                            mappingReader.IsDBNull(5) ? null : mappingReader.GetString(5),
                            mappingReader.IsDBNull(6) ? null : mappingReader.GetDecimal(6)));
                    }
                }
            }

            var seedPlaceIds = references
                .Where(reference => string.Equals(
                    NormalizeReferenceNamespace(reference.Namespace),
                    PlaceReferenceResolver.CanonicalNamespace,
                    StringComparison.Ordinal))
                .Select(reference => Guid.TryParse(reference.ExternalId?.Trim(), out var placeId) ? placeId : Guid.Empty)
                .Where(placeId => placeId != Guid.Empty)
                .ToHashSet();
            foreach (var mapping in mappings.Where(mapping => mapping.PlaceId is not null))
            {
                seedPlaceIds.Add(mapping.PlaceId!.Value);
            }

            var redirects = new Dictionary<Guid, Guid>();
            var redirectReasons = new Dictionary<Guid, string>();
            if (seedPlaceIds.Count > 0)
            {
                await using var redirectCommand = new NpgsqlCommand(
                    """
                    with recursive redirect_graph as (
                      select source_place_id, target_place_id, reason, 1 as depth
                      from aevo_place_redirects
                      where status = 'active'
                        and source_place_id = any(@seed_place_ids)
                      union all
                      select next_redirect.source_place_id,
                             next_redirect.target_place_id,
                             next_redirect.reason,
                             redirect_graph.depth + 1
                      from aevo_place_redirects next_redirect
                      join redirect_graph
                        on next_redirect.source_place_id = redirect_graph.target_place_id
                      where next_redirect.status = 'active'
                        and redirect_graph.depth < @max_depth
                    )
                    select source_place_id, target_place_id, reason
                    from redirect_graph
                    """,
                    connection,
                    transaction)
                {
                    CommandTimeout = 2
                };
                redirectCommand.Parameters.Add("seed_place_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = seedPlaceIds.ToArray();
                redirectCommand.Parameters.AddWithValue("max_depth", PlaceApiContract.MaxRedirectHops + 1);

                await using var redirectReader = await redirectCommand.ExecuteReaderAsync(cancellationToken);
                while (await redirectReader.ReadAsync(cancellationToken))
                {
                    var sourcePlaceId = redirectReader.GetGuid(0);
                    if (redirects.ContainsKey(sourcePlaceId)) continue;
                    redirects[sourcePlaceId] = redirectReader.GetGuid(1);
                    redirectReasons[sourcePlaceId] = redirectReader.GetString(2);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            var resolver = new PlaceReferenceResolver(mappings, redirects, redirectReasons);
            return references.Select(resolver.Resolve).ToArray();
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place reference state is not available.", error);
        }
    }

    private static string NormalizeReferenceNamespace(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string NormalizeReferenceVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? PlaceCompatibilityMapper.Unversioned : value.Trim();
}
