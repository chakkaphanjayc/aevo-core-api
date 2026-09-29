using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Runtime;

namespace Aevo.CoreApi.Data;

public sealed record AdminOrganizationRecord(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    int? MaxUsers,
    int? MaxStores,
    long StoresCount,
    long MembersCount,
    DateTimeOffset CreatedAt);

public sealed record AdminDirectoryPage<T>(
    IReadOnlyList<T> Items,
    bool HasMore);

public sealed record AdminOrganizationStoreRecord(
    Guid Id,
    string Code,
    string Name,
    string Timezone,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AdminOrganizationMemberRecord(
    Guid MembershipId,
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AdminOrganizationDetailRecord(
    AdminOrganizationRecord Organization,
    IReadOnlyList<AdminOrganizationStoreRecord> Stores,
    IReadOnlyList<AdminOrganizationMemberRecord> Members);

public sealed record AdminSubscriptionRecord(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    string PlanId,
    string Provider,
    string Status,
    DateTimeOffset? TrialEnd,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CanceledAt,
    string? ProviderSubscriptionId,
    string ProjectionVersion,
    DateTimeOffset UpdatedAt);

public sealed record AdminUserMembershipRecord(
    string OrganizationName,
    string Role);

public sealed record AdminUserRecord(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    IReadOnlyList<AdminUserMembershipRecord> Memberships);

public sealed record AdminSearchResultRecord(
    string Type,
    string Id,
    string Title,
    string Subtitle,
    string Status,
    string Route);

public sealed partial class CoreDataStore
{
    public async Task<IReadOnlyList<AdminOrganizationRecord>> ListAdminOrganizationsAsync(
        int limit,
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var page = await ListAdminOrganizationsPageAsync(limit, 0, query, status, cancellationToken);
        return page.Items;
    }

    public Task<AdminDirectoryPage<AdminOrganizationRecord>> ListAdminOrganizationsPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        CancellationToken cancellationToken)
        => ListAdminOrganizationsPageAsync(limit, offset, query, status, "createdAt", "desc", cancellationToken);

    public Task<AdminDirectoryPage<AdminOrganizationRecord>> ListAdminOrganizationsPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.organizations",
            () => ListAdminOrganizationsFromDatabaseAsync(limit, offset, query, status, sort, direction, cancellationToken));

    private async Task<AdminDirectoryPage<AdminOrganizationRecord>> ListAdminOrganizationsFromDatabaseAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var orderBy = OrganizationOrderBy(sort, direction);
        await using var command = new NpgsqlCommand(
            $"""
            select
              o.id,
              o.name,
              o.slug,
              o.status,
              count(distinct s.id) filter (where s.status = 'ACTIVE') as stores_count,
              count(distinct m.id) filter (where m.status = 'ACTIVE') as members_count,
              o.created_at
            from public.organizations o
            left join public.stores s on s.organization_id = o.id
            left join public.memberships m on m.organization_id = o.id
            where (@query is null
                   or lower(o.name) like '%' || lower(@query) || '%'
                   or lower(o.slug) like '%' || lower(@query) || '%')
              and (@status is null or o.status = @status)
            group by o.id, o.name, o.slug, o.status, o.created_at
            order by {orderBy}
            limit @limit_plus_one offset @offset
            """,
            connection);
        var pageSize = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        command.Parameters.AddWithValue("offset", Math.Max(offset, 0));
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(query) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(status)?.ToUpperInvariant() ?? DBNull.Value });

        var organizations = new List<AdminOrganizationRecord>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                organizations.Add(new AdminOrganizationRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    null,
                    null,
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("The organization directory is not available.", error);
        }

        var hasMore = organizations.Count > pageSize;
        if (hasMore) organizations.RemoveAt(organizations.Count - 1);
        return new AdminDirectoryPage<AdminOrganizationRecord>(organizations, hasMore);
    }

    public async Task<IReadOnlyList<AdminSubscriptionRecord>> ListAdminSubscriptionsAsync(
        int limit,
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var page = await ListAdminSubscriptionsPageAsync(limit, 0, query, status, cancellationToken);
        return page.Items;
    }

    public Task<AdminDirectoryPage<AdminSubscriptionRecord>> ListAdminSubscriptionsPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        CancellationToken cancellationToken)
        => ListAdminSubscriptionsPageAsync(limit, offset, query, status, "updatedAt", "desc", cancellationToken);

    public Task<AdminDirectoryPage<AdminSubscriptionRecord>> ListAdminSubscriptionsPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.subscriptions",
            () => ListAdminSubscriptionsFromDatabaseAsync(limit, offset, query, status, sort, direction, cancellationToken));

    private async Task<AdminDirectoryPage<AdminSubscriptionRecord>> ListAdminSubscriptionsFromDatabaseAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var orderBy = SubscriptionOrderBy(sort, direction);
        await using var command = new NpgsqlCommand(
            $"""
            select
              s.organization_id,
              s.organization_id,
              coalesce(o.name, '(unknown organization)'),
              s.plan_id,
              s.provider,
              s.status,
              s.trial_end,
              s.current_period_start,
              s.current_period_end,
              s.cancel_at_period_end,
              s.canceled_at,
              s.provider_subscription_id,
              s.projection_version,
              s.updated_at
            from aevo_organization_subscriptions s
            left join public.organizations o on o.id = s.organization_id
            where (@query is null
                   or lower(coalesce(o.name, '')) like '%' || lower(@query) || '%'
                   or lower(s.plan_id) like '%' || lower(@query) || '%'
                   or lower(coalesce(s.provider_subscription_id, '')) like '%' || lower(@query) || '%')
              and (@status is null or s.status = @status)
            order by {orderBy}
            limit @limit_plus_one offset @offset
            """,
            connection);
        var pageSize = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        command.Parameters.AddWithValue("offset", Math.Max(offset, 0));
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(query) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(status)?.ToUpperInvariant() ?? DBNull.Value });

        var subscriptions = new List<AdminSubscriptionRecord>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                subscriptions.Add(new AdminSubscriptionRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    NullableDateTimeOffset(reader, 6),
                    NullableDateTimeOffset(reader, 7),
                    NullableDateTimeOffset(reader, 8),
                    reader.GetBoolean(9),
                    NullableDateTimeOffset(reader, 10),
                    NullableString(reader, 11),
                    reader.GetString(12),
                    reader.GetFieldValue<DateTimeOffset>(13)));
            }
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("The subscription directory is not available.", error);
        }

        var hasMore = subscriptions.Count > pageSize;
        if (hasMore) subscriptions.RemoveAt(subscriptions.Count - 1);
        return new AdminDirectoryPage<AdminSubscriptionRecord>(subscriptions, hasMore);
    }

    public async Task<IReadOnlyList<AdminUserRecord>> ListAdminUsersAsync(
        int limit,
        string? query,
        string? status,
        CancellationToken cancellationToken)
    {
        var page = await ListAdminUsersPageAsync(limit, 0, query, status, cancellationToken);
        return page.Items;
    }

    public Task<AdminDirectoryPage<AdminUserRecord>> ListAdminUsersPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        CancellationToken cancellationToken)
        => ListAdminUsersPageAsync(limit, offset, query, status, "email", "asc", cancellationToken);

    public Task<AdminDirectoryPage<AdminUserRecord>> ListAdminUsersPageAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "admin.users",
            () => ListAdminUsersFromDatabaseAsync(limit, offset, query, status, sort, direction, cancellationToken));

    private async Task<AdminDirectoryPage<AdminUserRecord>> ListAdminUsersFromDatabaseAsync(
        int limit,
        int offset,
        string? query,
        string? status,
        string? sort,
        string? direction,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var orderBy = UserOrderBy(sort, direction);
        await using var command = new NpgsqlCommand(
            $"""
            with identities as (
              select id, email, display_name, upper(status) as status, 0 as source_priority
              from aevo_identity_users
              union all
              select id, email, display_name, upper(status) as status, 1 as source_priority
              from public.user_profiles
            ),
            canonical_identities as (
              select distinct on (id) id, email, coalesce(display_name, '') as display_name, status
              from identities
              order by id, source_priority
            ),
            filtered_identities as (
              select id, email, display_name, status
              from canonical_identities
              where (@query is null
                     or lower(email) like '%' || lower(@query) || '%'
                     or lower(display_name) like '%' || lower(@query) || '%')
                and (@status is null or status = @status)
              order by {orderBy}
              limit @limit_plus_one offset @offset
            )
            select
              i.id,
              i.email,
              i.display_name,
              i.status,
              o.name,
              r.code
            from filtered_identities i
            left join public.memberships m
              on m.user_id = i.id
             and m.status = 'ACTIVE'
            left join public.organizations o on o.id = m.organization_id
            left join public.roles r on r.id = m.role_id
            order by i.email asc, i.id asc, o.name asc nulls last
            """,
            connection);
        var pageSize = Math.Clamp(limit, 1, 50);
        command.Parameters.AddWithValue("limit_plus_one", pageSize + 1);
        command.Parameters.AddWithValue("offset", Math.Max(offset, 0));
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(query) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)NormalizeFilter(status)?.ToUpperInvariant() ?? DBNull.Value });

        var users = new Dictionary<Guid, AdminUserBuilder>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var userId = reader.GetGuid(0);
                if (!users.TryGetValue(userId, out var user))
                {
                    user = new AdminUserBuilder(
                        userId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3));
                    users.Add(userId, user);
                }

                if (!reader.IsDBNull(4) && !reader.IsDBNull(5))
                {
                    user.Memberships.Add(new AdminUserMembershipRecord(reader.GetString(4), reader.GetString(5)));
                }
            }
        }
        catch (PostgresException error)
        {
            throw new CoreDatabaseException("The user directory is not available.", error);
        }

        var orderedUsers = users.Values
            .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
            .Select(user => new AdminUserRecord(user.Id, user.Email, user.DisplayName, user.Status, user.Memberships))
            .ToArray();
        var hasMore = orderedUsers.Length > pageSize;
        return new AdminDirectoryPage<AdminUserRecord>(
            hasMore ? orderedUsers[..pageSize] : orderedUsers,
            hasMore);
    }

    public async Task<AdminOrganizationDetailRecord?> GetAdminOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var organization = await ReadAdminOrganizationAsync(connection, organizationId, cancellationToken);
        if (organization is null) return null;

        await using var storesCommand = new NpgsqlCommand(
            """
            select id, code, name, timezone, currency, status, created_at
            from public.stores
            where organization_id = @organization_id
            order by created_at asc, id asc
            limit 50
            """,
            connection);
        storesCommand.Parameters.AddWithValue("organization_id", organizationId);
        var stores = new List<AdminOrganizationStoreRecord>();
        await using (var reader = await storesCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                stores.Add(new AdminOrganizationStoreRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        await using var membersCommand = new NpgsqlCommand(
            """
            select m.id, m.user_id, coalesce(u.email, ''), coalesce(u.display_name, ''),
                   r.code, m.status, m.created_at
            from public.memberships m
            left join public.user_profiles u on u.id = m.user_id
            join public.roles r on r.id = m.role_id
            where m.organization_id = @organization_id
            order by m.created_at asc, m.id asc
            limit 50
            """,
            connection);
        membersCommand.Parameters.AddWithValue("organization_id", organizationId);
        var members = new List<AdminOrganizationMemberRecord>();
        await using (var reader = await membersCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(new AdminOrganizationMemberRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        return new AdminOrganizationDetailRecord(organization, stores, members);
    }

    public async Task<AdminSubscriptionRecord?> GetAdminSubscriptionAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select
              s.organization_id,
              coalesce(o.name, '(unknown organization)'),
              s.plan_id,
              s.provider,
              s.status,
              s.trial_end,
              s.current_period_start,
              s.current_period_end,
              s.cancel_at_period_end,
              s.canceled_at,
              s.provider_subscription_id,
              s.projection_version,
              s.updated_at
            from aevo_organization_subscriptions s
            left join public.organizations o on o.id = s.organization_id
            where s.organization_id = @organization_id
            """,
            connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new AdminSubscriptionRecord(
            reader.GetGuid(0),
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            NullableDateTimeOffset(reader, 5),
            NullableDateTimeOffset(reader, 6),
            NullableDateTimeOffset(reader, 7),
            reader.GetBoolean(8),
            NullableDateTimeOffset(reader, 9),
            NullableString(reader, 10),
            reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12));
    }

    public async Task<AdminUserRecord?> GetAdminUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with identities as (
              select id, email, display_name, upper(status) as status, 0 as source_priority
              from aevo_identity_users
              union all
              select id, email, display_name, upper(status) as status, 1 as source_priority
              from public.user_profiles
            ), canonical_identity as (
              select distinct on (id) id, email, coalesce(display_name, '') as display_name, status
              from identities
              where id = @user_id
              order by id, source_priority
            )
            select i.id, i.email, i.display_name, i.status, o.name, r.code
            from canonical_identity i
            left join public.memberships m on m.user_id = i.id and m.status = 'ACTIVE'
            left join public.organizations o on o.id = m.organization_id
            left join public.roles r on r.id = m.role_id
            order by o.name asc nulls last
            """,
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        AdminUserBuilder? user = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            user ??= new AdminUserBuilder(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3));
            if (!reader.IsDBNull(4) && !reader.IsDBNull(5))
            {
                user.Memberships.Add(new AdminUserMembershipRecord(reader.GetString(4), reader.GetString(5)));
            }
        }

        return user is null
            ? null
            : new AdminUserRecord(user.Id, user.Email, user.DisplayName, user.Status, user.Memberships);
    }

    public async Task<AdminOrganizationDetailRecord?> CreateAdminOrganizationAsync(
        Guid actorId,
        string requestId,
        JsonElement body,
        string reason,
        CancellationToken cancellationToken)
    {
        var name = RequiredJsonString(body, "name", 160).Trim();
        var slug = RequiredJsonString(body, "slug", 63).Trim().ToLowerInvariant();
        var ownerUserText = JsonString(body, "ownerUserId")?.Trim();
        if (!string.IsNullOrWhiteSpace(ownerUserText) && !Guid.TryParse(ownerUserText, out _))
        {
            throw new CoreDatabaseException("The organization owner id is invalid.");
        }
        var ownerUserId = string.IsNullOrWhiteSpace(ownerUserText) ? actorId : Guid.Parse(ownerUserText);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var organizationId = Guid.NewGuid();

        await using (var owner = new NpgsqlCommand("select 1 from public.user_profiles where id = @owner_user_id", connection, transaction))
        {
            owner.Parameters.AddWithValue("owner_user_id", ownerUserId);
            if (await owner.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new CoreDatabaseException("The organization owner must already have an Aevo Accounts identity.");
            }
        }

        await using (var organization = new NpgsqlCommand(
            "insert into public.organizations (id, name, slug, owner_user_id, status) values (@id, @name, @slug, @owner_user_id, 'ACTIVE')",
            connection,
            transaction))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            organization.Parameters.AddWithValue("name", name);
            organization.Parameters.AddWithValue("slug", slug);
            organization.Parameters.AddWithValue("owner_user_id", ownerUserId);
            await organization.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid roleId;
        await using (var role = new NpgsqlCommand("select id from public.roles where code = 'OWNER'", connection, transaction))
        {
            roleId = (Guid)(await role.ExecuteScalarAsync(cancellationToken)
                ?? throw new CoreDatabaseException("Owner role is not configured."));
        }

        Guid membershipId;
        await using (var membership = new NpgsqlCommand(
            "insert into public.memberships (organization_id, user_id, role_id, status) values (@organization_id, @user_id, @role_id, 'ACTIVE') returning id",
            connection,
            transaction))
        {
            membership.Parameters.AddWithValue("organization_id", organizationId);
            membership.Parameters.AddWithValue("user_id", ownerUserId);
            membership.Parameters.AddWithValue("role_id", roleId);
            membershipId = (Guid)(await membership.ExecuteScalarAsync(cancellationToken)
                ?? throw new CoreDatabaseException("Organization owner membership creation failed."));
        }

        await SyncCoreApplicationAssignmentAsync(connection, transaction, organizationId, membershipId, "HUB", "ACTIVE", cancellationToken);
        await using (var installation = new NpgsqlCommand(
            """
            insert into aevo_application_installations (organization_id, app_code, status, source, projection_version)
            select @organization_id, registry.code, 'DISABLED', 'SYSTEM', 'installation-v1'
            from aevo_application_registry registry
            where registry.owner_repository <> 'aevo-digital-sing'
            on conflict (organization_id, app_code) do nothing
            """,
            connection,
            transaction))
        {
            installation.Parameters.AddWithValue("organization_id", organizationId);
            await installation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var subscription = new NpgsqlCommand(
            """
            insert into aevo_organization_subscriptions
              (organization_id, plan_id, provider, status, current_period_start, current_period_end, projection_version)
            values
              (@organization_id, 'starter', 'MANUAL', 'ACTIVE', timezone('utc', now()), null, 'billing-v1')
            on conflict (organization_id) do nothing
            """,
            connection,
            transaction))
        {
            subscription.Parameters.AddWithValue("organization_id", organizationId);
            await subscription.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var entitlement = new NpgsqlCommand(
            """
            insert into aevo_organization_entitlements
              (organization_id, feature_key, is_enabled, custom_override, limit_value, source, projection_version)
            select @organization_id, feature_key, is_enabled, false, limit_value, 'PLAN', 'entitlements-v1'
            from aevo_plan_entitlements
            where plan_id = 'starter'
            on conflict (organization_id, feature_key) do nothing
            """,
            connection,
            transaction))
        {
            entitlement.Parameters.AddWithValue("organization_id", organizationId);
            await entitlement.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new { organizationId, name, slug, status = "ACTIVE", ownerUserId });
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "ORGANIZATION_CREATED", "organization", organizationId.ToString(), reason, null, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        return await GetAdminOrganizationAsync(organizationId, cancellationToken);
    }

    public async Task<AdminOrganizationDetailRecord?> UpdateAdminOrganizationAsync(
        Guid actorId,
        string requestId,
        Guid organizationId,
        JsonElement body,
        string reason,
        CancellationToken cancellationToken)
    {
        var name = JsonString(body, "name")?.Trim();
        var slug = JsonString(body, "slug")?.Trim().ToLowerInvariant();
        var status = JsonString(body, "status")?.Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadJsonAsync(
            connection,
            transaction,
            "select jsonb_build_object('id', id, 'name', name, 'slug', slug, 'status', status) from public.organizations where id = @organization_id",
            cancellationToken,
            ("organization_id", organizationId));
        if (before is null) return null;

        await using (var command = new NpgsqlCommand(
            "update public.organizations set name = coalesce(@name, name), slug = coalesce(@slug, slug), status = coalesce(@status, status), updated_at = now() where id = @organization_id",
            connection,
            transaction))
        {
            AddNullableText(command, "name", name);
            AddNullableText(command, "slug", slug);
            AddNullableText(command, "status", status);
            command.Parameters.AddWithValue("organization_id", organizationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new { organizationId, name, slug, status });
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "ORGANIZATION_UPDATED", "organization", organizationId.ToString(), reason, before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        return await GetAdminOrganizationAsync(organizationId, cancellationToken);
    }

    public async Task<AdminUserRecord?> UpdateAdminUserStatusAsync(
        Guid actorId,
        string requestId,
        Guid userId,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = status.Trim().ToUpperInvariant();
        var identityStatus = normalizedStatus == "ACTIVE" ? "active" : "disabled";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadJsonAsync(
            connection,
            transaction,
            """
            select jsonb_build_object(
              'id', coalesce(i.id, p.id),
              'email', coalesce(i.email, p.email),
              'identityStatus', i.status,
              'profileStatus', p.status)
            from (select @user_id::uuid as id) target
            left join aevo_identity_users i on i.id = target.id
            left join public.user_profiles p on p.id = target.id
            where i.id is not null or p.id is not null
            """,
            cancellationToken,
            ("user_id", userId));
        if (before is null) return null;

        await using (var identity = new NpgsqlCommand(
            "update aevo_identity_users set status = @status, updated_at = now() where id = @user_id",
            connection,
            transaction))
        {
            identity.Parameters.AddWithValue("status", identityStatus);
            identity.Parameters.AddWithValue("user_id", userId);
            await identity.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var profile = new NpgsqlCommand(
            "update public.user_profiles set status = @status, updated_at = now() where id = @user_id",
            connection,
            transaction))
        {
            profile.Parameters.AddWithValue("status", normalizedStatus);
            profile.Parameters.AddWithValue("user_id", userId);
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new { userId, status = normalizedStatus });
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "USER_STATUS_UPDATED", "user", userId.ToString(), reason, before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (normalizedStatus == "DISABLED") await RevokeSessionsForUserAsync(userId, cancellationToken);
        return await GetAdminUserAsync(userId, cancellationToken);
    }

    public async Task<IReadOnlyList<AdminSearchResultRecord>> SearchAdminDirectoryAsync(
        string query,
        int limit,
        bool includeSubscriptions,
        bool includeApplications,
        bool includeConnections,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeFilter(query);
        if (normalized is null) return Array.Empty<AdminSearchResultRecord>();
        var result = new List<AdminSearchResultRecord>();
        var perType = Math.Clamp((limit + 4) / 5, 1, 10);

        var organizationsTask = ListAdminOrganizationsPageAsync(perType, 0, normalized, null, cancellationToken);
        var usersTask = ListAdminUsersPageAsync(perType, 0, normalized, null, cancellationToken);
        var subscriptionsTask = includeSubscriptions
            ? ListAdminSubscriptionsPageAsync(perType, 0, normalized, null, cancellationToken)
            : Task.FromResult(new AdminDirectoryPage<AdminSubscriptionRecord>(Array.Empty<AdminSubscriptionRecord>(), false));
        var applicationsTask = includeApplications ? ListApplicationsAsync(cancellationToken) : Task.FromResult<IReadOnlyList<ApplicationRegistryRecord>>(Array.Empty<ApplicationRegistryRecord>());
        var connectionsTask = includeConnections ? ListConnectionsAsync(environment, cancellationToken) : Task.FromResult<IReadOnlyList<ApplicationConnectionRecord>>(Array.Empty<ApplicationConnectionRecord>());
        await Task.WhenAll(organizationsTask, usersTask, subscriptionsTask, applicationsTask, connectionsTask);

        foreach (var organization in organizationsTask.Result.Items)
        {
            result.Add(new AdminSearchResultRecord("organization", organization.Id.ToString(), organization.Name, organization.Slug, organization.Status, $"/organizations/{organization.Id}"));
        }
        foreach (var user in usersTask.Result.Items)
        {
            result.Add(new AdminSearchResultRecord("user", user.Id.ToString(), user.DisplayName.Length > 0 ? user.DisplayName : user.Email, user.Email, user.Status, $"/users/{user.Id}"));
        }
        foreach (var subscription in subscriptionsTask.Result.Items)
        {
            result.Add(new AdminSearchResultRecord("subscription", subscription.Id.ToString(), subscription.OrganizationName, $"{subscription.PlanId} · {subscription.Provider}", subscription.Status, $"/subscriptions/{subscription.Id}"));
        }
        if (includeApplications)
        {
            foreach (var application in applicationsTask.Result.Where(application => ContainsIgnoreCase(application.Code, normalized) || ContainsIgnoreCase(application.Name, normalized)).Take(perType))
            {
                result.Add(new AdminSearchResultRecord("application", application.Code, application.Name, application.Kind, application.Status, $"/applications/{application.Code}"));
            }
        }
        if (includeConnections)
        {
            foreach (var connection in connectionsTask.Result.Where(connection => ContainsIgnoreCase(connection.AppCode, normalized) || ContainsIgnoreCase(connection.Label, normalized)).Take(perType))
            {
                result.Add(new AdminSearchResultRecord("connection", connection.AppCode, connection.Label, connection.BaseUrl ?? connection.Status, connection.Status, "/connections"));
            }
        }

        return result.Take(Math.Clamp(limit, 1, 50)).ToArray();
    }

    private static bool ContainsIgnoreCase(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string SortDirection(string? direction) => string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

    private static string OrganizationOrderBy(string? sort, string? direction)
    {
        var column = sort switch
        {
            "name" or "organization" => "o.name",
            "status" => "o.status",
            "stores" => "stores_count",
            "members" => "members_count",
            "createdAt" => "o.created_at",
            _ => "o.created_at"
        };
        return $"{column} {SortDirection(direction)}, o.id asc";
    }

    private static string SubscriptionOrderBy(string? sort, string? direction)
    {
        var column = sort switch
        {
            "organization" => "coalesce(o.name, '(unknown organization)')",
            "plan" => "s.plan_id",
            "provider" => "s.provider",
            "status" => "s.status",
            "periodEnd" => "s.current_period_end",
            "updatedAt" => "s.updated_at",
            _ => "s.updated_at"
        };
        return $"{column} {SortDirection(direction)}, s.organization_id asc";
    }

    private static string UserOrderBy(string? sort, string? direction)
    {
        var column = sort switch
        {
            "user" or "displayName" => "display_name",
            "status" => "status",
            "email" => "email",
            _ => "email"
        };
        return $"{column} {SortDirection(direction)}, id asc";
    }

    private static async Task<AdminOrganizationRecord?> ReadAdminOrganizationAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select o.id, o.name, o.slug, o.status,
                   (select count(*) from public.stores s where s.organization_id = o.id and s.status = 'ACTIVE'),
                   (select count(*) from public.memberships m where m.organization_id = o.id and m.status = 'ACTIVE'),
                   o.created_at
            from public.organizations o
            where o.id = @organization_id
            """,
            connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AdminOrganizationRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                null,
                null,
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetFieldValue<DateTimeOffset>(6))
            : null;
    }

    private static string? NormalizeFilter(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed class AdminUserBuilder(Guid id, string email, string displayName, string status)
    {
        public Guid Id { get; } = id;
        public string Email { get; } = email;
        public string DisplayName { get; } = displayName;
        public string Status { get; } = status;
        public List<AdminUserMembershipRecord> Memberships { get; } = [];
    }
}
