using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Runtime;

namespace Aevo.CoreApi.Data;

internal static class TenantContextSql
{
    public static Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? userId,
        Guid? organizationId,
        Guid? storeId,
        string? appCode,
        string? platformRole,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "tenant.context",
            () => ApplyCoreAsync(connection, transaction, userId, organizationId, storeId, appCode, platformRole, cancellationToken));

    private static async Task ApplyCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? userId,
        Guid? organizationId,
        Guid? storeId,
        string? appCode,
        string? platformRole,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select
              set_config('aevo.user_id', @user_id, true),
              set_config('aevo.organization_id', @organization_id, true),
              set_config('aevo.store_id', @store_id, true),
              set_config('aevo.app_code', @app_code, true),
              set_config('aevo.platform_role', @platform_role, true)
            """,
            connection,
            transaction);
        Add(command, "user_id", userId);
        Add(command, "organization_id", organizationId);
        Add(command, "store_id", storeId);
        Add(command, "app_code", appCode);
        Add(command, "platform_role", platformRole);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(NpgsqlCommand command, string name, Guid? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value?.ToString() ?? string.Empty;
    }

    private static void Add(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value?.Trim() ?? string.Empty;
    }
}
