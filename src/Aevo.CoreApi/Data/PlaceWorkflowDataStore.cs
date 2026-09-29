using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed partial class CoreDataStore
{
    public async Task<IReadOnlyList<AdminPlaceWorkflowRecord>> ListAdminPlaceWorkflowsAsync(
        int limit,
        string? aggregateType,
        string? status,
        CancellationToken cancellationToken)
    {
        var page = await ListAdminPlaceWorkflowsPageAsync(limit, 0, aggregateType, status, cancellationToken);
        return page.Items;
    }

    public async Task<AdminDirectoryPage<AdminPlaceWorkflowRecord>> ListAdminPlaceWorkflowsPageAsync(
        int limit,
        int offset,
        string? aggregateType,
        string? status,
        CancellationToken cancellationToken)
        => await ListAdminPlaceWorkflowsPageAsync(limit, offset, aggregateType, status, null, "submittedAt", "desc", cancellationToken);

    public async Task<AdminDirectoryPage<AdminPlaceWorkflowRecord>> ListAdminPlaceWorkflowsPageAsync(
        int limit,
        int offset,
        string? aggregateType,
        string? status,
        string? query,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetPlaceAccessAsync(connection, transaction, cancellationToken);
        var orderBy = WorkflowOrderBy(sort, direction);
        await using var command = new NpgsqlCommand(
            $"""
            with workflows as (
              select
                'claim'::text as aggregate_type,
                c.claim_id as workflow_id,
                c.place_id,
                c.organization_id,
                c.submitted_by as actor_id,
                c.status,
                c.submitted_at,
                c.reviewed_at,
                (select count(*)::integer from aevo_place_claim_evidence evidence where evidence.claim_id = c.claim_id and evidence.revoked_at is null) as evidence_count,
                c.requested_fields,
                jsonb_build_object(
                  'businessId', c.business_id,
                  'branchId', c.branch_id,
                  'evidenceStatus', c.evidence_status,
                  'reviewReason', c.review_reason
                ) as summary
              from aevo_place_claims c
              union all
              select
                'submission'::text as aggregate_type,
                s.submission_id as workflow_id,
                s.place_id,
                null::uuid as organization_id,
                s.submitter_id as actor_id,
                s.status,
                s.submitted_at,
                s.reviewed_at,
                case when jsonb_typeof(s.evidence) = 'array' then jsonb_array_length(s.evidence) else 0 end as evidence_count,
                jsonb_build_array(s.submission_type) as requested_fields,
                jsonb_build_object(
                  'submissionType', s.submission_type,
                  'appliedRevision', s.applied_revision,
                  'reviewReason', s.review_reason
                ) as summary
              from aevo_place_submissions s
              union all
              select
                'relationship'::text as aggregate_type,
                r.relationship_id as workflow_id,
                r.place_id,
                r.organization_id,
                r.created_by as actor_id,
                r.status,
                r.created_at as submitted_at,
                r.updated_at as reviewed_at,
                0::integer as evidence_count,
                jsonb_build_array(r.relationship_type) as requested_fields,
                jsonb_build_object(
                  'relationshipType', r.relationship_type,
                  'targetId', coalesce(r.business_id, r.branch_id, r.store_id, r.venue_id),
                  'isPrimary', r.is_primary,
                  'verificationStatus', r.verification_status
                ) as summary
              from aevo_place_relationships r
            )
            select aggregate_type, workflow_id, place_id, organization_id, actor_id,
                   status, submitted_at, reviewed_at, evidence_count,
                   requested_fields, summary
            from workflows
            where (@aggregate_type is null or aggregate_type = @aggregate_type)
              and (@status is null or status = @status)
              and (@query is null or workflow_id::text like '%' || lower(@query) || '%' or coalesce(place_id::text, '') like '%' || lower(@query) || '%' or lower(summary::text) like '%' || lower(@query) || '%')
            order by {orderBy}
            limit @limit_plus_one offset @offset
            """,
            connection,
            transaction)
        {
            CommandTimeout = 2
        };
        command.Parameters.Add(new NpgsqlParameter("aggregate_type", NpgsqlDbType.Text) { Value = (object?)aggregateType ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)query?.Trim().ToLowerInvariant() ?? DBNull.Value });
        var pageSize = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        command.Parameters.AddWithValue("offset", Math.Max(offset, 0));

        var result = new List<AdminPlaceWorkflowRecord>();
        try
        {
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new AdminPlaceWorkflowRecord(
                        reader.GetGuid(1),
                        reader.GetString(0),
                        NullableGuid(reader, 2),
                        NullableGuid(reader, 3),
                        NullableGuid(reader, 4),
                        reader.GetString(5),
                        reader.GetFieldValue<DateTimeOffset>(6),
                        NullableDateTimeOffset(reader, 7),
                        reader.GetInt32(8),
                        ReadStringArray(reader.GetString(9)),
                        JsonValue(reader, 10) ?? JsonSerializer.SerializeToElement(new { })));
                }
            }
            await transaction.CommitAsync(cancellationToken);
            var hasMore = result.Count > pageSize;
            if (hasMore) result.RemoveAt(result.Count - 1);
            return new AdminDirectoryPage<AdminPlaceWorkflowRecord>(result, hasMore);
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("Place workflow queue is not available.", error);
        }
    }

    private static string WorkflowOrderBy(string? sort, string? direction)
    {
        var column = sort switch
        {
            "workflow" => "aggregate_type",
            "status" => "status",
            "evidence" => "evidence_count",
            "submitted" or "submittedAt" => "submitted_at",
            _ => "submitted_at"
        };
        var orderDirection = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        return $"{column} {orderDirection}, workflow_id asc";
    }

    private static string[] ReadStringArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
