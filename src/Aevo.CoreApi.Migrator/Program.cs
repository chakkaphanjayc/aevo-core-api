using System.Security.Cryptography;
using System.Text;
using Npgsql;

const long migrationLockKey = 748_322_902;

var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
var verifyOnly = args.Contains("--verify", StringComparer.Ordinal);
var rlsCheck = args.Contains("--rls-check", StringComparer.Ordinal);
var legacyRemovalCheck = args.Contains("--legacy-removal-check", StringComparer.Ordinal);
var rawDatabaseUrl = Environment.GetEnvironmentVariable("AEVO_DATABASE_URL")?.Trim();
if (string.IsNullOrWhiteSpace(rawDatabaseUrl)) rawDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")?.Trim();
var databaseUrl = string.IsNullOrWhiteSpace(rawDatabaseUrl) ? null : NormalizeDatabaseConnectionString(rawDatabaseUrl);
var migrationsPath = Environment.GetEnvironmentVariable("AEVO_MIGRATIONS_PATH")?.Trim();

if (string.IsNullOrWhiteSpace(migrationsPath)) migrationsPath = FindMigrationsDirectory();
if (string.IsNullOrWhiteSpace(migrationsPath) || !Directory.Exists(migrationsPath))
{
    throw new InvalidOperationException("Core migration directory was not found. Set AEVO_MIGRATIONS_PATH to aevo-core-api/db/migrations.");
}

var files = Directory.EnumerateFiles(migrationsPath, "*.sql")
    .Select(path => new MigrationFile(Path.GetFileName(path), File.ReadAllText(path)))
    .OrderBy(file => file.Name, StringComparer.Ordinal)
    .ToArray();

if (files.Length == 0) throw new InvalidOperationException("No Core API migration files were found.");
if (dryRun)
{
    foreach (var file in files) Console.WriteLine($"{file.Name}  sha256={file.Checksum}");
    return;
}
if (string.IsNullOrWhiteSpace(databaseUrl)) throw new InvalidOperationException("AEVO_DATABASE_URL is required to apply Core API migrations.");

await using var dataSource = CreateDataSource(databaseUrl);
if (rlsCheck)
{
    await RunRlsTransactionContextCheckAsync(dataSource);
    return;
}
if (legacyRemovalCheck)
{
    await RunLegacyRemovalCheckAsync(dataSource);
    return;
}

await using var connection = await dataSource.OpenConnectionAsync();
await using var lockCommand = new NpgsqlCommand("select pg_advisory_lock(@key)", connection);
lockCommand.Parameters.AddWithValue("key", migrationLockKey);
await lockCommand.ExecuteNonQueryAsync();

try
{
    await using (var setup = new NpgsqlCommand(
        """
        create table if not exists _aevo_core_migrations (
          id bigserial primary key,
          filename varchar(255) unique not null,
          checksum text not null,
          applied_at timestamptz not null default now()
        )
        """, connection))
    {
        await setup.ExecuteNonQueryAsync();
    }

    var applied = new Dictionary<string, string>(StringComparer.Ordinal);
    await using (var read = new NpgsqlCommand("select filename, checksum from _aevo_core_migrations order by id", connection))
    await using (var reader = await read.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) applied[reader.GetString(0)] = reader.GetString(1);
    }

    var knownFiles = files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
    var unknownApplied = applied.Keys.Where(name => !knownFiles.Contains(name)).ToArray();
    if (unknownApplied.Length > 0) throw new InvalidOperationException($"Core migration history contains missing files: {string.Join(", ", unknownApplied)}");

    foreach (var file in files)
    {
        if (applied.TryGetValue(file.Name, out var checksum))
        {
            if (!string.Equals(checksum, file.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Core migration drift detected for {file.Name}.");
            }
            continue;
        }

        if (verifyOnly) continue;
        Console.WriteLine($"Applying {file.Name}...");
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var migration = new NpgsqlCommand(file.Sql, connection, transaction))
        {
            await migration.ExecuteNonQueryAsync();
        }
        await using (var record = new NpgsqlCommand(
            "insert into _aevo_core_migrations (filename, checksum) values (@filename, @checksum)",
            connection,
            transaction))
        {
            record.Parameters.AddWithValue("filename", file.Name);
            record.Parameters.AddWithValue("checksum", file.Checksum);
            await record.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    Console.WriteLine(verifyOnly
        ? $"Verified {applied.Count} applied Core API migration(s)."
        : "Core API migrations complete.");
}
finally
{
    await using var unlock = new NpgsqlCommand("select pg_advisory_unlock(@key)", connection);
    unlock.Parameters.AddWithValue("key", migrationLockKey);
    await unlock.ExecuteNonQueryAsync();
}

static string FindMigrationsDirectory()
{
    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null)
    {
        var candidate = Path.Combine(current.FullName, "db", "migrations");
        if (Directory.Exists(candidate)) return candidate;
        current = current.Parent;
    }
    return string.Empty;
}

