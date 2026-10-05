using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Runtime;
using Aevo.CoreApi.Security;

namespace Aevo.CoreApi.Data;

public sealed record HubPrincipalRecord(
    Guid UserId,
    Guid MembershipId,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed record HubOrganizationRecord(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record HubStoreRecord(
    Guid Id,
    Guid OrganizationId,
    string Code,
    string Name,
    string Timezone,
    string Currency,
    string Status,
    string? StoreMode,
    string? Address,
    string? Phone,
    string? TaxId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record HubStoreDeletionRecord(
    Guid Id,
    Guid OrganizationId,
    Guid StoreId,
    string Status,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ScheduledPurgeAt,
    Guid? CancelledBy,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? PurgedAt,
    string Reason,
    string? RequestId);

public sealed record HubApplicationRecord(
    string Id,
    string Code,
    string Name,
    string Description,
    string Icon,
    string PricingModel,
    int BasePriceMonthlyMinor,
    string Status,
    IReadOnlyList<string> Features,
    string ManifestVersion,
    string OwnerRepository,
    string ContractVersion,
    string Audience,
    string InstallScope,
    bool StoreScoped,
    string LaunchPath,
    string LifecycleStatus,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ConfigSchemaRefs,
    string? Origin,
    DateTimeOffset CreatedAt);

public sealed record HubStoreApplicationRecord(
    Guid OrganizationId,
    Guid StoreId,
    string ApplicationCode,
    string ApplicationName,
    string Status,
    bool ApplicationActive,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ConfigSchemaRefs);

public sealed record HubApplicationConfigOptionRecord(
    string Value,
    string Label);

public sealed record HubApplicationConfigFieldRecord(
    string Key,
    string Label,
    string Description,
    string Type,
    bool Required,
    JsonElement DefaultValue,
    int? Min = null,
    int? Max = null,
    int? MaxLength = null,
    IReadOnlyList<HubApplicationConfigOptionRecord>? Options = null);

public sealed record HubApplicationConfigSchemaRecord(
    string SchemaRef,
    string SchemaVersion,
    string Label,
    string Description,
    IReadOnlyList<HubApplicationConfigFieldRecord> Fields,
    JsonElement Config,
    DateTimeOffset? UpdatedAt);

public sealed record HubStoreApplicationConfigurationRecord(
    Guid OrganizationId,
    Guid StoreId,
    string ApplicationCode,
    string ApplicationName,
    bool ApplicationActive,
    bool ApplicationEnabled,
    IReadOnlyList<HubApplicationConfigSchemaRecord> Schemas);

public sealed class HubStoreApplicationIdempotencyConflictException()
    : Exception("The idempotency key was already used for a different store application operation.");

public sealed class HubStoreApplicationEntitlementException(
    string code,
    string message,
    int statusCode = 403)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class HubStoreRetentionException()
    : Exception("The store is in its retention window and cannot be changed until the deletion request is cancelled.")
{
    public string Code { get; } = "STORE_RETENTION_ACTIVE";
}

public sealed class HubIntegrationValidationException(string code, string message, int statusCode = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record HubFavoriteRecord(
    Guid Id,
    Guid UserId,
    Guid? OrganizationId,
    Guid? StoreId,
    string Kind,
    string TargetKey,
    string Label,
    string Href,
    string IconKey,
    int Position,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record HubMemberRecord(
    Guid MembershipId,
    Guid UserId,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    IReadOnlyList<Guid> StoreIds,
    DateTimeOffset CreatedAt);

public sealed record HubDeviceRecord(
    Guid Id,
    Guid OrganizationId,
    Guid StoreId,
    string Name,
    string Mode,
    Guid? StationId,
    string Status,
    DateTimeOffset? PairedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? PairingExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record HubProductVariantRecord(
    Guid Id,
    string Code,
    string Name,
    int PriceMinor,
    int SortOrder,
    string Status);

public sealed record HubAvailabilityRecord(
    string Channel,
    bool IsAvailable,
    bool SoldOut,
    int? PriceOverrideMinor);

public sealed record HubProductRecord(
    Guid Id,
    Guid OrganizationId,
    Guid? CategoryId,
    string Sku,
    string Name,
    string Description,
    int BasePriceMinor,
    string Currency,
    string Status,
    string? ImageUrl,
    int DisplayOrder,
    IReadOnlyList<HubProductVariantRecord> Variants,
    IReadOnlyList<HubAvailabilityRecord> Availability,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record HubCategoryRecord(
    Guid Id,
    Guid OrganizationId,
    Guid? ParentId,
    string Code,
    string Name,
    string Slug,
    int SortOrder,
    string Status);

public sealed record HubMenuItemRecord(
    Guid Id,
    Guid MenuId,
    Guid ProductId,
    Guid? VariantId,
    int? PriceOverrideMinor,
    int SortOrder,
    bool IsAvailable,
    bool SoldOut);

public sealed record HubMenuRecord(
    Guid Id,
    Guid OrganizationId,
    Guid StoreId,
    string Code,
    string Name,
    string Channel,
    string Status,
    IReadOnlyList<HubMenuItemRecord> Items);

public sealed record HubModifierRecord(
    Guid Id,
    string Code,
    string Name,
    int PriceDeltaMinor,
    int SortOrder,
    string Status);

public sealed record HubModifierGroupRecord(
    Guid Id,
    Guid OrganizationId,
    string Code,
    string Name,
    string SelectionType,
    int MinSelections,
    int MaxSelections,
    bool Required,
    IReadOnlyList<HubModifierRecord> Modifiers,
    string Status);

public sealed record HubProductModifierGroupRecord(
    Guid ProductId,
    Guid ModifierGroupId,
    int SortOrder);

public sealed record HubCatalogSnapshotRecord(
    IReadOnlyList<HubCategoryRecord> Categories,
    IReadOnlyList<HubProductRecord> Products,
    IReadOnlyList<HubMenuRecord> Menus,
    IReadOnlyList<HubModifierGroupRecord> ModifierGroups,
    IReadOnlyList<HubProductModifierGroupRecord> ProductModifierGroups);

public sealed class HubCatalogValidationException(string code, string message, int statusCode = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record HubStoreTemplateRecord(
    Guid Id,
    Guid OrganizationId,
    string Name,
    Guid? SourceStoreId,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed partial class CoreDataStore
{
    private const int HubStoreDeletionGraceDays = 7;
    private static readonly JsonSerializerOptions HubDashboardJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Task<HubPrincipalRecord?> ResolveHubPrincipalAsync(CoreSession session, CancellationToken cancellationToken)
    {
        var key = string.Join(":", new object?[]
        {
            session.Id,
            session.UserId,
            session.OrganizationId,
            session.StoreId
        });
        return hubPrincipalFlights.RunAsync(key, () =>
        {
            if (memoryCache is null)
            {
                return RequestPerformance.MeasureDatabaseAsync(
                    "authorization.hub_principal",
                    () => ResolveHubPrincipalFromDatabaseAsync(session, cancellationToken));
            }

            var cacheKey = AevoCacheKeys.HubPrincipal(session.UserId, session.OrganizationId, session.StoreId);
            return memoryCache.GetOrCreateAsync(
                cacheKey,
                AccessSnapshotTtl,
                ct => RequestPerformance.MeasureDatabaseAsync(
                    "authorization.hub_principal",
                    () => ResolveHubPrincipalFromDatabaseAsync(session, ct)),
                cancellationToken);
        });
    }

    private async Task<HubPrincipalRecord?> ResolveHubPrincipalFromDatabaseAsync(CoreSession session, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            session.UserId,
            session.OrganizationId,
            session.StoreId,
            "HUB",
            session.PlatformRole,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select m.id, m.organization_id, o.name, o.slug, r.code,
                   coalesce(array_agg(distinct rp.permission_code)
                     filter (where rp.permission_code is not null), '{}')
            from public.memberships m
            join public.organizations o on o.id = m.organization_id and o.status = 'ACTIVE'
            join public.roles r on r.id = m.role_id
            left join public.role_permissions rp on rp.role_id = r.id
            where m.user_id = @user_id
              and m.status = 'ACTIVE'
              and (@organization_id is null or m.organization_id = @organization_id)
              and exists (
                select 1
                from aevo_application_assignments aa
                where aa.user_id = m.user_id
                  and (aa.organization_id is null or aa.organization_id = m.organization_id)
                  and aa.app_code = 'HUB'
                  and aa.status = 'active'
                  and aa.starts_at <= now()
                  and (aa.expires_at is null or aa.expires_at > now())
              )
            group by m.id, m.organization_id, o.name, o.slug, r.code
            order by m.created_at
            limit 1
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", session.UserId);
        AddNullableGuid(command, "organization_id", session.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new HubPrincipalRecord(
            session.UserId, reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetFieldValue<string[]>(5));
    }

    public async Task<IReadOnlyList<HubOrganizationRecord>> ListHubOrganizationsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(connection, transaction, userId, null, null, "HUB", null, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select o.id, o.name, o.slug, o.status, o.created_at, o.updated_at
            from public.organizations o
            join public.memberships m on m.organization_id = o.id
            where m.user_id = @user_id and m.status = 'ACTIVE'
              and o.status = 'ACTIVE'
              and exists (
                select 1 from aevo_application_assignments aa
                where aa.user_id = m.user_id
                  and (aa.organization_id is null or aa.organization_id = m.organization_id)
                  and aa.app_code = 'HUB'
                  and aa.status = 'active' and aa.starts_at <= now()
                  and (aa.expires_at is null or aa.expires_at > now())
              )
            order by o.created_at
            """, connection, transaction);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubOrganizationRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadOrganization(reader));
        return result;
    }

    public Task<HubOrganizationRecord?> GetHubOrganizationAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return RequestPerformance.MeasureDatabaseAsync(
                "hub.organization.get",
                () => GetHubOrganizationFromDatabaseAsync(userId, organizationId, cancellationToken));
        }

        var cacheKey = AevoCacheKeys.HubOrganization(userId, organizationId);
        return memoryCache.GetOrCreateAsync(
            cacheKey,
            TimeSpan.FromSeconds(30),
            ct => RequestPerformance.MeasureDatabaseAsync(
                "hub.organization.get",
                () => GetHubOrganizationFromDatabaseAsync(userId, organizationId, ct)),
            cancellationToken);
    }

    private async Task<HubOrganizationRecord?> GetHubOrganizationFromDatabaseAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select o.id, o.name, o.slug, o.status, o.created_at, o.updated_at
            from public.organizations o
            join public.memberships m on m.organization_id = o.id and m.user_id = @user_id and m.status = 'ACTIVE'
            where o.id = @organization_id and o.status = 'ACTIVE'
              and exists (
                select 1 from aevo_application_assignments aa
                where aa.user_id = m.user_id
                  and (aa.organization_id is null or aa.organization_id = m.organization_id)
                  and aa.app_code = 'HUB'
                  and aa.status = 'active' and aa.starts_at <= now()
                  and (aa.expires_at is null or aa.expires_at > now())
              )
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOrganization(reader) : null;
    }

    public async Task<HubOrganizationRecord> CreateHubOrganizationAsync(Guid userId, string name, string slug, CancellationToken cancellationToken)
        => await CreateHubOrganizationAsync(userId, name, slug, default, cancellationToken);

    public async Task<HubOrganizationRecord> CreateHubOrganizationAsync(Guid userId, string name, string slug, JsonElement body, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var organizationId = Guid.NewGuid();
        await using (var organization = new NpgsqlCommand(
            """
            insert into public.organizations (id, name, slug, owner_user_id, onboarding_status)
            values (@id, @name, @slug, @user_id, 'IN_PROGRESS')
            """, connection, transaction))
        {
            organization.Parameters.AddWithValue("id", organizationId);
            organization.Parameters.AddWithValue("name", name.Trim());
            organization.Parameters.AddWithValue("slug", slug.Trim().ToLowerInvariant());
            organization.Parameters.AddWithValue("user_id", userId);
            await organization.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid roleId;
        await using (var role = new NpgsqlCommand("select id from public.roles where code = 'OWNER'", connection, transaction))
        {
            var value = await role.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid resolvedRole) throw new CoreDatabaseException("Owner role is not configured.");
            roleId = resolvedRole;
        }

        Guid membershipId;
        await using (var membership = new NpgsqlCommand(
            "insert into public.memberships (organization_id, user_id, role_id, status) values (@organization_id, @user_id, @role_id, 'ACTIVE') returning id",
            connection, transaction))
        {
            membership.Parameters.AddWithValue("organization_id", organizationId);
            membership.Parameters.AddWithValue("user_id", userId);
            membership.Parameters.AddWithValue("role_id", roleId);
            membershipId = (Guid)(await membership.ExecuteScalarAsync(cancellationToken) ?? throw new CoreDatabaseException("Membership creation failed."));
        }

        await SyncCoreApplicationAssignmentAsync(connection, transaction, organizationId, membershipId, "HUB", "ACTIVE", cancellationToken);
        await using (var installation = new NpgsqlCommand(
            """
            insert into aevo_application_installations (organization_id, app_code, status, source, projection_version)
            select @organization_id, registry.code, 'DISABLED', 'SYSTEM', 'installation-v1'
            from aevo_application_registry registry
            where registry.owner_repository <> 'aevo-digital-sing'
            on conflict (organization_id, app_code) do nothing
            """, connection, transaction))
        {
            installation.Parameters.AddWithValue("organization_id", organizationId);
            await installation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var coreSubscription = new NpgsqlCommand(
            """
            insert into aevo_organization_subscriptions
              (organization_id, plan_id, provider, status, current_period_start, current_period_end, projection_version)
            values
              (@organization_id, 'starter', 'MANUAL', 'ACTIVE', timezone('utc', now()), null, 'billing-v1')
            on conflict (organization_id) do nothing
            """, connection, transaction))
        {
            coreSubscription.Parameters.AddWithValue("organization_id", organizationId);
            await coreSubscription.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var coreEntitlement = new NpgsqlCommand(
            """
            insert into aevo_organization_entitlements
              (organization_id, feature_key, is_enabled, custom_override, limit_value, source, projection_version)
            select @organization_id, feature_key, is_enabled, false, limit_value, 'PLAN', 'entitlements-v1'
            from aevo_plan_entitlements
            where plan_id = 'starter'
            on conflict (organization_id, feature_key) do nothing
            """, connection, transaction))
        {
            coreEntitlement.Parameters.AddWithValue("organization_id", organizationId);
            await coreEntitlement.ExecuteNonQueryAsync(cancellationToken);
        }

        if (body.ValueKind == JsonValueKind.Object)
        {
            await using var profile = new NpgsqlCommand(
                """
                update public.organizations
                set legal_name = coalesce(@legal_name, legal_name),
                    business_type = coalesce(@business_type, business_type),
                    country = coalesce(@country, country),
                    timezone = coalesce(@timezone, timezone),
                    currency = coalesce(@currency, currency),
                    contact_email = coalesce(@contact_email, contact_email),
                    onboarding_status = 'IN_PROGRESS',
                    updated_at = now()
                where id = @organization_id
                """, connection, transaction);
            profile.Parameters.AddWithValue("organization_id", organizationId);
            AddNullableText(profile, "legal_name", JsonString(body, "legalName"));
            AddNullableText(profile, "business_type", JsonString(body, "businessType")?.Trim().ToUpperInvariant());
            AddNullableText(profile, "country", JsonString(body, "country")?.Trim().ToUpperInvariant());
            AddNullableText(profile, "timezone", JsonString(body, "timezone")?.Trim());
            AddNullableText(profile, "currency", JsonString(body, "currency")?.Trim().ToUpperInvariant());
            AddNullableText(profile, "contact_email", JsonString(body, "contactEmail")?.Trim().ToLowerInvariant());
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        return await GetHubOrganizationAsync(userId, organizationId, cancellationToken)
            ?? throw new CoreDatabaseException("Organization creation returned no organization.");
    }

    public async Task<HubOrganizationRecord?> UpdateHubOrganizationAsync(Guid userId, Guid organizationId, JsonElement body, CancellationToken cancellationToken)
    {
        var name = JsonString(body, "name");
        var slug = JsonString(body, "slug");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update public.organizations o
            set name = coalesce(@name, o.name), slug = coalesce(@slug, o.slug), updated_at = now()
            from public.memberships m
            join public.roles r on r.id = m.role_id
            where o.id = @organization_id and m.organization_id = o.id and m.user_id = @user_id
              and m.status = 'ACTIVE' and r.code in ('OWNER', 'ADMIN')
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableText(command, "name", name);
        AddNullableText(command, "slug", slug?.Trim().ToLowerInvariant());
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        InvalidateHubOrganizationCache(userId, organizationId);
        InvalidateAuthorizationCaches();
        return await GetHubOrganizationAsync(userId, organizationId, cancellationToken);
    }

    public Task<IReadOnlyList<HubStoreRecord>> ListHubStoresAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return ListHubStoresFromDatabaseAsync(principal, cancellationToken);
        }

        var isGlobal = principal.Role is "OWNER" or "ADMIN";
        var cacheKey = AevoCacheKeys.HubStores(principal.OrganizationId, isGlobal, principal.MembershipId);
        return memoryCache.GetOrCreateAsync(
            cacheKey,
            TimeSpan.FromSeconds(30),
            ct => ListHubStoresFromDatabaseAsync(principal, ct),
            cancellationToken);
    }

    private Task<IReadOnlyList<HubStoreRecord>> ListHubStoresFromDatabaseAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "hub.stores.list",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """
                    select s.id, s.organization_id, s.code, s.name, s.timezone, s.currency, s.status,
                           s.store_mode, s.address, s.phone, s.tax_id, s.created_at, s.updated_at
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
                      and (
                        @global_access
                        or exists (
                          select 1 from public.memberships m
                          join public.membership_stores ms on ms.membership_id = m.id
                          where m.id = @membership_id and m.status = 'ACTIVE' and ms.store_id = s.id
                        )
                      )
                    order by s.created_at
                    """, connection);
                command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
                command.Parameters.AddWithValue("membership_id", principal.MembershipId);
                command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var result = new List<HubStoreRecord>();
                while (await reader.ReadAsync(cancellationToken)) result.Add(ReadStore(reader));
                return (IReadOnlyList<HubStoreRecord>)result;
            });

    public async Task<HubStoreRecord?> GetHubStoreAsync(HubPrincipalRecord principal, Guid storeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select s.id, s.organization_id, s.code, s.name, s.timezone, s.currency, s.status,
                   s.store_mode, s.address, s.phone, s.tax_id, s.created_at, s.updated_at
            from public.stores s
            where s.id = @store_id and s.organization_id = @organization_id
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
              and (@global_access or exists (
                select 1 from public.membership_stores ms where ms.membership_id = @membership_id and ms.store_id = s.id
              ))
            """, connection);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("membership_id", principal.MembershipId);
        command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStore(reader) : null;
    }

    public async Task<HubStoreRecord> CreateHubStoreAsync(HubPrincipalRecord principal, JsonElement body, CancellationToken cancellationToken)
    {
        var name = RequiredJsonString(body, "name", 160);
        var code = RequiredJsonString(body, "code", 32).Trim().ToUpperInvariant();
        var timezone = JsonString(body, "timezone") ?? "Asia/Bangkok";
        var currency = (JsonString(body, "currency") ?? "THB").Trim().ToUpperInvariant();
        var storeMode = JsonString(body, "storeMode") ?? "POS";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into public.stores (organization_id, code, name, timezone, currency, store_mode, address, phone, tax_id, status)
            values (@organization_id, @code, @name, @timezone, @currency, @store_mode, @address, @phone, @tax_id, 'ACTIVE')
            returning id, organization_id, code, name, timezone, currency, status, store_mode, address, phone, tax_id, created_at, updated_at
            """, connection, transaction);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("timezone", timezone.Trim());
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("store_mode", storeMode.Trim());
        AddNullableText(command, "address", JsonString(body, "address"));
        AddNullableText(command, "phone", JsonString(body, "phone"));
        AddNullableText(command, "tax_id", JsonString(body, "taxId"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Store creation returned no store.");
        var store = ReadStore(reader);
        await reader.CloseAsync();

        await using (var bindings = new NpgsqlCommand(
            """
            insert into aevo_store_application_bindings (organization_id, store_id, app_code, status, source, projection_version)
            select @organization_id, @store_id, registry.code, 'DISABLED', 'HUB', 'store-binding-v1'
            from aevo_application_registry registry
            where registry.store_scoped = true
              and registry.owner_repository <> 'aevo-digital-sing'
            on conflict (organization_id, store_id, app_code) do nothing
            """, connection, transaction))
        {
            bindings.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            bindings.Parameters.AddWithValue("store_id", store.Id);
            await bindings.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateHubStoresCache(principal.OrganizationId, principal.MembershipId);
        InvalidateHubProfileCache(principal.OrganizationId, principal.MembershipId);
        InvalidateAuthorizationCaches();
        return store;
    }

    public async Task<HubStoreRecord?> UpdateHubStoreAsync(HubPrincipalRecord principal, Guid storeId, JsonElement body, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            update public.stores s set
              name = coalesce(@name, s.name), code = coalesce(@code, s.code), timezone = coalesce(@timezone, s.timezone),
              currency = coalesce(@currency, s.currency), store_mode = coalesce(@store_mode, s.store_mode),
              address = coalesce(@address, s.address), phone = coalesce(@phone, s.phone), tax_id = coalesce(@tax_id, s.tax_id),
              updated_at = now()
            where s.id = @store_id and s.organization_id = @organization_id
              and s.status = 'ACTIVE'
              and not exists (
                select 1
                from aevo_data_deletion_requests deletion
                where deletion.organization_id = s.organization_id
                  and deletion.resource_type = 'STORE'
                  and deletion.resource_id = s.id
                  and deletion.status = 'PENDING'
              )
              and (@global_access or exists (select 1 from public.membership_stores ms where ms.membership_id = @membership_id and ms.store_id = s.id))
            returning s.id, s.organization_id, s.code, s.name, s.timezone, s.currency, s.status, s.store_mode, s.address, s.phone, s.tax_id, s.created_at, s.updated_at
            """, connection);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("membership_id", principal.MembershipId);
        command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
        AddNullableText(command, "name", JsonString(body, "name"));
        AddNullableText(command, "code", JsonString(body, "code")?.Trim().ToUpperInvariant());
        AddNullableText(command, "timezone", JsonString(body, "timezone"));
        AddNullableText(command, "currency", JsonString(body, "currency")?.Trim().ToUpperInvariant());
        AddNullableText(command, "store_mode", JsonString(body, "storeMode"));
        AddNullableText(command, "address", JsonString(body, "address"));
        AddNullableText(command, "phone", JsonString(body, "phone"));
        AddNullableText(command, "tax_id", JsonString(body, "taxId"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var store = await reader.ReadAsync(cancellationToken) ? ReadStore(reader) : null;
        if (store is not null)
        {
            InvalidateHubStoresCache(principal.OrganizationId, principal.MembershipId);
            InvalidateAuthorizationCaches();
        }
        return store;
    }

    public async Task<HubStoreDeletionRecord?> GetHubStoreDeletionAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select deletion.id, deletion.organization_id, deletion.resource_id,
                   deletion.status, deletion.requested_by, deletion.requested_at,
                   deletion.scheduled_purge_at, deletion.cancelled_by,
                   deletion.cancelled_at, deletion.purged_at, deletion.reason,
                   deletion.request_id
            from aevo_data_deletion_requests deletion
            where deletion.organization_id = @organization_id
              and deletion.resource_type = 'STORE'
              and deletion.resource_id = @store_id
              and deletion.status = 'PENDING'
              and (@global_access or exists (
                select 1
                from public.membership_stores scope
                where scope.membership_id = @membership_id
                  and scope.store_id = deletion.resource_id
              ))
            order by deletion.requested_at desc
            limit 1
            """, connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("membership_id", principal.MembershipId);
        command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadHubStoreDeletion(reader) : null;
    }

    public async Task<bool> IsHubStoreDeletionPendingAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        CancellationToken cancellationToken)
        => await GetHubStoreDeletionAsync(principal, storeId, cancellationToken) is not null;

    private async Task EnsureHubStoreWritableAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        CancellationToken cancellationToken)
    {
        if (await IsHubStoreDeletionPendingAsync(principal, storeId, cancellationToken))
        {
            throw new HubStoreRetentionException();
        }
    }

    public async Task<HubStoreDeletionRecord?> RequestHubStoreDeletionAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string reason,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            principal.UserId,
            principal.OrganizationId,
            storeId,
            "HUB",
            null,
            cancellationToken);
        await AcquireHubProvisioningLockAsync(
            connection,
            transaction,
            principal.OrganizationId,
            $"store-retention:{storeId:D}",
            cancellationToken);

        await using (var existing = new NpgsqlCommand(
            """
            select deletion.id, deletion.organization_id, deletion.resource_id,
                   deletion.status, deletion.requested_by, deletion.requested_at,
                   deletion.scheduled_purge_at, deletion.cancelled_by,
                   deletion.cancelled_at, deletion.purged_at, deletion.reason,
                   deletion.request_id
            from aevo_data_deletion_requests deletion
            where deletion.organization_id = @organization_id
              and deletion.resource_type = 'STORE'
              and deletion.resource_id = @store_id
              and deletion.status = 'PENDING'
              and (@global_access or exists (
                select 1 from public.membership_stores scope
                where scope.membership_id = @membership_id and scope.store_id = deletion.resource_id
              ))
            order by deletion.requested_at desc
            limit 1
            for update
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            existing.Parameters.AddWithValue("store_id", storeId);
            existing.Parameters.AddWithValue("membership_id", principal.MembershipId);
            existing.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var pending = ReadHubStoreDeletion(reader);
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                return pending;
            }
        }

        Guid deletionId;
        DateTimeOffset scheduledPurgeAt;
        await using (var create = new NpgsqlCommand(
            """
            insert into aevo_data_deletion_requests
              (organization_id, resource_type, resource_id, status, requested_by,
               scheduled_purge_at, reason, request_id, metadata)
            select @organization_id, 'STORE', s.id, 'PENDING', @requested_by,
                   now() + (@grace_days * interval '1 day'), @reason, @request_id,
                   jsonb_build_object('retentionDays', @grace_days, 'requestedFrom', 'HUB')
            from public.stores s
            where s.id = @store_id
              and s.organization_id = @organization_id
              and s.status = 'ACTIVE'
              and (@global_access or exists (
                select 1 from public.membership_stores scope
                where scope.membership_id = @membership_id and scope.store_id = s.id
              ))
            returning id, scheduled_purge_at
            """, connection, transaction))
        {
            create.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            create.Parameters.AddWithValue("store_id", storeId);
            create.Parameters.AddWithValue("requested_by", principal.UserId);
            create.Parameters.AddWithValue("grace_days", HubStoreDeletionGraceDays);
            create.Parameters.AddWithValue("reason", reason);
            create.Parameters.AddWithValue("request_id", requestId);
            create.Parameters.AddWithValue("membership_id", principal.MembershipId);
            create.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
            await using var reader = await create.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            deletionId = reader.GetGuid(0);
            scheduledPurgeAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        await using (var deactivate = new NpgsqlCommand(
            "update public.stores set status = 'INACTIVE', updated_at = now() where id = @store_id and organization_id = @organization_id",
            connection,
            transaction))
        {
            deactivate.Parameters.AddWithValue("store_id", storeId);
            deactivate.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new
        {
            deletionId,
            organizationId = principal.OrganizationId,
            storeId,
            status = "PENDING",
            requestedAt = DateTimeOffset.UtcNow,
            scheduledPurgeAt,
            reason,
            retentionDays = HubStoreDeletionGraceDays
        });
        await InsertAuditAsync(
            connection,
            transaction,
            principal.UserId,
            "HUB",
            "HUB_STORE_DELETION_REQUESTED",
            "store",
            storeId.ToString("D"),
            reason,
            null,
            after,
            requestId,
            cancellationToken);
        await InsertHubAuditLogAsync(
            connection,
            transaction,
            principal.OrganizationId,
            principal.UserId,
            "STORE_DELETION_REQUESTED",
            "store",
            storeId,
            after,
            cancellationToken);
        await InsertHubRetentionOutboxAsync(
            connection,
            transaction,
            "HUB_STORE_DELETION_REQUESTED",
            $"{principal.OrganizationId:D}:{storeId:D}",
            after,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        InvalidateHubStoresCache(principal.OrganizationId, principal.MembershipId);
        InvalidateHubProfileCache(principal.OrganizationId, principal.MembershipId);
        InvalidateAuthorizationCaches();
        return new HubStoreDeletionRecord(
            deletionId,
            principal.OrganizationId,
            storeId,
            "PENDING",
            principal.UserId,
            DateTimeOffset.UtcNow,
            scheduledPurgeAt,
            null,
            null,
            null,
            reason,
            requestId);
    }

    public async Task<HubStoreDeletionRecord?> CancelHubStoreDeletionAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            principal.UserId,
            principal.OrganizationId,
            storeId,
            "HUB",
            null,
            cancellationToken);

        HubStoreDeletionRecord? pending;
        await using (var read = new NpgsqlCommand(
            """
            select id, organization_id, resource_id, status, requested_by,
                   requested_at, scheduled_purge_at, cancelled_by, cancelled_at,
                   purged_at, reason, request_id
            from aevo_data_deletion_requests
            where organization_id = @organization_id
              and resource_type = 'STORE'
              and resource_id = @store_id
              and status = 'PENDING'
              and (@global_access or exists (
                select 1 from public.membership_stores scope
                where scope.membership_id = @membership_id and scope.store_id = resource_id
              ))
            order by requested_at desc
            limit 1
            for update
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            read.Parameters.AddWithValue("store_id", storeId);
            read.Parameters.AddWithValue("membership_id", principal.MembershipId);
            read.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            pending = await reader.ReadAsync(cancellationToken) ? ReadHubStoreDeletion(reader) : null;
        }

        if (pending is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var cancel = new NpgsqlCommand(
            "update aevo_data_deletion_requests set status = 'CANCELLED', cancelled_by = @cancelled_by, cancelled_at = now() where id = @id",
            connection,
            transaction))
        {
            cancel.Parameters.AddWithValue("id", pending.Id);
            cancel.Parameters.AddWithValue("cancelled_by", principal.UserId);
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var restore = new NpgsqlCommand(
            "update public.stores set status = 'ACTIVE', updated_at = now() where id = @store_id and organization_id = @organization_id and status = 'INACTIVE'",
            connection,
            transaction))
        {
            restore.Parameters.AddWithValue("store_id", storeId);
            restore.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            await restore.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new
        {
            deletionId = pending.Id,
            organizationId = principal.OrganizationId,
            storeId,
            status = "CANCELLED",
            cancelledAt = DateTimeOffset.UtcNow,
            cancelledBy = principal.UserId
        });
        await InsertAuditAsync(
            connection,
            transaction,
            principal.UserId,
            "HUB",
            "HUB_STORE_DELETION_CANCELLED",
            "store",
            storeId.ToString("D"),
            "Store deletion request cancelled during the retention window.",
            JsonSerializer.SerializeToElement(pending),
            after,
            requestId,
            cancellationToken);
        await InsertHubAuditLogAsync(
            connection,
            transaction,
            principal.OrganizationId,
            principal.UserId,
            "STORE_DELETION_CANCELLED",
            "store",
            storeId,
            after,
            cancellationToken);
        await InsertHubRetentionOutboxAsync(
            connection,
            transaction,
            "HUB_STORE_DELETION_CANCELLED",
            $"{principal.OrganizationId:D}:{storeId:D}",
            after,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        InvalidateHubStoresCache(principal.OrganizationId, principal.MembershipId);
        InvalidateHubProfileCache(principal.OrganizationId, principal.MembershipId);
        InvalidateAuthorizationCaches();
        return pending with
        {
            Status = "CANCELLED",
            CancelledBy = principal.UserId,
            CancelledAt = DateTimeOffset.UtcNow
        };
    }

    // Kept as a compatibility shim for older internal callers. It now creates
    // a retention request and never removes data synchronously.
    public async Task<bool> DeleteHubStoreAsync(HubPrincipalRecord principal, Guid storeId, CancellationToken cancellationToken)
        => await RequestHubStoreDeletionAsync(
            principal,
            storeId,
            "Store deletion requested through the legacy API path.",
            $"legacy-store-delete-{storeId:N}",
            cancellationToken) is not null;

    public async Task<IReadOnlyList<HubApplicationRecord>> ListHubApplicationsAsync(string environment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select case when r.code = 'PLAY' then 'booking' else lower(r.code) end,
                   r.code,
                   r.name,
                   ''::text,
                   lower(r.code),
                   r.pricing_model,
                   r.base_price_monthly_minor,
                   r.status,
                   r.features,
                   r.manifest_version,
                   r.owner_repository,
                   r.contract_version,
                   r.audience,
                   r.install_scope,
                   r.store_scoped,
                   r.launch_path,
                   r.lifecycle_status,
                   r.capabilities,
                   r.config_schema_refs,
                   c.base_url,
                   r.created_at
            from aevo_application_registry r
            left join aevo_application_connections c on c.app_code = r.code and c.environment = @environment
            where r.code not in ('HUB', 'ADMIN')
            order by r.created_at
            """, connection);
        command.Parameters.AddWithValue("environment", environment);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubApplicationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HubApplicationRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                ReadJsonStringArray(reader, 8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetBoolean(14),
                reader.GetString(15),
                reader.GetString(16),
                ReadJsonStringArray(reader, 17),
                ReadJsonStringArray(reader, 18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetFieldValue<DateTimeOffset>(20)));
        }
        return result;
    }

    public async Task<IReadOnlyList<HubStoreApplicationRecord>?> ListHubStoreApplicationsAsync(HubPrincipalRecord principal, Guid storeId, CancellationToken cancellationToken)
    {
        if (await GetHubStoreAsync(principal, storeId, cancellationToken) is null) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.code,
                   r.name,
                   coalesce(binding.status, 'DISABLED'),
                   r.status = 'ACTIVE' and r.lifecycle_status not in ('DEPRECATED', 'RETIRED'),
                   r.capabilities,
                   r.config_schema_refs
            from aevo_application_registry r
            left join aevo_store_application_bindings binding on binding.app_code = r.code
              and binding.organization_id = @organization_id and binding.store_id = @store_id
            where r.store_scoped = true
              and r.owner_repository <> 'aevo-digital-sing'
            order by r.code
            """, connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubStoreApplicationRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HubStoreApplicationRecord(
                principal.OrganizationId,
                storeId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                ReadJsonStringArray(reader, 4),
                ReadJsonStringArray(reader, 5)));
        }
        return result;
    }

    public Task<HubStoreApplicationRecord?> UpdateHubStoreApplicationAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string applicationCode,
        bool enabled,
        CancellationToken cancellationToken)
        => UpdateHubStoreApplicationAsync(
            principal,
            storeId,
            applicationCode,
            enabled,
            "hub-store-binding",
            $"hub-store-binding-{storeId:N}-{applicationCode.Trim().ToUpperInvariant()}-{enabled}",
            cancellationToken);

    public async Task<HubStoreApplicationRecord?> UpdateHubStoreApplicationAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string applicationCode,
        bool enabled,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (await GetHubStoreAsync(principal, storeId, cancellationToken) is null) return null;
        await EnsureHubStoreWritableAsync(principal, storeId, cancellationToken);
        var normalizedApplicationCode = applicationCode.Trim().ToUpperInvariant();
        var normalizedIdempotencyKey = idempotencyKey.Trim();
        if (normalizedIdempotencyKey.Length is < 8 or > 128)
        {
            throw new ArgumentException("A store application idempotency key between 8 and 128 characters is required.", nameof(idempotencyKey));
        }

        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"STORE_APPLICATION_BINDING|{principal.UserId:D}|{storeId:D}|{normalizedApplicationCode}|{enabled.ToString().ToLowerInvariant()}"))).ToLowerInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireHubProvisioningLockAsync(connection, transaction, principal.OrganizationId, normalizedIdempotencyKey, cancellationToken);

        var existingOperation = await ReadHubProvisioningOperationAsync(
            connection,
            transaction,
            principal.OrganizationId,
            normalizedIdempotencyKey,
            cancellationToken);
        if (existingOperation is not null)
        {
            if (!string.Equals(existingOperation.Value.RequestHash, requestHash, StringComparison.Ordinal)
                || existingOperation.Value.StoreId != storeId
                || !string.Equals(existingOperation.Value.AppCode, normalizedApplicationCode, StringComparison.Ordinal)
                || existingOperation.Value.OperationType != "STORE_APPLICATION_BINDING")
            {
                throw new HubStoreApplicationIdempotencyConflictException();
            }

            if (!string.Equals(existingOperation.Value.Status, "SUCCEEDED", StringComparison.Ordinal))
            {
                throw new CoreDatabaseException("The previous store application provisioning operation did not complete successfully.");
            }

            await transaction.CommitAsync(cancellationToken);
            return ReadHubStoreApplicationResponse(existingOperation.Value.ResponseJson);
        }

        await using var manifestCommand = new NpgsqlCommand(
            """
            select code, name, status, lifecycle_status, capabilities, config_schema_refs
            from aevo_application_registry
            where code = @application_code
              and store_scoped = true
              and owner_repository <> 'aevo-digital-sing'
            """, connection, transaction);
        manifestCommand.Parameters.AddWithValue("application_code", normalizedApplicationCode);
        await using var manifestReader = await manifestCommand.ExecuteReaderAsync(cancellationToken);
        if (!await manifestReader.ReadAsync(cancellationToken)) return null;
        var manifestCode = manifestReader.GetString(0);
        var manifestName = manifestReader.GetString(1);
        var applicationActive = manifestReader.GetString(2) == "ACTIVE"
            && manifestReader.GetString(3) is not "DEPRECATED" and not "RETIRED";
        var capabilities = ReadJsonStringArray(manifestReader, 4);
        var configSchemaRefs = ReadJsonStringArray(manifestReader, 5);
        await manifestReader.CloseAsync();
        if (!applicationActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new HubStoreApplicationRecord(
                principal.OrganizationId,
                storeId,
                manifestCode,
                manifestName,
                "DISABLED",
                false,
                capabilities,
                configSchemaRefs);
        }

        var status = enabled ? "ACTIVE" : "DISABLED";
        if (enabled)
        {
            await EnsureStoreApplicationEntitledAsync(
                connection,
                transaction,
                principal.OrganizationId,
                storeId,
                manifestCode,
                manifestName,
                cancellationToken);
        }
        var before = await ReadJsonAsync(
            connection,
            transaction,
            "select coalesce((select to_jsonb(binding) from aevo_store_application_bindings binding where binding.organization_id=@organization_id and binding.store_id=@store_id and binding.app_code=@application_code),'null'::jsonb)",
            cancellationToken,
            ("organization_id", principal.OrganizationId),
            ("store_id", storeId),
            ("application_code", manifestCode));

        Guid operationId;
        await using (var operation = new NpgsqlCommand(
            "insert into aevo_provisioning_operations (organization_id, store_id, app_code, operation_type, idempotency_key, request_hash, status, created_by) values (@organization_id, @store_id, @app_code, 'STORE_APPLICATION_BINDING', @idempotency_key, @request_hash, 'PENDING', @created_by) returning id",
            connection,
            transaction))
        {
            operation.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            operation.Parameters.AddWithValue("store_id", storeId);
            operation.Parameters.AddWithValue("app_code", manifestCode);
            operation.Parameters.AddWithValue("idempotency_key", normalizedIdempotencyKey);
            operation.Parameters.AddWithValue("request_hash", requestHash);
            operation.Parameters.AddWithValue("created_by", principal.UserId);
            operationId = (Guid)(await operation.ExecuteScalarAsync(cancellationToken) ?? throw new CoreDatabaseException("Provisioning operation creation failed."));
        }

        await using (var binding = new NpgsqlCommand(
            """
            insert into aevo_store_application_bindings (
              organization_id, store_id, app_code, status, source, projection_version, updated_by
            )
            values (@organization_id, @store_id, @application_code, @status, 'HUB', 'store-binding-v1', @updated_by)
            on conflict (organization_id, store_id, app_code) do update set
              status = excluded.status,
              source = excluded.source,
              projection_version = excluded.projection_version,
              updated_by = excluded.updated_by,
              updated_at = now()
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            binding.Parameters.AddWithValue("store_id", storeId);
            binding.Parameters.AddWithValue("application_code", manifestCode);
            binding.Parameters.AddWithValue("status", status);
            binding.Parameters.AddWithValue("updated_by", principal.UserId);
            await binding.ExecuteNonQueryAsync(cancellationToken);
        }

        if (enabled)
        {
            await using var installation = new NpgsqlCommand(
                "insert into aevo_application_installations (organization_id, app_code, status, source, projection_version, updated_by) values (@organization_id, @app_code, 'ACTIVE', 'HUB', 'installation-v1', @updated_by) on conflict (organization_id, app_code) do update set status='ACTIVE', source='HUB', projection_version='installation-v1', updated_by=excluded.updated_by, updated_at=now()",
                connection,
                transaction);
            installation.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            installation.Parameters.AddWithValue("app_code", manifestCode);
            installation.Parameters.AddWithValue("updated_by", principal.UserId);
            await installation.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var installation = new NpgsqlCommand(
                "update aevo_application_installations installation set status = case when exists (select 1 from aevo_store_application_bindings active_binding where active_binding.organization_id = installation.organization_id and active_binding.app_code = installation.app_code and active_binding.status = 'ACTIVE') then 'ACTIVE' else 'DISABLED' end, source='HUB', projection_version='installation-v1', updated_by=@updated_by, updated_at=now() where installation.organization_id=@organization_id and installation.app_code=@app_code",
                connection,
                transaction);
            installation.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            installation.Parameters.AddWithValue("app_code", manifestCode);
            installation.Parameters.AddWithValue("updated_by", principal.UserId);
            await installation.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = new HubStoreApplicationRecord(principal.OrganizationId, storeId, manifestCode, manifestName, status, true, capabilities, configSchemaRefs);
        var response = JsonSerializer.SerializeToElement(new
        {
            organizationId = result.OrganizationId,
            storeId = result.StoreId,
            applicationCode = result.ApplicationCode,
            applicationName = result.ApplicationName,
            status = result.Status,
            applicationActive = result.ApplicationActive,
            capabilities = result.Capabilities,
            configSchemaRefs = result.ConfigSchemaRefs,
            operationId,
            idempotencyKey = normalizedIdempotencyKey
        });
        var eventPayload = JsonSerializer.SerializeToElement(new
        {
            operationId,
            idempotencyKey = normalizedIdempotencyKey,
            organizationId = result.OrganizationId,
            storeId = result.StoreId,
            applicationCode = result.ApplicationCode,
            enabled,
            before,
            after = response
        });
        await InsertAuditAsync(
            connection,
            transaction,
            principal.UserId,
            "HUB",
            "HUB_STORE_APPLICATION_BINDING_UPDATED",
            "store_application_binding",
            $"{storeId:D}:{manifestCode}",
            $"Set {manifestCode} store application binding to {status}.",
            before,
            response,
            requestId,
            cancellationToken);
        await using (var outbox = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values ('HUB_STORE_APPLICATION_BINDING_CHANGED', 'store_application_binding', @aggregate_id, @payload)",
            connection,
            transaction))
        {
            outbox.Parameters.AddWithValue("aggregate_id", $"{principal.OrganizationId:D}:{storeId:D}:{manifestCode}");
            outbox.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = eventPayload.GetRawText() });
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var completed = new NpgsqlCommand(
            "update aevo_provisioning_operations set status='SUCCEEDED', response=@response, completed_at=now() where id=@id",
            connection,
            transaction))
        {
            completed.Parameters.Add(new NpgsqlParameter("response", NpgsqlDbType.Jsonb) { Value = response.GetRawText() });
            completed.Parameters.AddWithValue("id", operationId);
            await completed.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        return result;
    }

    private static async Task EnsureStoreApplicationEntitledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid storeId,
        string applicationCode,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var featureKey = EntitlementEvaluator.FeatureKeyForApplication(applicationCode);
        if (featureKey is null)
        {
            throw new HubStoreApplicationEntitlementException(
                EntitlementReasonCodes.ApplicationNotRegistered,
                $"{applicationName} is not registered as a commercial application.");
        }

        bool quotaEnabled;
        int? quotaLimit;
        await using (var quota = new NpgsqlCommand(
            "select is_enabled, limit_value from aevo_organization_entitlements where organization_id=@organization_id and feature_key='store_application_bindings' for update",
            connection,
            transaction))
        {
            quota.Parameters.AddWithValue("organization_id", organizationId);
            await using var quotaReader = await quota.ExecuteReaderAsync(cancellationToken);
            if (!await quotaReader.ReadAsync(cancellationToken))
            {
                throw new HubStoreApplicationEntitlementException(
                    EntitlementReasonCodes.EntitlementRequired,
                    "The organization has no active Free tier or billing entitlement for store applications.");
            }

            quotaEnabled = quotaReader.GetBoolean(0);
            quotaLimit = quotaReader.IsDBNull(1) ? null : quotaReader.GetInt32(1);
        }

        EntitlementSubscriptionSnapshot? subscription = null;
        EntitlementFeatureSnapshot? entitlement = null;
        await using (var projection = new NpgsqlCommand(
            """
            select
              subscription.plan_id,
              subscription.status,
              subscription.trial_end,
              subscription.current_period_start,
              subscription.current_period_end,
              entitlement.feature_key,
              entitlement.is_enabled,
              entitlement.limit_value
            from (select @organization_id::uuid as organization_id) organization
            left join lateral (
              select s.plan_id, s.status, s.trial_end, s.current_period_start, s.current_period_end
              from aevo_organization_subscriptions s
              where s.organization_id = organization.organization_id
              order by s.updated_at desc, s.created_at desc
              limit 1
            ) subscription on true
            left join aevo_organization_entitlements entitlement
              on entitlement.organization_id = organization.organization_id
             and lower(entitlement.feature_key) = lower(@feature_key)
            """,
            connection,
            transaction))
        {
            projection.Parameters.AddWithValue("organization_id", organizationId);
            projection.Parameters.AddWithValue("feature_key", featureKey);
            await using var projectionReader = await projection.ExecuteReaderAsync(cancellationToken);
            if (!await projectionReader.ReadAsync(cancellationToken))
            {
                throw new CoreDatabaseException("The entitlement projection returned no organization row.");
            }

            if (!projectionReader.IsDBNull(0))
            {
                subscription = new EntitlementSubscriptionSnapshot(
                    projectionReader.GetString(0),
                    projectionReader.GetString(1),
                    projectionReader.IsDBNull(2) ? null : projectionReader.GetFieldValue<DateTimeOffset>(2),
                    projectionReader.IsDBNull(3) ? null : projectionReader.GetFieldValue<DateTimeOffset>(3),
                    projectionReader.IsDBNull(4) ? null : projectionReader.GetFieldValue<DateTimeOffset>(4));
            }

            if (!projectionReader.IsDBNull(5))
            {
                entitlement = new EntitlementFeatureSnapshot(
                    projectionReader.GetString(5),
                    projectionReader.GetBoolean(6),
                    projectionReader.IsDBNull(7) ? null : projectionReader.GetInt32(7));
            }
        }

        var applicationDecision = EntitlementEvaluator.Evaluate(
            organizationId,
            null,
            applicationCode,
            new ApplicationEntitlementSnapshot(
                applicationCode,
                true,
                true,
                subscription,
                entitlement,
                false,
                false),
            DateTimeOffset.UtcNow);
        if (!applicationDecision.Allowed)
        {
            var message = applicationDecision.Reason switch
            {
                EntitlementReasonCodes.EntitlementExpired => $"{applicationName} is not available because the organization entitlement has expired.",
                EntitlementReasonCodes.EntitlementInactive => $"{applicationName} is not available because the organization entitlement is inactive.",
                EntitlementReasonCodes.EntitlementRequired => $"{applicationName} requires an active organization entitlement before it can be enabled.",
                _ => $"{applicationName} cannot be enabled under the current organization billing state."
            };
            throw new HubStoreApplicationEntitlementException(applicationDecision.Reason, message);
        }

        if (!quotaEnabled)
        {
            throw new HubStoreApplicationEntitlementException(
                EntitlementReasonCodes.EntitlementInactive,
                "The organization entitlement for store applications is inactive.");
        }

        var currentStatus = "DISABLED";
        await using (var current = new NpgsqlCommand(
            "select coalesce(status, 'DISABLED') from aevo_store_application_bindings where organization_id=@organization_id and store_id=@store_id and app_code=@application_code",
            connection,
            transaction))
        {
            current.Parameters.AddWithValue("organization_id", organizationId);
            current.Parameters.AddWithValue("store_id", storeId);
            current.Parameters.AddWithValue("application_code", applicationCode);
            currentStatus = Convert.ToString(await current.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) ?? "DISABLED";
        }

        if (string.Equals(currentStatus, "ACTIVE", StringComparison.Ordinal)) return;

        int activeBindingCount;
        await using (var usage = new NpgsqlCommand(
            "select count(*)::integer from aevo_store_application_bindings where organization_id=@organization_id and status='ACTIVE'",
            connection,
            transaction))
        {
            usage.Parameters.AddWithValue("organization_id", organizationId);
            activeBindingCount = Convert.ToInt32(await usage.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (quotaLimit is { } limit && activeBindingCount >= limit)
        {
            throw new HubStoreApplicationEntitlementException(
                EntitlementReasonCodes.EntitlementLimitExceeded,
                $"The current Free tier allows {limit} active application on {limit} store. Disable the existing store app or upgrade the organization plan before enabling {applicationName}.");
        }
    }

    public async Task<HubStoreApplicationConfigurationRecord?> GetHubStoreApplicationConfigurationAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string applicationCode,
        CancellationToken cancellationToken)
    {
        if (await GetHubStoreAsync(principal, storeId, cancellationToken) is null) return null;
        var normalizedApplicationCode = applicationCode.Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            principal.UserId,
            principal.OrganizationId,
            storeId,
            "HUB",
            null,
            cancellationToken);
        await using var manifest = new NpgsqlCommand(
            """
            select r.code,
                   r.name,
                   r.status = 'ACTIVE' and r.lifecycle_status not in ('DEPRECATED', 'RETIRED'),
                   coalesce(binding.status, 'DISABLED'),
                   r.config_schema_refs
            from aevo_application_registry r
            left join aevo_store_application_bindings binding on binding.app_code = r.code
              and binding.organization_id = @organization_id and binding.store_id = @store_id
            where r.code = @application_code
              and r.store_scoped = true
              and r.owner_repository <> 'aevo-digital-sing'
            """,
            connection,
            transaction);
        manifest.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        manifest.Parameters.AddWithValue("store_id", storeId);
        manifest.Parameters.AddWithValue("application_code", normalizedApplicationCode);
        await using var manifestReader = await manifest.ExecuteReaderAsync(cancellationToken);
        if (!await manifestReader.ReadAsync(cancellationToken)) return null;
        var manifestCode = manifestReader.GetString(0);
        var manifestName = manifestReader.GetString(1);
        var applicationActive = manifestReader.GetBoolean(2);
        var applicationEnabled = string.Equals(manifestReader.GetString(3), "ACTIVE", StringComparison.Ordinal);
        var schemaRefs = ReadJsonStringArray(manifestReader, 4);
        await manifestReader.CloseAsync();

        var definitions = HubApplicationConfigurationCatalog.For(manifestCode, schemaRefs);
        var stored = new Dictionary<string, (string Version, JsonElement Config, DateTimeOffset UpdatedAt)>(StringComparer.Ordinal);
        await using (var configs = new NpgsqlCommand(
            "select schema_ref, schema_version, config, updated_at from aevo_store_application_configurations where organization_id=@organization_id and store_id=@store_id and app_code=@application_code",
            connection,
            transaction))
        {
            configs.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            configs.Parameters.AddWithValue("store_id", storeId);
            configs.Parameters.AddWithValue("application_code", manifestCode);
            await using var configReader = await configs.ExecuteReaderAsync(cancellationToken);
            while (await configReader.ReadAsync(cancellationToken))
            {
                stored[configReader.GetString(0)] = (
                    configReader.GetString(1),
                    ParseJson(configReader.GetValue(2), "{}"),
                    configReader.GetFieldValue<DateTimeOffset>(3));
            }
        }

        var schemas = definitions.Select(definition =>
        {
            if (stored.TryGetValue(definition.SchemaRef, out var saved)
                && string.Equals(saved.Version, definition.SchemaVersion, StringComparison.Ordinal))
            {
                return HubApplicationConfigurationCatalog.ToSchema(definition, saved.Config, saved.UpdatedAt);
            }
            return HubApplicationConfigurationCatalog.ToSchema(definition, HubApplicationConfigurationCatalog.DefaultConfig(definition), null);
        }).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new HubStoreApplicationConfigurationRecord(
            principal.OrganizationId,
            storeId,
            manifestCode,
            manifestName,
            applicationActive,
            applicationEnabled,
            schemas);
    }

    public async Task<HubStoreApplicationConfigurationRecord?> UpdateHubStoreApplicationConfigurationAsync(
        HubPrincipalRecord principal,
        Guid storeId,
        string applicationCode,
        JsonElement body,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (await GetHubStoreAsync(principal, storeId, cancellationToken) is null) return null;
        await EnsureHubStoreWritableAsync(principal, storeId, cancellationToken);
        var normalizedApplicationCode = applicationCode.Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantContextSql.ApplyAsync(
            connection,
            transaction,
            principal.UserId,
            principal.OrganizationId,
            storeId,
            "HUB",
            null,
            cancellationToken);

        await using var manifest = new NpgsqlCommand(
            """
            select r.code, r.name,
                   r.status = 'ACTIVE' and r.lifecycle_status not in ('DEPRECATED', 'RETIRED'),
                   coalesce(binding.status, 'DISABLED'),
                   r.config_schema_refs
            from aevo_application_registry r
            left join aevo_store_application_bindings binding on binding.app_code = r.code
              and binding.organization_id = @organization_id and binding.store_id = @store_id
            where r.code = @application_code
              and r.store_scoped = true
              and r.owner_repository <> 'aevo-digital-sing'
            """,
            connection,
            transaction);
        manifest.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        manifest.Parameters.AddWithValue("store_id", storeId);
        manifest.Parameters.AddWithValue("application_code", normalizedApplicationCode);
        await using var manifestReader = await manifest.ExecuteReaderAsync(cancellationToken);
        if (!await manifestReader.ReadAsync(cancellationToken)) return null;
        var manifestCode = manifestReader.GetString(0);
        var manifestName = manifestReader.GetString(1);
        var applicationActive = manifestReader.GetBoolean(2);
        var applicationEnabled = string.Equals(manifestReader.GetString(3), "ACTIVE", StringComparison.Ordinal);
        var schemaRefs = ReadJsonStringArray(manifestReader, 4);
        await manifestReader.CloseAsync();

        if (!applicationActive)
        {
            throw new HubApplicationConfigurationValidationException(
                "APPLICATION_DISABLED",
                "The application is disabled in the Core registry.",
                409);
        }
        if (!applicationEnabled)
        {
            throw new HubApplicationConfigurationValidationException(
                "STORE_APPLICATION_DISABLED",
                "Enable the application for this store before changing its configuration.",
                409);
        }

        var schemaRef = JsonString(body, "schemaRef")?.Trim() ?? string.Empty;
        var schemaVersion = JsonString(body, "schemaVersion")?.Trim() ?? string.Empty;
        if (schemaRef.Length is < 2 or > 64 || schemaVersion.Length is < 1 or > 16)
        {
            throw new HubApplicationConfigurationValidationException(
                "INVALID_CONFIGURATION_SCHEMA",
                "A valid schemaRef and schemaVersion are required.");
        }
        if (!schemaRefs.Contains(schemaRef, StringComparer.Ordinal))
        {
            throw new HubApplicationConfigurationValidationException(
                "CONFIGURATION_SCOPE_MISMATCH",
                "The requested schema is not registered for this application.");
        }
        if (!HubApplicationConfigurationCatalog.TryGet(manifestCode, schemaRef, out var definition))
        {
            throw new HubApplicationConfigurationValidationException(
                "CONFIGURATION_SCHEMA_UNSUPPORTED",
                "The application schema is registered but has no Core adapter yet.",
                409);
        }
        if (!string.Equals(definition.SchemaVersion, schemaVersion, StringComparison.Ordinal))
        {
            throw new HubApplicationConfigurationValidationException(
                "CONFIGURATION_SCHEMA_VERSION_MISMATCH",
                $"Schema {schemaRef} requires version {definition.SchemaVersion}.");
        }
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("config", out var config)
            || config.ValueKind != JsonValueKind.Object)
        {
            throw new HubApplicationConfigurationValidationException(
                "INVALID_CONFIGURATION",
                "config must be a JSON object matching the registered schema.");
        }

        var normalizedConfig = ValidateHubApplicationConfig(definition, config);
        var before = await ReadJsonAsync(
            connection,
            transaction,
            "select coalesce((select to_jsonb(configuration) from aevo_store_application_configurations configuration where configuration.organization_id=@organization_id and configuration.store_id=@store_id and configuration.app_code=@application_code and configuration.schema_ref=@schema_ref),'null'::jsonb)",
            cancellationToken,
            ("organization_id", principal.OrganizationId),
            ("store_id", storeId),
            ("application_code", manifestCode),
            ("schema_ref", schemaRef));
        await using (var upsert = new NpgsqlCommand(
            """
            insert into aevo_store_application_configurations
              (organization_id, store_id, app_code, schema_ref, schema_version, config, updated_by)
            values
              (@organization_id, @store_id, @application_code, @schema_ref, @schema_version, @config, @updated_by)
            on conflict (organization_id, store_id, app_code, schema_ref) do update set
              schema_version = excluded.schema_version,
              config = excluded.config,
              updated_by = excluded.updated_by,
              updated_at = now()
            """,
            connection,
            transaction))
        {
            upsert.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            upsert.Parameters.AddWithValue("store_id", storeId);
            upsert.Parameters.AddWithValue("application_code", manifestCode);
            upsert.Parameters.AddWithValue("schema_ref", schemaRef);
            upsert.Parameters.AddWithValue("schema_version", definition.SchemaVersion);
            upsert.Parameters.Add(new NpgsqlParameter("config", NpgsqlDbType.Jsonb) { Value = normalizedConfig.GetRawText() });
            upsert.Parameters.AddWithValue("updated_by", principal.UserId);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(new
        {
            organizationId = principal.OrganizationId,
            storeId,
            applicationCode = manifestCode,
            schemaRef,
            schemaVersion = definition.SchemaVersion,
            config = normalizedConfig
        });
        await InsertAuditAsync(
            connection,
            transaction,
            principal.UserId,
            "HUB",
            "HUB_STORE_APPLICATION_CONFIGURATION_UPDATED",
            "store_application_configuration",
            $"{storeId:D}:{manifestCode}:{schemaRef}",
            $"Updated {manifestCode} configuration schema {schemaRef}.",
            before,
            after,
            requestId,
            cancellationToken);
        var eventPayload = JsonSerializer.SerializeToElement(new
        {
            organizationId = principal.OrganizationId,
            storeId,
            applicationCode = manifestCode,
            schemaRef,
            schemaVersion = definition.SchemaVersion,
            config = normalizedConfig
        });
        await using (var outbox = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values ('HUB_STORE_APPLICATION_CONFIGURATION_CHANGED', 'store_application_configuration', @aggregate_id, @payload)",
            connection,
            transaction))
        {
            outbox.Parameters.AddWithValue("aggregate_id", $"{principal.OrganizationId:D}:{storeId:D}:{manifestCode}:{schemaRef}");
            outbox.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = eventPayload.GetRawText() });
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetHubStoreApplicationConfigurationAsync(principal, storeId, manifestCode, cancellationToken);
    }

    public async Task<IReadOnlyList<HubFavoriteRecord>> ListHubFavoritesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select f.id,f.user_id,f.organization_id,f.store_id,f.kind,f.target_key,f.label,f.href,f.icon_key,f.position,f.created_at,f.updated_at
            from public.user_navigation_favorites f
            where f.user_id=@user_id
              and (
                f.kind = 'MENU'
                or exists (
                  select 1 from public.memberships m
                  where m.user_id=f.user_id and m.organization_id=f.organization_id and m.status='ACTIVE'
                    and exists (
                      select 1 from aevo_application_assignments aa
                      where aa.user_id=m.user_id
                        and (aa.organization_id is null or aa.organization_id=m.organization_id)
                        and aa.app_code='HUB'
                        and aa.status='active' and aa.starts_at <= now()
                        and (aa.expires_at is null or aa.expires_at > now())
                    )
                )
                or exists (
                  select 1 from public.memberships m
                  join public.stores s on s.organization_id=m.organization_id and s.id=f.store_id and s.status='ACTIVE'
                  where m.user_id=f.user_id and m.status='ACTIVE'
                    and exists (
                      select 1 from aevo_application_assignments aa
                      where aa.user_id=m.user_id
                        and (aa.organization_id is null or aa.organization_id=m.organization_id)
                        and aa.app_code='HUB'
                        and aa.status='active' and aa.starts_at <= now()
                        and (aa.expires_at is null or aa.expires_at > now())
                    )
                    and (m.role_id in (select id from public.roles where code in ('OWNER','ADMIN'))
                         or exists (select 1 from public.membership_stores ms where ms.membership_id=m.id and ms.store_id=s.id))
                )
              )
            order by f.position,f.created_at
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubFavoriteRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(CanonicalizeFavorite(ReadFavorite(reader)));
        return result;
    }

    public async Task<HubFavoriteRecord> UpsertHubFavoriteAsync(Guid userId, HubPrincipalRecord? principal, JsonElement body, CancellationToken cancellationToken)
    {
        var kind = RequiredJsonString(body, "kind", 32).ToUpperInvariant();
        var targetKey = RequiredJsonString(body, "targetKey", 160);
        var targetId = JsonGuid(body, "targetId");
        var label = RequiredJsonString(body, "label", 160);
        var href = RequiredJsonString(body, "href", 500);
        var iconKey = JsonString(body, "iconKey") ?? "pin";
        var position = JsonInt(body, "position") ?? 0;
        if (kind is not ("MENU" or "ORGANIZATION" or "STORE")) throw new CoreDatabaseException("The favorite kind is not supported.");
        if (kind is "ORGANIZATION" or "STORE" && principal is null) throw new CoreDatabaseException("A Hub organization membership is required for this favorite.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into public.user_navigation_favorites (user_id, organization_id, store_id, kind, target_key, label, href, icon_key, position)
            values (@user_id, @organization_id, @store_id, @kind, @target_key, @label, @href, @icon_key, @position)
            on conflict (user_id, target_key) do update set label=excluded.label, href=excluded.href, icon_key=excluded.icon_key, position=excluded.position, updated_at=now()
            returning id,user_id,organization_id,store_id,kind,target_key,label,href,icon_key,position,created_at,updated_at
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        AddNullableGuid(command, "organization_id", principal?.OrganizationId);
        AddNullableGuid(command, "store_id", kind == "STORE" ? targetId : null);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("target_key", targetKey);
        command.Parameters.AddWithValue("label", label);
        command.Parameters.AddWithValue("href", href);
        command.Parameters.AddWithValue("icon_key", iconKey);
        command.Parameters.AddWithValue("position", Math.Clamp(position, 0, 10000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Favorite creation failed.");
        return ReadFavorite(reader);
    }

    public async Task<bool> DeleteHubFavoriteAsync(Guid userId, Guid favoriteId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("delete from public.user_navigation_favorites where user_id=@user_id and id=@id", connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", favoriteId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<JsonElement> GetHubPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select jsonb_build_object('userId',id,'locale',locale,'updatedAt',updated_at) from public.user_profiles where id=@id", connection);
        command.Parameters.AddWithValue("id", userId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ParseJson(value, $"{{\"userId\":\"{userId}\",\"locale\":null,\"updatedAt\":null}}");
    }

    public async Task<JsonElement> UpdateHubPreferencesAsync(Guid userId, string locale, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update public.user_profiles set locale=@locale, updated_at=now() where id=@id returning jsonb_build_object('userId',id,'locale',locale,'updatedAt',updated_at)", connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("locale", locale);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null) throw new CoreDatabaseException("User profile is not configured.");
        return ParseJson(value, "{}");
    }

    public async Task<IReadOnlyList<HubDeviceRecord>> ListHubDevicesAsync(HubPrincipalRecord principal, Guid? storeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select d.id,d.organization_id,d.store_id,d.name,d.mode,d.station_id,d.status,d.paired_at,d.last_seen_at,d.pairing_expires_at,d.created_at
            from public.devices d
            where d.organization_id=@organization_id and (@store_id is null or d.store_id=@store_id)
              and (@global_access or exists (select 1 from public.membership_stores ms where ms.membership_id=@membership_id and ms.store_id=d.store_id))
            order by d.created_at desc
            """, connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        AddNullableGuid(command, "store_id", storeId);
        command.Parameters.AddWithValue("membership_id", principal.MembershipId);
        command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubDeviceRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDevice(reader));
        return result;
    }

    public async Task<HubDeviceRecord> CreateHubDeviceAsync(HubPrincipalRecord principal, JsonElement body, string pairingCodeHash, DateTimeOffset pairingExpiresAt, CancellationToken cancellationToken)
    {
        var storeId = JsonGuid(body, "storeId") ?? throw new CoreDatabaseException("A store is required.");
        var name = RequiredJsonString(body, "name", 64);
        var mode = JsonString(body, "mode") ?? "POS";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into public.devices (organization_id,store_id,name,mode,status,pairing_code_hash,pairing_expires_at) values (@organization_id,@store_id,@name,@mode,'ACTIVE',@pairing_code_hash,@pairing_expires_at) returning id,organization_id,store_id,name,mode,station_id,status,paired_at,last_seen_at,pairing_expires_at,created_at",
            connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("mode", mode);
        command.Parameters.AddWithValue("pairing_code_hash", pairingCodeHash);
        command.Parameters.AddWithValue("pairing_expires_at", pairingExpiresAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Device creation failed.");
        return ReadDevice(reader);
    }

    public async Task<bool> RevokeHubDeviceAsync(HubPrincipalRecord principal, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update public.devices set status='REVOKED', pairing_code_hash=null, device_token_hash=null where id=@id and organization_id=@organization_id", connection);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task<IReadOnlyList<HubStoreTemplateRecord>> ListHubTemplatesAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return ListHubTemplatesFromDatabaseAsync(principal, cancellationToken);
        }

        var cacheKey = AevoCacheKeys.HubTemplates(principal.OrganizationId);
        return memoryCache.GetOrCreateAsync(
            cacheKey,
            TimeSpan.FromSeconds(60),
            ct => ListHubTemplatesFromDatabaseAsync(principal, ct),
            cancellationToken);
    }

    private Task<IReadOnlyList<HubStoreTemplateRecord>> ListHubTemplatesFromDatabaseAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "hub.templates.list",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("select id,organization_id,name,source_store_id,created_by,created_at,updated_at from public.store_templates where organization_id=@organization_id order by created_at desc", connection);
                command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var result = new List<HubStoreTemplateRecord>();
                while (await reader.ReadAsync(cancellationToken)) result.Add(new HubStoreTemplateRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), NullableGuid(reader, 3), reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
                return (IReadOnlyList<HubStoreTemplateRecord>)result;
            });

    public async Task<HubStoreTemplateRecord> CreateHubTemplateAsync(HubPrincipalRecord principal, JsonElement body, CancellationToken cancellationToken)
    {
        var sourceStoreId = JsonGuid(body, "sourceStoreId") ?? throw new CoreDatabaseException("A source store is required.");
        var name = RequiredJsonString(body, "name", 120);
        await EnsureHubStoreWritableAsync(principal, sourceStoreId, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into public.store_templates (organization_id,name,source_store_id,created_by,snapshot) select @organization_id,@name,s.id,@user_id,jsonb_build_object('store',jsonb_build_object('timezone',s.timezone,'currency',s.currency,'storeMode',s.store_mode),'applications',coalesce((select jsonb_agg(jsonb_build_object('applicationCode',binding.app_code,'status',binding.status)) from aevo_store_application_bindings binding where binding.organization_id=s.organization_id and binding.store_id=s.id),'[]'::jsonb),'applicationConfigurations',coalesce((select jsonb_agg(jsonb_build_object('applicationCode',configuration.app_code,'schemaRef',configuration.schema_ref,'schemaVersion',configuration.schema_version,'config',configuration.config)) from aevo_store_application_configurations configuration where configuration.organization_id=s.organization_id and configuration.store_id=s.id),'[]'::jsonb)) from public.stores s where s.id=@source_store_id and s.organization_id=@organization_id returning id,organization_id,name,source_store_id,created_by,created_at,updated_at",
            connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("source_store_id", sourceStoreId);
        command.Parameters.AddWithValue("user_id", principal.UserId);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CoreDatabaseException("Store template source store was not found.");
        InvalidateHubTemplateCache(principal.OrganizationId);
        return new HubStoreTemplateRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), NullableGuid(reader, 3), reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<bool> DeleteHubTemplateAsync(HubPrincipalRecord principal, Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("delete from public.store_templates where id=@id and organization_id=@organization_id", connection);
        command.Parameters.AddWithValue("id", templateId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted) InvalidateHubTemplateCache(principal.OrganizationId);
        return deleted;
    }

    public async Task<HubStoreRecord?> InstantiateHubTemplateAsync(HubPrincipalRecord principal, Guid templateId, JsonElement body, CancellationToken cancellationToken)
    {
        var name = RequiredJsonString(body, "name", 160);
        var code = RequiredJsonString(body, "code", 32).Trim().ToUpperInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select * from public.aevo_core_create_store_from_template(@organization_id,@template_id,@name,@code,@created_by,@public_slug)", connection, transaction);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        command.Parameters.AddWithValue("template_id", templateId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("created_by", principal.UserId);
        AddNullableText(command, "public_slug", JsonString(body, "publicSlug"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var store = new HubStoreRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(3),
            reader.GetString(2),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(10),
            reader.GetString(6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableString(reader, 9),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await reader.CloseAsync();

        await using (var configurations = new NpgsqlCommand(
            """
            insert into aevo_store_application_configurations
              (organization_id, store_id, app_code, schema_ref, schema_version, config, updated_by)
            select
              @organization_id,
              @store_id,
              snapshot_config->>'applicationCode',
              snapshot_config->>'schemaRef',
              snapshot_config->>'schemaVersion',
              snapshot_config->'config',
              @updated_by
            from public.store_templates template
            cross join lateral jsonb_array_elements(coalesce(template.snapshot->'applicationConfigurations', '[]'::jsonb)) snapshot_config
            join aevo_application_registry registry on registry.code = snapshot_config->>'applicationCode'
              and registry.store_scoped = true
              and registry.owner_repository <> 'aevo-digital-sing'
            where template.id = @template_id
              and template.organization_id = @organization_id
              and snapshot_config->>'schemaRef' is not null
              and snapshot_config->>'schemaVersion' is not null
            on conflict (organization_id, store_id, app_code, schema_ref) do nothing
            """,
            connection,
            transaction))
        {
            configurations.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            configurations.Parameters.AddWithValue("store_id", store.Id);
            configurations.Parameters.AddWithValue("template_id", templateId);
            configurations.Parameters.AddWithValue("updated_by", principal.UserId);
            await configurations.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateHubStoresCache(principal.OrganizationId, principal.MembershipId);
        InvalidateAuthorizationCaches();
        return store;
    }

    public async Task<IReadOnlyList<HubMemberRecord>> ListHubMembersAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select m.id,m.user_id,u.email,u.display_name,r.code,m.status,m.created_at,
              coalesce(array_agg(distinct ms.store_id) filter (where ms.store_id is not null),'{}')
            from public.memberships m join public.user_profiles u on u.id=m.user_id join public.roles r on r.id=m.role_id
            left join public.membership_stores ms on ms.membership_id=m.id
            where m.organization_id=@organization_id group by m.id,m.user_id,u.email,u.display_name,r.code,m.status,m.created_at order by m.created_at
            """, connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<HubMemberRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new HubMemberRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<Guid[]>(7), reader.GetFieldValue<DateTimeOffset>(6)));
        return result;
    }

    public async Task<HubMemberRecord?> UpdateHubMemberAsync(HubPrincipalRecord principal, Guid membershipId, JsonElement body, CancellationToken cancellationToken)
    {
        var roleCode = JsonString(body, "role");
        var status = JsonString(body, "status");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update public.memberships m set role_id=coalesce((select id from public.roles where code=@role),m.role_id), status=coalesce(@status,m.status), updated_at=now() where m.id=@membership_id and m.organization_id=@organization_id", connection);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        AddNullableText(command, "role", roleCode);
        AddNullableText(command, "status", status);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using (var permissions = new NpgsqlCommand(
            """
            update aevo_application_assignments aa
            set permissions = coalesce((
                    select array_agg(distinct rp.permission_code)
                    from public.memberships m
                    left join public.role_permissions rp on rp.role_id = m.role_id
                    where m.id = @membership_id
                      and aa.user_id = m.user_id
                      and aa.organization_id = m.organization_id
                ), '{}'::text[]),
                status = case when (select status from public.memberships where id = @membership_id) = 'ACTIVE' then 'active' else 'disabled' end,
                updated_at = now()
            where aa.organization_id = @organization_id
              and aa.user_id = (select user_id from public.memberships where id = @membership_id)
            """, connection))
        {
            permissions.Parameters.AddWithValue("membership_id", membershipId);
            permissions.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            await permissions.ExecuteNonQueryAsync(cancellationToken);
        }
        return (await ListHubMembersAsync(principal, cancellationToken)).FirstOrDefault(member => member.MembershipId == membershipId);
    }

    public async Task<bool> DeleteHubMemberAsync(HubPrincipalRecord principal, Guid membershipId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update public.memberships set status='SUSPENDED', updated_at=now() where id=@id and organization_id=@organization_id", connection);
        command.Parameters.AddWithValue("id", membershipId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            await using var revoke = new NpgsqlCommand(
                "update aevo_application_assignments set status='disabled', updated_at=now() where organization_id=@organization_id and user_id=(select user_id from public.memberships where id=@membership_id)",
                connection);
            revoke.Parameters.AddWithValue("membership_id", membershipId);
            revoke.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        if (changed) InvalidateAuthorizationCaches();
        return changed;
    }

    public async Task<HubMemberRecord?> CreateHubMemberAsync(HubPrincipalRecord principal, JsonElement body, CancellationToken cancellationToken)
    {
        var email = RequiredJsonString(body, "email", 320).ToLowerInvariant();
        var roleCode = (JsonString(body, "role") ?? "STAFF").Trim().ToUpperInvariant();
        var storeIds = JsonGuidArray(body, "storeIds");
        var applicationCodes = JsonStringArray(body, "applicationCodes")
            .Concat(JsonStringArray(body, "applications"))
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => ApplicationCodes.All.Contains(value) && value is not "HUB" and not "ADMIN")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid userId;
        await using (var user = new NpgsqlCommand("select id from public.user_profiles where lower(email)=@email", connection, transaction))
        {
            user.Parameters.AddWithValue("email", email);
            var value = await user.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid resolvedUser) throw new CoreDatabaseException("The invited identity must register with Aevo Accounts first.");
            userId = resolvedUser;
        }
        Guid roleId;
        await using (var role = new NpgsqlCommand("select id from public.roles where code=@role", connection, transaction))
        {
            role.Parameters.AddWithValue("role", roleCode);
            var value = await role.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid resolvedRole) throw new CoreDatabaseException("The requested role is not configured.");
            roleId = resolvedRole;
        }
        Guid membershipId;
        await using (var membership = new NpgsqlCommand(
            "insert into public.memberships (organization_id,user_id,role_id,status) values (@organization_id,@user_id,@role_id,'ACTIVE') on conflict (organization_id,user_id) do update set role_id=excluded.role_id,status='ACTIVE',updated_at=now() returning id",
            connection, transaction))
        {
            membership.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            membership.Parameters.AddWithValue("user_id", userId);
            membership.Parameters.AddWithValue("role_id", roleId);
            membershipId = (Guid)(await membership.ExecuteScalarAsync(cancellationToken) ?? throw new CoreDatabaseException("Member creation failed."));
        }

        foreach (var storeId in storeIds)
        {
            await using var store = new NpgsqlCommand(
                "insert into public.membership_stores (membership_id,store_id) select @membership_id,id from public.stores where id=@store_id and organization_id=@organization_id and status='ACTIVE' on conflict (membership_id,store_id) do nothing",
                connection,
                transaction);
            store.Parameters.AddWithValue("membership_id", membershipId);
            store.Parameters.AddWithValue("store_id", storeId);
            store.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            await store.ExecuteNonQueryAsync(cancellationToken);
            await using var verifyStore = new NpgsqlCommand(
                "select 1 from public.membership_stores where membership_id=@membership_id and store_id=@store_id",
                connection,
                transaction);
            verifyStore.Parameters.AddWithValue("membership_id", membershipId);
            verifyStore.Parameters.AddWithValue("store_id", storeId);
            if (await verifyStore.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new CoreDatabaseException("The member store scope is not part of this organization.");
            }
        }

        await SyncCoreApplicationAssignmentAsync(connection, transaction, principal.OrganizationId, membershipId, "HUB", "ACTIVE", cancellationToken);

        foreach (var applicationCode in applicationCodes)
        {
            if (storeIds.Length == 0)
            {
                await SyncCoreApplicationAssignmentAsync(connection, transaction, principal.OrganizationId, membershipId, applicationCode, "ACTIVE", cancellationToken);
            }
            else
            {
                foreach (var storeId in storeIds)
                {
                    await SyncCoreApplicationAssignmentAsync(connection, transaction, principal.OrganizationId, membershipId, applicationCode, "ACTIVE", cancellationToken, storeId);
                }
            }

        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        return (await ListHubMembersAsync(principal, cancellationToken)).FirstOrDefault(member => member.MembershipId == membershipId);
    }

    public async Task<JsonElement> ListHubMemberAssignmentsAsync(HubPrincipalRecord principal, Guid membershipId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(jsonb_agg(jsonb_build_object(
              'id', coalesce(a.organization_assignment_id, a.first_assignment_id),
              'membershipId', @membership_id,
              'applicationCode', a.app_code,
              'status', case when a.is_active then 'ACTIVE' else 'REVOKED' end,
              'startsAt', a.starts_at,
              'expiresAt', a.expires_at,
              'scopes', coalesce((
                select jsonb_agg(jsonb_build_object('scopeType','STORE','scopeRef',scoped.store_id::text) order by scoped.store_id)
                from aevo_application_assignments scoped
                where scoped.user_id = a.user_id
                  and scoped.organization_id = a.organization_id
                  and scoped.app_code = a.app_code
                  and scoped.store_id is not null
                  and scoped.status = 'active'
              ), '[]'::jsonb)
            ) order by a.app_code), '[]'::jsonb)
            from (
              select aa.user_id,
                     aa.organization_id,
                     aa.app_code,
                     (min(aa.id::text) filter (where aa.store_id is null))::uuid as organization_assignment_id,
                     (min(aa.id::text))::uuid as first_assignment_id,
                     bool_or(aa.status = 'active') as is_active,
                     min(aa.starts_at) as starts_at,
                     max(aa.expires_at) as expires_at
              from aevo_application_assignments aa
              join public.memberships m on m.user_id = aa.user_id and m.organization_id = aa.organization_id
              where m.id = @membership_id
                and m.organization_id = @organization_id
              group by aa.user_id, aa.organization_id, aa.app_code
            ) a
            """,
            connection);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
    }

    public async Task<JsonElement> ListHubMemberAssignmentsAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(jsonb_agg(jsonb_build_object(
              'id', coalesce(a.organization_assignment_id, a.first_assignment_id),
              'membershipId', a.membership_id,
              'applicationCode', a.app_code,
              'status', case when a.is_active then 'ACTIVE' else 'REVOKED' end,
              'startsAt', a.starts_at,
              'expiresAt', a.expires_at,
              'scopes', coalesce((
                select jsonb_agg(jsonb_build_object('scopeType','STORE','scopeRef',scoped.store_id::text) order by scoped.store_id)
                from aevo_application_assignments scoped
                where scoped.user_id = a.user_id
                  and scoped.organization_id = a.organization_id
                  and scoped.app_code = a.app_code
                  and scoped.store_id is not null
                  and scoped.status = 'active'
              ), '[]'::jsonb)
            ) order by a.membership_id, a.app_code), '[]'::jsonb)
            from (
              select m.id as membership_id,
                     aa.user_id,
                     aa.organization_id,
                     aa.app_code,
                     (min(aa.id::text) filter (where aa.store_id is null))::uuid as organization_assignment_id,
                     (min(aa.id::text))::uuid as first_assignment_id,
                     bool_or(aa.status = 'active') as is_active,
                     min(aa.starts_at) as starts_at,
                     max(aa.expires_at) as expires_at
              from aevo_application_assignments aa
              join public.memberships m on m.user_id = aa.user_id and m.organization_id = aa.organization_id
              where m.organization_id = @organization_id
              group by m.id, aa.user_id, aa.organization_id, aa.app_code
            ) a
            """,
            connection);
        command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
    }

    public async Task<JsonElement?> UpdateHubMemberAssignmentAsync(HubPrincipalRecord principal, Guid membershipId, string applicationCode, JsonElement body, CancellationToken cancellationToken)
    {
        var normalizedApplication = applicationCode.Trim().ToUpperInvariant();
        if (!ApplicationCodes.All.Contains(normalizedApplication)) return null;
        var status = JsonString(body, "status")?.Trim().ToUpperInvariant() ?? "ACTIVE";
        var storeIds = JsonGuidArray(body, "storeIds");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var membership = new NpgsqlCommand(
            "select 1 from public.memberships where id=@membership_id and organization_id=@organization_id",
            connection,
            transaction))
        {
            membership.Parameters.AddWithValue("membership_id", membershipId);
            membership.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            if (await membership.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        foreach (var storeId in storeIds)
        {
            await using var store = new NpgsqlCommand(
                "select 1 from public.stores where id=@store_id and organization_id=@organization_id and status='ACTIVE'",
                connection,
                transaction);
            store.Parameters.AddWithValue("store_id", storeId);
            store.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            if (await store.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var disable = new NpgsqlCommand(
            "update aevo_application_assignments set status='disabled', updated_at=now() where user_id=(select user_id from public.memberships where id=@membership_id and organization_id=@organization_id) and organization_id=@organization_id and app_code=@application_code",
            connection,
            transaction))
        {
            disable.Parameters.AddWithValue("membership_id", membershipId);
            disable.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            disable.Parameters.AddWithValue("application_code", normalizedApplication);
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        if (storeIds.Length > 0)
        {
            foreach (var storeId in storeIds)
            {
                await SyncCoreApplicationAssignmentAsync(
                    connection,
                    transaction,
                    principal.OrganizationId,
                    membershipId,
                    normalizedApplication,
                    status,
                    cancellationToken,
                    storeId);
            }
        }
        else
        {
            await SyncCoreApplicationAssignmentAsync(
                connection,
                transaction,
                principal.OrganizationId,
                membershipId,
                normalizedApplication,
                status,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        InvalidateAuthorizationCaches();
        var assignments = await ListHubMemberAssignmentsAsync(principal, membershipId, cancellationToken);
        if (assignments.ValueKind != JsonValueKind.Array) return null;
        foreach (var assignment in assignments.EnumerateArray())
        {
            if (assignment.TryGetProperty("applicationCode", out var code)
                && string.Equals(code.GetString(), normalizedApplication, StringComparison.Ordinal))
            {
                return assignment.Clone();
            }
        }
        return null;
    }

    public async Task<JsonElement> GetHubEntitlementsAsync(Guid organizationId, string? appId, Guid? storeId, CancellationToken cancellationToken)
    {
        var featureKey = appId is null
            ? null
            : EntitlementEvaluator.FeatureKeyForApplication(appId) ?? appId.Trim().ToLowerInvariant();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select coalesce(jsonb_agg(jsonb_build_object('featureKey',e.feature_key,'enabled',e.is_enabled,'limitValue',e.limit_value,'source',case when e.custom_override then 'organization' else lower(e.source) end) order by e.feature_key),'[]'::jsonb) from aevo_organization_entitlements e where e.organization_id=@organization_id and (@app_id is null or lower(e.feature_key)=lower(@app_id))",
            connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableText(command, "app_id", featureKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return ParseJson(value, "[]");
    }

    public async Task<JsonElement> GetHubResolvedEntitlementsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select jsonb_build_object(
              'organizationId', @organization_id,
              'planId', coalesce(subscription.plan_id, ''),
              'status', coalesce(subscription.status, 'INACTIVE'),
              'features', coalesce((
                select jsonb_object_agg(entitlement.feature_key, entitlement.is_enabled order by entitlement.feature_key)
                from aevo_organization_entitlements entitlement
                where entitlement.organization_id = @organization_id
              ), '{}'::jsonb),
              'limits', coalesce((
                select jsonb_object_agg(entitlement.feature_key, entitlement.limit_value order by entitlement.feature_key)
                from aevo_organization_entitlements entitlement
                where entitlement.organization_id = @organization_id
              ), '{}'::jsonb),
              'usage', jsonb_build_object(
                'store_application_bindings', (
                  select count(*)::integer
                  from aevo_store_application_bindings binding
                  where binding.organization_id = @organization_id
                    and binding.status = 'ACTIVE'
                ),
                'stores', (
                  select count(*)::integer
                  from public.stores store
                  where store.organization_id = @organization_id
                    and store.status = 'ACTIVE'
                )
              )
            )
            from (select 1) anchor
            left join lateral (
              select plan_id, status
              from aevo_organization_subscriptions
              where organization_id = @organization_id
              order by updated_at desc, created_at desc
              limit 1
            ) subscription on true
            """, connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "{}");
    }

    public async Task<JsonElement> GetHubSubscriptionsAsync(Guid organizationId, Guid? storeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(
              jsonb_agg(
                jsonb_build_object(
                  'id', concat(installation.organization_id::text, ':', installation.app_code),
                  'organizationId', installation.organization_id,
                  'storeId', null,
                  'appId', case when registry.code = 'PLAY' then 'booking' else lower(registry.code) end,
                  'status', installation.status,
                  'planCode', coalesce(installation.plan_code, subscription.plan_id, 'STANDARD'),
                  'trialEndsAt', coalesce(installation.trial_ends_at, subscription.trial_end),
                  'currentPeriodStartsAt', coalesce(installation.current_period_starts_at, subscription.current_period_start),
                  'currentPeriodEndsAt', coalesce(installation.current_period_ends_at, subscription.current_period_end, installation.grace_period_ends_at),
                  'gracePeriodEndsAt', installation.grace_period_ends_at,
                  'isEntitled', coalesce((
                    installation.status in ('TRIALING', 'ACTIVE', 'PAST_DUE', 'GRACE_PERIOD')
                    and (
                      (
                        coalesce(installation.plan_code, subscription.plan_id) = 'starter'
                        and coalesce(subscription.status, installation.status) = 'ACTIVE'
                      )
                      or coalesce(
                        installation.grace_period_ends_at,
                        installation.current_period_ends_at,
                        subscription.current_period_end,
                        installation.trial_ends_at,
                        subscription.trial_end
                      ) >= now()
                    )
                  ), false),
                  'daysRemaining', case
                    when coalesce(installation.plan_code, subscription.plan_id) = 'starter'
                      and coalesce(subscription.status, installation.status) = 'ACTIVE'
                    then null
                    when coalesce(
                      installation.grace_period_ends_at,
                      installation.current_period_ends_at,
                      subscription.current_period_end,
                      installation.trial_ends_at,
                      subscription.trial_end
                    ) is null then null
                    else greatest(
                      0,
                      ceil(extract(epoch from (coalesce(
                        installation.grace_period_ends_at,
                        installation.current_period_ends_at,
                        subscription.current_period_end,
                        installation.trial_ends_at,
                        subscription.trial_end
                      ) - now()) / 86400))
                    )::integer
                  end,
                  'createdAt', installation.created_at,
                  'updatedAt', installation.updated_at
                ) order by installation.app_code
              ),
              '[]'::jsonb
            )
            from aevo_application_installations installation
            join aevo_application_registry registry on registry.code = installation.app_code
            left join lateral (
              select s.plan_id, s.status, s.trial_end, s.current_period_start, s.current_period_end
              from aevo_organization_subscriptions s
              where s.organization_id = installation.organization_id
              order by s.updated_at desc, s.created_at desc
              limit 1
            ) subscription on true
            where installation.organization_id = @organization_id
              and registry.code not in ('HUB', 'ADMIN')
              and registry.owner_repository <> 'aevo-digital-sing'
              and (@store_id is null or exists (
                select 1
                from aevo_store_application_bindings binding
                where binding.organization_id = installation.organization_id
                  and binding.store_id = @store_id
                  and binding.app_code = installation.app_code
                  and binding.status = 'ACTIVE'
              ))
            """, connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableGuid(command, "store_id", storeId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
    }

    public async Task<JsonElement> GetHubCustomerProfileAsync(Guid organizationId, Guid storeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select coalesce((select to_jsonb(p) from public.customer_store_profiles p where p.organization_id=@organization_id and p.store_id=@store_id),'null'::jsonb)", connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "null");
    }

    public Task<JsonElement> ListHubCustomerProfilesAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return ListHubCustomerProfilesFromDatabaseAsync(principal, cancellationToken);
        }

        var isGlobal = principal.Role is "OWNER" or "ADMIN";
        var cacheKey = AevoCacheKeys.HubProfiles(principal.OrganizationId, isGlobal, principal.MembershipId);
        return memoryCache.GetOrCreateAsync(
            cacheKey,
            TimeSpan.FromSeconds(30),
            ct => ListHubCustomerProfilesFromDatabaseAsync(principal, ct),
            cancellationToken);
    }

    private Task<JsonElement> ListHubCustomerProfilesFromDatabaseAsync(HubPrincipalRecord principal, CancellationToken cancellationToken)
        => RequestPerformance.MeasureDatabaseAsync(
            "hub.profiles.list",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """
                    select coalesce(jsonb_agg(jsonb_build_object(
                      'storeId', p.store_id,
                      'organizationId', p.organization_id,
                      'publicSlug', p.public_slug,
                      'publicEnabled', p.public_enabled,
                      'area', p.area,
                      'category', p.category,
                      'priceRange', p.price_range,
                      'availabilityLabel', p.availability_label,
                      'description', p.description,
                      'imageUrl', p.image_url,
                      'mediaUrls', to_jsonb(p.media_urls),
                      'facilities', to_jsonb(p.facilities),
                      'policySummary', p.policy_summary,
                      'latitude', p.latitude,
                      'longitude', p.longitude,
                      'rating', p.rating,
                      'reviewCount', p.review_count,
                      'createdAt', p.created_at,
                      'updatedAt', p.updated_at
                    ) order by p.store_id), '[]'::jsonb)
                    from public.customer_store_profiles p
                    join public.stores s on s.id = p.store_id
                      and s.organization_id = p.organization_id
                      and s.status = 'ACTIVE'
                    where p.organization_id = @organization_id
                      and (
                        @global_access
                        or exists (
                          select 1
                          from public.memberships m
                          join public.membership_stores ms on ms.membership_id = m.id
                          where m.id = @membership_id
                            and m.status = 'ACTIVE'
                            and ms.store_id = p.store_id
                        )
                      )
                    """,
                    connection);
                command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
                command.Parameters.AddWithValue("membership_id", principal.MembershipId);
                command.Parameters.AddWithValue("global_access", principal.Role is "OWNER" or "ADMIN");
                return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
            });

    public async Task<JsonElement> ListHubAuditLogsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select coalesce(jsonb_agg(jsonb_build_object('id',a.id,'organizationId',a.organization_id,'userId',a.user_id,'action',a.action,'resourceType',a.resource_type,'resourceId',a.resource_id,'metadata',a.metadata,'createdAt',a.created_at) order by a.created_at desc),'[]'::jsonb) from public.audit_logs a where a.organization_id=@organization_id limit 200", connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
    }

    public async Task<JsonElement> GetHubOrganizationStatsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        return await RequestPerformance.MeasureDatabaseAsync(
            "hub.organization.stats",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("select jsonb_build_object('organizationId',@organization_id,'totalStores',(select count(*) from public.stores where organization_id=@organization_id and status='ACTIVE'),'totalMembers',(select count(*) from public.memberships where organization_id=@organization_id and status='ACTIVE'),'activeApps',(select count(distinct app_code) from aevo_store_application_bindings where organization_id=@organization_id and status='ACTIVE'),'activeDevices',(select count(*) from public.devices where organization_id=@organization_id and status='ACTIVE'))", connection);
                command.Parameters.AddWithValue("organization_id", organizationId);
                return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "{}");
            });
    }

    public Task<JsonElement> GetHubDashboardProjectionAsync(
        Guid organizationId,
        Guid? storeId,
        string environment,
        CancellationToken cancellationToken)
    {
        if (memoryCache is null)
        {
            return GetHubDashboardProjectionFromDatabaseAsync(organizationId, storeId, environment, cancellationToken);
        }

        return memoryCache.GetOrCreateAsync(
            AevoCacheKeys.HubDashboardProjection(organizationId, storeId),
            TimeSpan.FromMinutes(5),
            ct => GetHubDashboardProjectionFromDatabaseAsync(organizationId, storeId, environment, ct),
            cancellationToken);
    }

    private async Task<JsonElement> GetHubDashboardProjectionFromDatabaseAsync(
        Guid organizationId,
        Guid? storeId,
        string environment,
        CancellationToken cancellationToken)
    {
        JsonElement? storedProjection = null;
        DateTimeOffset? generatedAt = null;
        var stats = ParseJson(null, "{}");
        IReadOnlyList<ApplicationConnectionRecord> connections = Array.Empty<ApplicationConnectionRecord>();
        try
        {
            await RequestPerformance.MeasureDatabaseAsync(
                "hub.dashboard.projection.read",
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken);
                    await using var command = new NpgsqlCommand(
                        """
                        -- Keep the projection read in the same exchange as the
                        -- fallback stats and connection summary below.
                        select projection, generated_at
                        from aevo_hub_dashboard_projections
                        where organization_id = @organization_id
                          and store_id is not distinct from @store_id
                        ;

                        select case when @store_id is null then
                          jsonb_build_object(
                            'organizationId', @organization_id,
                            'totalStores', (select count(*) from public.stores where organization_id = @organization_id and status = 'ACTIVE'),
                            'totalMembers', (select count(*) from public.memberships where organization_id = @organization_id and status = 'ACTIVE'),
                            'activeApps', (select count(distinct app_code) from aevo_store_application_bindings where organization_id = @organization_id and status = 'ACTIVE'),
                            'activeDevices', (select count(*) from public.devices where organization_id = @organization_id and status = 'ACTIVE')
                          )
                        else
                          jsonb_build_object(
                            'storeId', @store_id,
                            'products', (select count(*) from public.product_availability where organization_id = @organization_id and store_id = @store_id),
                            'devices', (select count(*) from public.devices where organization_id = @organization_id and store_id = @store_id and status = 'ACTIVE')
                          )
                        end;

                        select coalesce(jsonb_agg(jsonb_build_object(
                          'appCode', r.code,
                          'label', r.name,
                          'status', coalesce(c.status, 'not_configured'),
                          'baseUrl', c.base_url,
                          'checkedAt', c.checked_at,
                          'latencyMs', c.latency_ms,
                          'lastErrorCode', c.last_error_code,
                          'metadata', c.metadata
                        ) order by r.code), '[]'::jsonb)
                        from aevo_application_registry r
                        left join aevo_application_connections c
                          on c.app_code = r.code and c.environment = @environment
                        """, connection);
                    command.Parameters.AddWithValue("organization_id", organizationId);
                    AddNullableGuid(command, "store_id", storeId);
                    command.Parameters.AddWithValue("environment", environment);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        if (!reader.IsDBNull(0)) storedProjection = ParseJson(reader.GetValue(0), "{}");
                        if (!reader.IsDBNull(1)) generatedAt = reader.GetFieldValue<DateTimeOffset>(1);
                    }
                    if (!await reader.NextResultAsync(cancellationToken) || !await reader.ReadAsync(cancellationToken))
                    {
                        throw new CoreDatabaseException("Hub dashboard stats result was unavailable.");
                    }
                    stats = ParseJson(reader.GetValue(0), "{}");

                    if (!await reader.NextResultAsync(cancellationToken) || !await reader.ReadAsync(cancellationToken))
                    {
                        throw new CoreDatabaseException("Hub dashboard connection result was unavailable.");
                    }
                    var connectionPayload = ParseJson(reader.GetValue(0), "[]");
                    connections = JsonSerializer.Deserialize<ApplicationConnectionRecord[]>(
                        connectionPayload.GetRawText(),
                        HubDashboardJsonOptions) ?? [];
                });

            if (storedProjection.HasValue && generatedAt.HasValue && generatedAt.Value >= DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                return storedProjection.Value;
            }

            var checkedAt = connections
                .Where(connection => connection.CheckedAt.HasValue)
                .Select(connection => connection.CheckedAt!.Value)
                .DefaultIfEmpty(DateTimeOffset.UtcNow)
                .Max();
            var partial = connections.Any(connection => connection.Status is "degraded" or "not_connected");
            var now = DateTimeOffset.UtcNow;
            var projection = BuildHubDashboardProjection(
                organizationId,
                storeId,
                stats,
                connections,
                partial ? "partial" : "fresh",
                checkedAt,
                now,
                partial ? "APPLICATION_CONNECTION_PARTIAL" : null);
            // Hub reads must not write this projection. Its RLS policy reserves
            // persistence for the platform/background projection writer; this
            // request returns the freshly computed read model and the bounded
            // L1 cache absorbs repeated navigation reads.
            return projection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            if (storedProjection.HasValue)
            {
                return MarkHubDashboardProjectionStale(storedProjection.Value, generatedAt ?? DateTimeOffset.UtcNow);
            }
            return BuildHubDashboardProjection(
                organizationId,
                storeId,
                JsonDocument.Parse("null").RootElement.Clone(),
                Array.Empty<ApplicationConnectionRecord>(),
                "unavailable",
                null,
                DateTimeOffset.UtcNow,
                "DASHBOARD_PROJECTION_UNAVAILABLE");
        }
    }

    private static JsonElement BuildHubDashboardProjection(
        Guid organizationId,
        Guid? storeId,
        JsonElement stats,
        IReadOnlyList<ApplicationConnectionRecord> connections,
        string freshnessState,
        DateTimeOffset? sourceUpdatedAt,
        DateTimeOffset generatedAt,
        string? errorCode)
    {
        return JsonSerializer.SerializeToElement(new
        {
            success = true,
            scope = new { organizationId, storeId },
            stats,
            freshness = new
            {
                state = freshnessState,
                generatedAt,
                sourceUpdatedAt,
                errorCode
            },
            sources = connections.Select(connection => new
            {
                appCode = connection.AppCode,
                status = connection.Status,
                checkedAt = connection.CheckedAt,
                latencyMs = connection.LatencyMs,
                lastErrorCode = connection.LastErrorCode
            }).ToArray()
        });
    }

    private static JsonElement MarkHubDashboardProjectionStale(JsonElement projection, DateTimeOffset generatedAt)
    {
        var stats = projection.TryGetProperty("stats", out var statsValue) ? statsValue : JsonDocument.Parse("null").RootElement.Clone();
        var sources = projection.TryGetProperty("sources", out var sourcesValue) ? sourcesValue : JsonDocument.Parse("[]").RootElement.Clone();
        return JsonSerializer.SerializeToElement(new
        {
            success = true,
            scope = projection.TryGetProperty("scope", out var scopeValue) ? scopeValue : JsonDocument.Parse("{}").RootElement.Clone(),
            stats,
            freshness = new { state = "stale", generatedAt, sourceUpdatedAt = generatedAt, errorCode = "DASHBOARD_PROJECTION_REFRESH_FAILED" },
            sources
        });
    }

    public async Task<JsonElement> ListHubIntegrationsAsync(
        Guid organizationId,
        Guid? storeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select coalesce(jsonb_agg(jsonb_build_object(
                'providerCode', manifest.provider_code,
                'name', manifest.display_name,
                'description', manifest.description,
                'ownerRepository', manifest.owner_repository,
                'manifestVersion', manifest.manifest_version,
                'scopes', to_jsonb(manifest.scopes),
                'secretRefRequired', manifest.secret_ref_required,
                'lifecycleStatus', manifest.lifecycle_status,
                'connection', case when integration.provider_code is null then null else jsonb_build_object(
                    'status', integration.status,
                    'secretConfigured', integration.secret_ref is not null,
                    'consentedAt', integration.consented_at,
                    'connectedAt', integration.connected_at,
                    'revokedAt', integration.revoked_at,
                    'lastErrorCode', integration.last_error_code,
                    'updatedAt', integration.updated_at
                ) end
            ) order by manifest.provider_code), '[]'::jsonb)
            from aevo_integration_manifests manifest
            left join aevo_organization_integrations integration
              on integration.provider_code = manifest.provider_code
             and integration.organization_id = @organization_id
             and integration.store_id is not distinct from @store_id
            where manifest.lifecycle_status in ('ACTIVE', 'BETA')
            """, connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableGuid(command, "store_id", storeId);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "[]");
    }

    public async Task<JsonElement> ConnectHubIntegrationAsync(
        HubPrincipalRecord principal,
        string providerCode,
        Guid? storeId,
        string secretRef,
        string requestId,
        CancellationToken cancellationToken)
    {
        var normalizedProviderCode = NormalizeHubIntegrationProvider(providerCode);
        var normalizedSecretRef = ValidateHubIntegrationSecretReference(secretRef);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var manifest = await ReadJsonAsync(
            connection,
            transaction,
            "select jsonb_build_object('providerCode', provider_code, 'status', lifecycle_status, 'secretRefRequired', secret_ref_required) from aevo_integration_manifests where provider_code=@provider_code",
            cancellationToken,
            ("provider_code", normalizedProviderCode));
        if (manifest is null) throw new HubIntegrationValidationException("INTEGRATION_NOT_FOUND", "The requested integration is not registered.", 404);
        if (!manifest.Value.TryGetProperty("status", out var status) || status.GetString() is not ("ACTIVE" or "BETA"))
        {
            throw new HubIntegrationValidationException("INTEGRATION_UNAVAILABLE", "The requested integration is not available.", 409);
        }

        var before = await ReadHubIntegrationStateAsync(connection, transaction, principal.OrganizationId, storeId, normalizedProviderCode, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var command = new NpgsqlCommand(
            """
            insert into aevo_organization_integrations
              (organization_id, store_id, provider_code, status, secret_ref, consented_at, consented_by, connected_at, revoked_at, last_error_code, updated_at)
            values
              (@organization_id, @store_id, @provider_code, 'ACTIVE', @secret_ref, @consented_at, @consented_by, @connected_at, null, null, @updated_at)
            on conflict (scope_key, provider_code) do update set
              status = 'ACTIVE',
              secret_ref = excluded.secret_ref,
              consented_at = excluded.consented_at,
              consented_by = excluded.consented_by,
              connected_at = excluded.connected_at,
              revoked_at = null,
              last_error_code = null,
              updated_at = excluded.updated_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            AddNullableGuid(command, "store_id", storeId);
            command.Parameters.AddWithValue("provider_code", normalizedProviderCode);
            command.Parameters.AddWithValue("secret_ref", normalizedSecretRef);
            command.Parameters.AddWithValue("consented_at", now);
            command.Parameters.AddWithValue("consented_by", principal.UserId);
            command.Parameters.AddWithValue("connected_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = BuildHubIntegrationState(normalizedProviderCode, storeId, "ACTIVE", true, now, now, null, now);
        await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "INTEGRATION_CONNECTED", "integration", IntegrationTargetId(normalizedProviderCode, storeId), "HUB-016 integration consent accepted", before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<JsonElement> RotateHubIntegrationSecretAsync(
        HubPrincipalRecord principal,
        string providerCode,
        Guid? storeId,
        string secretRef,
        string requestId,
        CancellationToken cancellationToken)
    {
        var normalizedProviderCode = NormalizeHubIntegrationProvider(providerCode);
        var normalizedSecretRef = ValidateHubIntegrationSecretReference(secretRef);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadHubIntegrationStateAsync(connection, transaction, principal.OrganizationId, storeId, normalizedProviderCode, cancellationToken);
        if (before is null) throw new HubIntegrationValidationException("INTEGRATION_NOT_CONNECTED", "Connect the integration before rotating its secret reference.", 404);
        var now = DateTimeOffset.UtcNow;
        await using (var command = new NpgsqlCommand(
            """
            update aevo_organization_integrations
               set status = 'ACTIVE', secret_ref = @secret_ref, consented_at = @consented_at,
                   consented_by = @consented_by, revoked_at = null, last_error_code = null, updated_at = @updated_at
             where organization_id = @organization_id
               and store_id is not distinct from @store_id
               and provider_code = @provider_code
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("secret_ref", normalizedSecretRef);
            command.Parameters.AddWithValue("consented_at", now);
            command.Parameters.AddWithValue("consented_by", principal.UserId);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            AddNullableGuid(command, "store_id", storeId);
            command.Parameters.AddWithValue("provider_code", normalizedProviderCode);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new HubIntegrationValidationException("INTEGRATION_NOT_CONNECTED", "The integration is not connected for this scope.", 404);
        }

        var after = BuildHubIntegrationState(normalizedProviderCode, storeId, "ACTIVE", true, now, null, null, now);
        await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "INTEGRATION_SECRET_ROTATED", "integration", IntegrationTargetId(normalizedProviderCode, storeId), "HUB-016 secret reference rotated", before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    public async Task<JsonElement> DisconnectHubIntegrationAsync(
        HubPrincipalRecord principal,
        string providerCode,
        Guid? storeId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var normalizedProviderCode = NormalizeHubIntegrationProvider(providerCode);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var before = await ReadHubIntegrationStateAsync(connection, transaction, principal.OrganizationId, storeId, normalizedProviderCode, cancellationToken);
        if (before is null) throw new HubIntegrationValidationException("INTEGRATION_NOT_CONNECTED", "The integration is not connected for this scope.", 404);
        var now = DateTimeOffset.UtcNow;
        await using (var command = new NpgsqlCommand(
            """
            update aevo_organization_integrations
               set status = 'REVOKED', secret_ref = null, revoked_at = @revoked_at, last_error_code = null, updated_at = @updated_at
             where organization_id = @organization_id
               and store_id is not distinct from @store_id
               and provider_code = @provider_code
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revoked_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("organization_id", principal.OrganizationId);
            AddNullableGuid(command, "store_id", storeId);
            command.Parameters.AddWithValue("provider_code", normalizedProviderCode);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new HubIntegrationValidationException("INTEGRATION_NOT_CONNECTED", "The integration is not connected for this scope.", 404);
        }

        var after = BuildHubIntegrationState(normalizedProviderCode, storeId, "REVOKED", false, before.Value.TryGetProperty("consentedAt", out var consentedAt) && consentedAt.ValueKind != JsonValueKind.Null ? consentedAt.GetDateTimeOffset() : null, null, now, now);
        await InsertAuditAsync(connection, transaction, principal.UserId, "HUB", "INTEGRATION_DISCONNECTED", "integration", IntegrationTargetId(normalizedProviderCode, storeId), "HUB-016 integration consent revoked", before, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return after;
    }

    private static async Task<JsonElement?> ReadHubIntegrationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid? storeId,
        string providerCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select jsonb_build_object(
              'providerCode', provider_code,
              'storeId', store_id,
              'status', status,
              'secretConfigured', secret_ref is not null,
              'consentedAt', consented_at,
              'connectedAt', connected_at,
              'revokedAt', revoked_at,
              'lastErrorCode', last_error_code,
              'updatedAt', updated_at
            )
            from aevo_organization_integrations
            where organization_id = @organization_id
              and store_id is not distinct from @store_id
              and provider_code = @provider_code
            """, connection, transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        AddNullableGuid(command, "store_id", storeId);
        command.Parameters.AddWithValue("provider_code", providerCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : ParseJson(value, "{}");
    }

    private static JsonElement BuildHubIntegrationState(
        string providerCode,
        Guid? storeId,
        string status,
        bool secretConfigured,
        DateTimeOffset? consentedAt,
        DateTimeOffset? connectedAt,
        DateTimeOffset? revokedAt,
        DateTimeOffset updatedAt)
    {
        return JsonSerializer.SerializeToElement(new
        {
            providerCode,
            storeId,
            status,
            secretConfigured,
            consentedAt,
            connectedAt,
            revokedAt,
            lastErrorCode = (string?)null,
            updatedAt
        });
    }

    private static string NormalizeHubIntegrationProvider(string providerCode)
    {
        var normalized = providerCode.Trim().ToUpperInvariant();
        return normalized is "LINE_OFFICIAL_ACCOUNT"
            ? normalized
            : throw new HubIntegrationValidationException("INTEGRATION_NOT_FOUND", "The requested integration is not registered.", 404);
    }

    private static string ValidateHubIntegrationSecretReference(string? secretRef)
    {
        var value = secretRef?.Trim() ?? string.Empty;
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator > 0 ? value[..separator].ToLowerInvariant() : string.Empty;
        if (value.Length is < 1 or > 256 || value.Any(char.IsWhiteSpace) || scheme is not ("secret" or "gcp-secret" or "vault" or "env"))
        {
            throw new HubIntegrationValidationException("INVALID_SECRET_REFERENCE", "Use a deployment secret reference such as secret://aevo/line/production; credential values are not accepted.");
        }

        return value;
    }

    private static string IntegrationTargetId(string providerCode, Guid? storeId) => $"{providerCode}:{storeId?.ToString() ?? "organization"}";

    public async Task<JsonElement> GetHubStoreStatsAsync(Guid organizationId, Guid storeId, CancellationToken cancellationToken)
    {
        return await RequestPerformance.MeasureDatabaseAsync(
            "hub.store.stats",
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("select jsonb_build_object('storeId',@store_id,'products',(select count(*) from public.product_availability where organization_id=@organization_id and store_id=@store_id),'devices',(select count(*) from public.devices where organization_id=@organization_id and store_id=@store_id and status='ACTIVE'))", connection);
                command.Parameters.AddWithValue("organization_id", organizationId);
                command.Parameters.AddWithValue("store_id", storeId);
                return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "{}");
            });
    }

    public async Task<JsonElement> UpsertHubCustomerProfileAsync(Guid organizationId, Guid storeId, JsonElement body, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into public.customer_store_profiles (organization_id,store_id,public_slug,public_enabled,area,category,price_range,availability_label,description,image_url,media_urls,facilities,policy_summary,latitude,longitude,rating,review_count)
            values (@organization_id,@store_id,@public_slug,@public_enabled,@area,@category,@price_range,@availability_label,@description,@image_url,@media_urls,@facilities,@policy_summary,@latitude,@longitude,@rating,@review_count)
            on conflict (organization_id,store_id) do update set public_slug=excluded.public_slug,public_enabled=excluded.public_enabled,area=excluded.area,category=excluded.category,price_range=excluded.price_range,availability_label=excluded.availability_label,description=excluded.description,image_url=excluded.image_url,media_urls=excluded.media_urls,facilities=excluded.facilities,policy_summary=excluded.policy_summary,latitude=excluded.latitude,longitude=excluded.longitude,rating=excluded.rating,review_count=excluded.review_count,updated_at=now()
            returning to_jsonb(customer_store_profiles.*)
            """, connection);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("public_slug", RequiredJsonString(body, "publicSlug", 63));
        command.Parameters.AddWithValue("public_enabled", JsonBool(body, "publicEnabled") ?? false);
        command.Parameters.AddWithValue("area", JsonString(body, "area") ?? "");
        command.Parameters.AddWithValue("category", JsonString(body, "category") ?? "");
        command.Parameters.AddWithValue("price_range", JsonString(body, "priceRange") ?? "");
        AddNullableText(command, "availability_label", JsonString(body, "availabilityLabel"));
        AddNullableText(command, "description", JsonString(body, "description"));
        AddNullableText(command, "image_url", JsonString(body, "imageUrl"));
        AddJsonArray(command, "media_urls", body, "mediaUrls");
        AddJsonArray(command, "facilities", body, "facilities");
        AddNullableText(command, "policy_summary", JsonString(body, "policySummary"));
        AddNullableDouble(command, "latitude", JsonDouble(body, "latitude"));
        AddNullableDouble(command, "longitude", JsonDouble(body, "longitude"));
        AddNullableDouble(command, "rating", JsonDouble(body, "rating"));
        command.Parameters.AddWithValue("review_count", JsonInt(body, "reviewCount") ?? 0);
        InvalidateHubProfileCache(organizationId, Guid.Empty);
        return ParseJson(await command.ExecuteScalarAsync(cancellationToken), "null");
    }

    private static async Task SyncCoreApplicationAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid membershipId,
        string applicationCode,
        string status,
        CancellationToken cancellationToken,
        Guid? storeId = null)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_application_assignments (
              user_id, app_code, organization_id, store_id, permissions,
              status, starts_at, expires_at, created_at, updated_at
            )
            select
              m.user_id,
              @application_code,
              m.organization_id,
              @store_id,
              coalesce(array_agg(distinct rp.permission_code) filter (where rp.permission_code is not null), '{}'::text[]),
              case when @status = 'ACTIVE' then 'active' else 'disabled' end,
              now(),
              null,
              now(),
              now()
            from public.memberships m
            left join public.role_permissions rp on rp.role_id = m.role_id
            where m.id = @membership_id
              and m.organization_id = @organization_id
            group by m.user_id, m.organization_id
            on conflict (
                user_id,
                app_code,
                (coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid)),
                (coalesce(store_id, '00000000-0000-0000-0000-000000000000'::uuid))
            ) do update set
              permissions = excluded.permissions,
              status = excluded.status,
              starts_at = excluded.starts_at,
              expires_at = excluded.expires_at,
              updated_at = now()
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("application_code", applicationCode.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("status", status.Trim().ToUpperInvariant());
        AddNullableGuid(command, "store_id", storeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireHubProvisioningLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_key", $"{organizationId:D}:{idempotencyKey}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HubProvisioningOperationSnapshot?> ReadHubProvisioningOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select request_hash, status, response::text, store_id, app_code, operation_type from aevo_provisioning_operations where organization_id=@organization_id and idempotency_key=@idempotency_key",
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new HubProvisioningOperationSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static HubStoreApplicationRecord ReadHubStoreApplicationResponse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        return new HubStoreApplicationRecord(
            root.GetProperty("organizationId").GetGuid(),
            root.GetProperty("storeId").GetGuid(),
            root.GetProperty("applicationCode").GetString() ?? throw new CoreDatabaseException("Provisioning response has no application code."),
            root.GetProperty("applicationName").GetString() ?? throw new CoreDatabaseException("Provisioning response has no application name."),
            root.GetProperty("status").GetString() ?? throw new CoreDatabaseException("Provisioning response has no status."),
            root.GetProperty("applicationActive").GetBoolean(),
            root.GetProperty("capabilities").Deserialize<string[]>() ?? [],
            root.GetProperty("configSchemaRefs").Deserialize<string[]>() ?? []);
    }

    private readonly record struct HubProvisioningOperationSnapshot(
        string RequestHash,
        string Status,
        string ResponseJson,
        Guid? StoreId,
        string AppCode,
        string OperationType);

    private static JsonElement ValidateHubApplicationConfig(
        HubApplicationConfigDefinition definition,
        JsonElement config)
    {
        if (config.GetRawText().Length > 16_384)
        {
            throw new HubApplicationConfigurationValidationException(
                "CONFIGURATION_TOO_LARGE",
                "The configuration is larger than the Core limit.");
        }
        if (ContainsSensitiveConfigKey(config))
        {
            throw new HubApplicationConfigurationValidationException(
                "PLAINTEXT_SECRET_FORBIDDEN",
                "Secret, token, password, credential, and private-key values must use an app-owned secret reference.");
        }

        var fields = definition.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var normalized = definition.Fields.ToDictionary(
            field => field.Key,
            field => JsonSerializer.SerializeToElement(field.DefaultValue),
            StringComparer.Ordinal);
        foreach (var property in config.EnumerateObject())
        {
            if (!fields.TryGetValue(property.Name, out var field))
            {
                throw new HubApplicationConfigurationValidationException(
                    "CONFIG_FIELD_NOT_ALLOWED",
                    $"The field {property.Name} is not part of schema {definition.SchemaRef}.");
            }

            normalized[property.Name] = field.Type switch
            {
                "boolean" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    => property.Value.Clone(),
                "integer" when property.Value.TryGetInt32(out var integer)
                    && (!field.Min.HasValue || integer >= field.Min.Value)
                    && (!field.Max.HasValue || integer <= field.Max.Value)
                    => property.Value.Clone(),
                "string" when property.Value.ValueKind == JsonValueKind.String
                    && (field.MaxLength is null || (property.Value.GetString()?.Length ?? 0) <= field.MaxLength.Value)
                    => property.Value.Clone(),
                "select" when property.Value.ValueKind == JsonValueKind.String
                    && field.Options?.Any(option => string.Equals(option.Value, property.Value.GetString(), StringComparison.Ordinal)) == true
                    => property.Value.Clone(),
                _ => throw new HubApplicationConfigurationValidationException(
                    "CONFIG_FIELD_INVALID",
                    $"The field {property.Name} does not match the typed schema.")
            };
        }

        foreach (var field in definition.Fields.Where(field => field.Required))
        {
            if (!config.TryGetProperty(field.Key, out _))
            {
                throw new HubApplicationConfigurationValidationException(
                    "CONFIG_FIELD_REQUIRED",
                    $"The field {field.Key} is required.");
            }
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static bool ContainsSensitiveConfigKey(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var key = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();
                if (key.Contains("secret", StringComparison.Ordinal)
                    || key.Contains("token", StringComparison.Ordinal)
                    || key.Contains("password", StringComparison.Ordinal)
                    || key.Contains("credential", StringComparison.Ordinal)
                    || key.Contains("privatekey", StringComparison.Ordinal)
                    || key.Contains("apikey", StringComparison.Ordinal)
                    || ContainsSensitiveConfigKey(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(ContainsSensitiveConfigKey);
        }
        return false;
    }

    private static HubOrganizationRecord ReadOrganization(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5));
    private static HubStoreRecord ReadStore(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9), NullableString(reader, 10), reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12));
    private static HubStoreDeletionRecord ReadHubStoreDeletion(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetString(3),
        reader.GetGuid(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        NullableGuid(reader, 7),
        NullableDateTimeOffset(reader, 8),
        NullableDateTimeOffset(reader, 9),
        reader.GetString(10),
        NullableString(reader, 11));

    private static async Task InsertHubAuditLogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid userId,
        string action,
        string resourceType,
        Guid resourceId,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into public.audit_logs
              (organization_id, user_id, action, resource_type, resource_id, metadata)
            values
              (@organization_id, @user_id, @action, @resource_type, @resource_id, @metadata)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("organization_id", organizationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resource_type", resourceType);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = metadata.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHubRetentionOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        string aggregateId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values (@event_type, 'data_deletion_request', @aggregate_id, @payload)",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("aggregate_id", aggregateId);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    private static HubFavoriteRecord ReadFavorite(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), NullableGuid(reader, 2), NullableGuid(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11));
    private static HubFavoriteRecord CanonicalizeFavorite(HubFavoriteRecord favorite)
    {
        var href = favorite.TargetKey switch
        {
            "organize.overview" => "/modern",
            "organize.stores" => "/modern/stores",
            "organize.team" => "/modern/settings#team-heading",
            "organize.apps" => "/modern/settings#apps-heading",
            "organize.billing" => "/modern/settings#apps-heading",
            "workspace.devices" => "/modern/stores",
            "workspace.apps" => "/modern/settings#apps-heading",
            "workspace.billing" => "/modern/settings#apps-heading",
            _ when favorite.TargetKey.StartsWith("organization:", StringComparison.Ordinal) => "/modern/settings",
            _ when favorite.TargetKey.StartsWith("store:", StringComparison.Ordinal)
                && Guid.TryParse(favorite.TargetKey["store:".Length..], out var storeId) => $"/modern/stores/{Uri.EscapeDataString(storeId.ToString())}",
            _ => favorite.Href
        };
        return favorite with { Href = href };
    }
    private static HubDeviceRecord ReadDevice(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), NullableGuid(reader, 5), reader.GetString(6), NullableDateTimeOffset(reader, 7), NullableDateTimeOffset(reader, 8), NullableDateTimeOffset(reader, 9), reader.GetFieldValue<DateTimeOffset>(10));
    private static string[] ReadJsonStringArray(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return [];
        using var document = JsonDocument.Parse(reader.GetString(ordinal));
        var payload = document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString() ?? "[]"
            : document.RootElement.GetRawText();
        return JsonSerializer.Deserialize<string[]>(payload) ?? [];
    }
    private static JsonElement ParseJson(object? value, string fallback) => value is null or DBNull ? JsonDocument.Parse(fallback).RootElement.Clone() : JsonDocument.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? fallback).RootElement.Clone();
    private static string? JsonString(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredJsonString(JsonElement body, string name, int maxLength) { var value = JsonString(body, name)?.Trim(); if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw new CoreDatabaseException($"A valid {name} is required."); return value; }
    private static Guid? JsonGuid(JsonElement body, string name) => Guid.TryParse(JsonString(body, name), out var value) ? value : null;
    private static Guid[] JsonGuidArray(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out _))
            .Select(item => Guid.Parse(item.GetString()!))
            .Distinct()
            .ToArray();
    }
    private static string[] JsonStringArray(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
    private static int? JsonInt(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static int? JsonNullableInt(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt32(out var result) ? result : null;
    private static bool? JsonBool(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static double? JsonDouble(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    private static void AddNullableText(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
    private static void AddNullableGuid(NpgsqlCommand command, string name, Guid? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = (object?)value ?? DBNull.Value });
    private static void AddNullableInt(NpgsqlCommand command, string name, int? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = (object?)value ?? DBNull.Value });
    private static void AddNullableBool(NpgsqlCommand command, string name, bool? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = (object?)value ?? DBNull.Value });
    private static void AddNullableDouble(NpgsqlCommand command, string name, double? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Double) { Value = (object?)value ?? DBNull.Value });
    private static void AddJsonArray(NpgsqlCommand command, string name, JsonElement body, string property) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetRawText() : "[]" });
    public void InvalidateHubStoresCache(Guid organizationId, Guid membershipId)
    {
        if (memoryCache is null) return;
        memoryCache.Remove($"core:hub:stores:v1:{organizationId}:all");
        memoryCache.Remove($"core:hub:stores:v1:{organizationId}:{membershipId}");
        // The consolidated Hub bootstrap contains the authorized store index;
        // invalidate all such short-lived entries after a store-scope write.
        memoryCache.RemoveByPrefix(AevoCacheKeys.HubBootstrapPrefix);
    }

    public void InvalidateHubTemplateCache(Guid organizationId)
    {
        if (memoryCache is null) return;
        memoryCache.Remove(AevoCacheKeys.HubTemplates(organizationId));
    }

    public void InvalidateHubProfileCache(Guid organizationId, Guid membershipId)
    {
        if (memoryCache is null) return;
        memoryCache.Remove($"core:hub:profiles:v1:{organizationId}:all");
        memoryCache.Remove($"core:hub:profiles:v1:{organizationId}:{membershipId}");
    }

    public void InvalidateHubOrganizationCache(Guid userId, Guid organizationId)
    {
        if (memoryCache is null) return;
        memoryCache.Remove(AevoCacheKeys.HubOrganization(userId, organizationId));
    }
}
