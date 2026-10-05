using System.Diagnostics;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.QueryPlatform;
using Aevo.CoreApi.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed partial class CoreDataStore
{
    public async Task<QueryPlatformEntitlement> GetQueryPlatformEntitlementAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select feature_key, is_enabled, limit_value
            from aevo_organization_entitlements
            where organization_id = @organization_id
              and feature_key in ('query_platform', 'query_rows')
            """,
            connection);
        command.Parameters.AddWithValue("organization_id", organizationId);

        var platformEntitlementFound = false;
        var platformEnabled = false;
        int? rowLimit = null;
        var rowLimitDisabled = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                switch (reader.GetString(0))
                {
                    case "query_platform":
                        platformEntitlementFound = true;
                        platformEnabled = reader.GetBoolean(1);
                        break;
                    case "query_rows":
                        rowLimit = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                        rowLimitDisabled = !reader.GetBoolean(1);
                        break;
                }
            }
        }

        if (!platformEntitlementFound)
        {
            return new QueryPlatformEntitlement(false, null, "ENTITLEMENT_REQUIRED");
        }

        if (!platformEnabled)
        {
            return new QueryPlatformEntitlement(false, null, "ENTITLEMENT_INACTIVE");
        }

        return new QueryPlatformEntitlement(
            true,
            rowLimitDisabled ? 0 : rowLimit,
            rowLimitDisabled ? "ENTITLEMENT_LIMIT_EXCEEDED" : "ALLOWED");
    }

    public Task<QueryExecutionResponse> ExecuteQueryPlanAsync(
        HubPrincipalRecord principal,
        Guid? sessionStoreId,
        QueryPlan plan,
        int effectiveLimit,
        string requestId,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "hub.query.execute",
            () => ExecuteQueryPlanFromDatabaseAsync(principal, sessionStoreId, plan, effectiveLimit, requestId, cancellationToken));

    private async Task<QueryExecutionResponse> ExecuteQueryPlanFromDatabaseAsync(
        HubPrincipalRecord principal,
        Guid? sessionStoreId,
        QueryPlan plan,
        int effectiveLimit,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(plan.Model.TechnicalName, "stores", StringComparison.Ordinal)
            || effectiveLimit is < 1 or > 200)
        {
            throw new CoreDatabaseException("The query plan is outside the registered execution boundary.");
        }

        var fields = plan.Fields.Select(field => QueryPlatformModelCatalog.GetField(field.Path)).ToArray();
        var projection = string.Join(", ", fields.Select((field, index) => $"{field.SqlExpression} as query_column_{index}"));
        var sql = $"""
            select {projection}
            from public.stores s
            where s.organization_id = @organization_id
              and (
                s.status = 'ACTIVE'
                or exists (
                  select 1
                  from aevo_data_deletion_requests deletion
                  where deletion.organization_id = s.organization_id
                    and deletion.resource_type = 'STORE'
                    and deletion.resource_id = s.id
                    and deletion.status = 'PENDING'
                )
              )
              and (@session_store_id is null or s.id = @session_store_id)
              and (
                @global_access
                or exists (
                  select 1
                  from public.membership_stores ms
                  join public.memberships m on m.id = ms.membership_id
                  where ms.store_id = s.id
                    and m.id = @membership_id
                    and m.user_id = @user_id
                    and m.organization_id = @organization_id
                    and m.status = 'ACTIVE'
                )
              )
              and ({plan.WhereSql})
            order by {plan.OrderSql}
            limit @query_limit
            offset @query_offset
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var rows = new List<IReadOnlyDictionary<string, object?>>(effectiveLimit + 1);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            command.Parameters.AddWithValue("membership_id", principal.MembershipId);
            command.Parameters.AddWithValue("user_id", principal.UserId);
            command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
            command.Parameters.Add(new NpgsqlParameter("session_store_id", NpgsqlDbType.Uuid)
            {
                Value = (object?)sessionStoreId ?? DBNull.Value
            });
            command.Parameters.AddWithValue("query_limit", effectiveLimit + 1);
            command.Parameters.AddWithValue("query_offset", plan.Offset);
            foreach (var parameter in plan.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
                for (var index = 0; index < fields.Length; index++)
                {
                    object? value = reader.IsDBNull(index)
                        ? null
                        : fields[index].ValueKind == QueryValueKind.Timestamp
                            ? reader.GetFieldValue<DateTimeOffset>(index)
                            : reader.GetString(index);
                    row.Add(fields[index].Metadata.Path, value);
                }

                rows.Add(row);
            }
        }

        stopwatch.Stop();
        var hasMore = rows.Count > effectiveLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var audit = JsonSerializer.SerializeToElement(new
        {
            organizationId = principal.OrganizationId,
            storeId = sessionStoreId,
            queryVersion = 1,
            model = plan.Model.TechnicalName,
            fields = plan.Fields.Select(field => field.Path).ToArray(),
            limit = effectiveLimit,
            offset = plan.Offset,
            returnedCount = rows.Count,
            hasMore,
            durationMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds
        });
        await InsertAuditAsync(
            connection,
            transaction,
            principal.UserId,
            "HUB",
            "QUERY_EXECUTED",
            "query_model",
            plan.Model.TechnicalName,
            "Bounded Query AST v1 execution.",
            null,
            audit,
            requestId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var nextOffset = hasMore ? checked(plan.Offset + effectiveLimit) : (int?)null;
        return new QueryExecutionResponse(
            1,
            plan.Fields.Select(field => new QueryColumn(field.Path, field.Label, field.Type)).ToArray(),
            rows,
            new QueryPage(effectiveLimit, plan.Offset, nextOffset, hasMore));
    }
}