static NpgsqlDataSource CreateDataSource(string? connectionString)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("AEVO_DATABASE_URL is required to apply Core API migrations.");
    }

    try
    {
        return NpgsqlDataSource.Create(connectionString);
    }
    catch (Exception)
    {
        throw new InvalidOperationException("AEVO_DATABASE_URL is invalid. Use a PostgreSQL URI or an Npgsql connection string.");
    }
}

static string NormalizeDatabaseConnectionString(string value)
{
    if (!value.Contains("://", StringComparison.Ordinal)) return value;
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || !string.Equals(uri.Scheme, "postgres", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(uri.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("AEVO_DATABASE_URL must be a PostgreSQL URI or an Npgsql connection string.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')) ? "postgres" : uri.AbsolutePath.Trim('/'),
    };

    var userInfo = uri.UserInfo;
    if (!string.IsNullOrWhiteSpace(userInfo))
    {
        var separator = userInfo.IndexOf(':');
        builder.Username = Uri.UnescapeDataString(separator >= 0 ? userInfo[..separator] : userInfo);
        if (separator >= 0) builder.Password = Uri.UnescapeDataString(userInfo[(separator + 1)..]);
    }

    foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pair = parameter.Split('=', 2);
        if (pair.Length != 2) continue;
        var key = Uri.UnescapeDataString(pair[0]).Trim().ToLowerInvariant();
        var queryValue = Uri.UnescapeDataString(pair[1]).Trim();
        if (key == "sslmode")
        {
            builder.SslMode = queryValue.ToLowerInvariant() switch
            {
                "disable" => SslMode.Disable,
                "allow" => SslMode.Allow,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => throw new InvalidOperationException("AEVO_DATABASE_URL contains an unsupported sslmode.")
            };
        }
    }

    return builder.ConnectionString;
}

static async Task RunRlsTransactionContextCheckAsync(NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    var currentRole = string.Empty;
    var currentRoleBypassesRls = false;
    await using (var roleCommand = new NpgsqlCommand(
        "select current_user, rolbypassrls or rolsuper from pg_roles where rolname = current_user",
        connection))
    await using (var roleReader = await roleCommand.ExecuteReaderAsync())
    {
        if (!await roleReader.ReadAsync()) throw new InvalidOperationException("RLS check could not resolve the current database role.");
        currentRole = roleReader.GetString(0);
        currentRoleBypassesRls = roleReader.GetBoolean(1);
    }

    var users = new List<Guid>();
    await using (var usersCommand = new NpgsqlCommand(
        "select distinct user_id from aevo_application_assignments order by user_id limit 2",
        connection))
    await using (var usersReader = await usersCommand.ExecuteReaderAsync())
    {
        while (await usersReader.ReadAsync()) users.Add(usersReader.GetGuid(0));
    }
    if (users.Count < 2) throw new InvalidOperationException("RLS check requires at least two assignment principals.");

    var owner = users[0];
    var other = users[1];
    var probeRole = currentRoleBypassesRls ? await FindNonBypassRoleAsync(connection) : null;
    if (currentRoleBypassesRls && probeRole is null)
    {
        throw new InvalidOperationException("RLS check requires an existing non-superuser, non-BYPASSRLS database role.");
    }
    var addedSchemaUsage = false;
    var addedTableSelect = false;
    try
    {
        if (probeRole is not null)
        {
            addedSchemaUsage = !await HasPrivilegeAsync(connection, probeRole, "schema", "public", "USAGE");
            addedTableSelect = !await HasPrivilegeAsync(connection, probeRole, "table", "aevo_application_assignments", "SELECT");
            if (addedSchemaUsage)
            {
                await using var grantSchema = new NpgsqlCommand("grant usage on schema public to " + QuoteIdentifier(probeRole), connection);
                await grantSchema.ExecuteNonQueryAsync();
            }
            if (addedTableSelect)
            {
                await using var grantTable = new NpgsqlCommand("grant select on table aevo_application_assignments to " + QuoteIdentifier(probeRole), connection);
                await grantTable.ExecuteNonQueryAsync();
            }
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var force = new NpgsqlCommand(
                "alter table aevo_application_assignments force row level security",
                connection,
                transaction))
            {
                await force.ExecuteNonQueryAsync();
            }

            if (probeRole is not null)
            {
                await using var setRole = new NpgsqlCommand($"set local role {QuoteIdentifier(probeRole)}", connection, transaction);
                await setRole.ExecuteNonQueryAsync();
            }

            await SetTransactionContextAsync(connection, transaction, owner);
            var visible = await CountAssignmentsAsync(connection, transaction);
            var ownerVisible = await CountAssignmentsAsync(connection, transaction, owner);
            var otherVisible = await CountAssignmentsAsync(connection, transaction, other);
            if (visible == 0 || visible != ownerVisible || otherVisible != 0)
            {
                throw new InvalidOperationException($"RLS policy did not isolate the transaction context (visible={visible}, owner={ownerVisible}, other={otherVisible}).");
            }
            await transaction.RollbackAsync();
        }

        await using (var cleanTransaction = await connection.BeginTransactionAsync())
        {
            if (probeRole is not null)
            {
                await using var setRole = new NpgsqlCommand($"set local role {QuoteIdentifier(probeRole)}", connection, cleanTransaction);
                await setRole.ExecuteNonQueryAsync();
            }
            await using var context = new NpgsqlCommand("select current_setting('aevo.user_id', true)", connection, cleanTransaction);
            var contextValue = await context.ExecuteScalarAsync();
            var visibleAfterRollback = await CountAssignmentsAsync(connection, cleanTransaction);
            if (contextValue is not null and not DBNull && !string.IsNullOrWhiteSpace(Convert.ToString(contextValue)) || visibleAfterRollback != 0)
            {
                throw new InvalidOperationException("Tenant context leaked across a pooled connection transaction.");
            }
            await cleanTransaction.CommitAsync();
        }
    }
    finally
    {
        if (probeRole is not null)
        {
            if (addedTableSelect)
            {
                await using var revokeTable = new NpgsqlCommand("revoke select on table aevo_application_assignments from " + QuoteIdentifier(probeRole), connection);
                await revokeTable.ExecuteNonQueryAsync();
            }
            if (addedSchemaUsage)
            {
                await using var revokeSchema = new NpgsqlCommand("revoke usage on schema public from " + QuoteIdentifier(probeRole), connection);
                await revokeSchema.ExecuteNonQueryAsync();
            }
        }
    }

    Console.WriteLine($"RLS transaction-context check passed using {(probeRole is null ? "configured runtime role" : "temporary non-bypass probe role")}: tenant settings are transaction-local and assignment rows are isolated.");
}

static async Task RunLegacyRemovalCheckAsync(NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    var legacyRelations = new[]
    {
        "public.apps",
        "public.app_authorization_codes",
        "public.app_entitlements",
        "public.app_subscriptions",
        "public.billing_customers",
        "public.billing_webhook_events",
        "public.member_app_assignments",
        "public.member_app_roles",
        "public.member_app_scopes",
        "public.organization_entitlements",
        "public.plan_entitlements",
        "public.store_application_access",
        "public.subscriptions",
        "public.admin_query_subscriptions",
        "public.app_sessions",
        "public.application_registry",
        "public.impersonation_sessions",
        "public.platform_roles",
        "public.platform_users",
        "public.plans",
        "public.system_settings",
        "public.usage_counters"
    };

    await using var relationCommand = new NpgsqlCommand(
        "select relation_name from unnest(@relation_names) as expected(relation_name) where to_regclass(relation_name) is not null order by relation_name",
        connection);
    relationCommand.Parameters.AddWithValue("relation_names", legacyRelations);
    var remainingRelations = new List<string>();
    await using (var reader = await relationCommand.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) remainingRelations.Add(reader.GetString(0));
    }

    var legacyFunctions = new[]
    {
        "public.assign_default_hub_application()",
        "public.initialize_store_application_access()",
        "public.hub_create_store_from_template(uuid,uuid,text,text,uuid,text)",
        "public.hub_organization_entitlements(uuid)",
        "public.hub_organization_overview_metrics(uuid,timestamptz,timestamptz)",
        "public.hub_store_overview_metrics(uuid,uuid,timestamptz)",
        "public.hub_user_navigation_favorites(uuid)",
        "public.hub_user_organizations(uuid)",
        "private.is_app_entitled(uuid,uuid,text)",
        "public.hub_create_organization(uuid,text,text,text,text,text,text,text,text,text,text)",
        "public.hub_create_store(uuid,text,text,text,text,text,text,text,text)"
    };
    await using var functionCommand = new NpgsqlCommand(
        "select function_name from unnest(@function_names) as expected(function_name) where to_regprocedure(function_name) is not null order by function_name",
        connection);
    functionCommand.Parameters.AddWithValue("function_names", legacyFunctions);
    var remainingFunctions = new List<string>();
    await using (var reader = await functionCommand.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) remainingFunctions.Add(reader.GetString(0));
    }

    if (remainingRelations.Count > 0 || remainingFunctions.Count > 0)
    {
        throw new InvalidOperationException($"Retired Hub compatibility objects remain: relations=[{string.Join(", ", remainingRelations)}], functions=[{string.Join(", ", remainingFunctions)}].");
    }

    Console.WriteLine("Legacy removal check passed: retired Hub compatibility relations and functions are absent; Core owns sessions, assignments, bindings, installations, and billing projections.");
}

static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

static async Task<string?> FindNonBypassRoleAsync(NpgsqlConnection connection)
{
    await using var command = new NpgsqlCommand(
        """
        select rolname
        from pg_roles
        where not rolsuper
          and not rolbypassrls
          and rolname <> current_user
          and rolname not like 'aevo_rls_probe_%'
        order by case when rolname = 'authenticated' then 0 when rolname = 'anon' then 1 else 2 end, rolname
        limit 1
        """,
        connection);
    return await command.ExecuteScalarAsync() as string;
}

static async Task<bool> HasPrivilegeAsync(NpgsqlConnection connection, string role, string objectType, string objectName, string privilege)
{
    var function = objectType == "schema" ? "has_schema_privilege" : "has_table_privilege";
    await using var command = new NpgsqlCommand($"select {function}(@role, @object_name, @privilege)", connection);
    command.Parameters.AddWithValue("role", role);
    command.Parameters.AddWithValue("object_name", objectName);
    command.Parameters.AddWithValue("privilege", privilege);
    return Convert.ToBoolean(await command.ExecuteScalarAsync());
}

static async Task SetTransactionContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId)
{
    await using var command = new NpgsqlCommand(
        "select set_config('aevo.user_id', @user_id, true), set_config('aevo.organization_id', '', true), set_config('aevo.store_id', '', true), set_config('aevo.platform_role', '', true)",
        connection,
        transaction);
    command.Parameters.AddWithValue("user_id", userId.ToString());
    await command.ExecuteNonQueryAsync();
}

static async Task<long> CountAssignmentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? userId = null)
{
    await using var command = new NpgsqlCommand(
        userId is null
            ? "select count(*) from aevo_application_assignments"
            : "select count(*) from aevo_application_assignments where user_id = @user_id",
        connection,
        transaction);
    if (userId is not null) command.Parameters.AddWithValue("user_id", userId.Value);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

sealed record MigrationFile(string Name, string Sql)
{
    public string Checksum => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();
}
