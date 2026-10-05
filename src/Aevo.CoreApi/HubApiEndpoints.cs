using System.Security.Cryptography;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Security;

namespace Aevo.CoreApi;

public static class HubApiEndpoints
{
    private sealed record LaunchReadinessResult(
        bool Ready,
        string Status,
        string Reason,
        string Origin,
        DateTimeOffset CheckedAt);

    private static readonly Action<ILogger, string, Guid, Guid?, bool, string, string, Exception?> EntitlementDecisionLog =
        LoggerMessage.Define<string, Guid, Guid?, bool, string, string>(
            LogLevel.Information,
            new EventId(2201, nameof(EntitlementDecisionLog)),
            "Hub entitlement decision application={Application} organization_id={OrganizationId} store_id={StoreId} allowed={Allowed} reason={Reason} projection_version={ProjectionVersion}");

    private static readonly Action<ILogger, string, Guid, Guid?, Exception?> EntitlementProjectionUnavailableLog =
        LoggerMessage.Define<string, Guid, Guid?>(
            LogLevel.Error,
            new EventId(2202, nameof(EntitlementProjectionUnavailableLog)),
            "Hub entitlement projection unavailable application={Application} organization_id={OrganizationId} store_id={StoreId}");

    private static readonly Action<ILogger, string, string, string, Exception?> ApplicationHandshakeLog =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2203, nameof(ApplicationHandshakeLog)),
            "Application compatibility probe application={Application} status={Status} reason={Reason}");

    public static void MapHubApi(this WebApplication app)
    {
        app.MapGet("/api/v1/hub/me", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { user = User(auth.Session!), principal = auth.Principal });
        });

        app.MapGet("/api/v1/hub/bootstrap", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            var session = auth.Session!;
            var organizations = await database.ListHubOrganizationsAsync(session.UserId, context.RequestAborted);
            var principal = auth.Principal ?? (organizations.Count > 0
                ? await database.ResolveHubPrincipalAsync(session with { OrganizationId = organizations[0].Id }, context.RequestAborted)
                : null);
            var stores = principal is null ? [] : await database.ListHubStoresAsync(principal, context.RequestAborted);
            var applications = await database.ListHubApplicationsAsync(configuration["AEVO_ENVIRONMENT"] ?? "development", context.RequestAborted);
            var preferences = await database.GetHubPreferencesAsync(session.UserId, context.RequestAborted);
            return Results.Ok(new
            {
                user = User(session),
                principal,
                organizations,
                stores,
                applications,
                preferences,
                access = new AccessResponse(principal is not null, principal is null ? "MEMBERSHIP_REQUIRED" : "ALLOWED", "HUB", principal?.Permissions ?? [])
            });
        });

        app.MapGet("/api/v1/hub/favorites", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { favorites = await database.ListHubFavoritesAsync(auth.Session!.UserId, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/favorites", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var normalized = await NormalizeFavoriteAsync(database, auth.Session!, auth.Principal, body, context);
            if (normalized.Failure is not null) return normalized.Failure;
            return Results.Ok(new { favorite = await database.UpsertHubFavoriteAsync(auth.Session!.UserId, auth.Principal, normalized.Body!.Value, context.RequestAborted) });
        });
        app.MapDelete("/api/v1/hub/favorites/{favoriteId:guid}", async (HttpContext context, Guid favoriteId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            return await database.DeleteHubFavoriteAsync(auth.Session!.UserId, favoriteId, context.RequestAborted)
                ? Results.NoContent()
                : Fail(context, 404, "FAVORITE_NOT_FOUND", "Favorite not found.");
        });

        app.MapGet("/api/v1/hub/organizations", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, organizations = await database.ListHubOrganizationsAsync(auth.Session!.UserId, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/organizations", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var name = Required(body, "name", 160);
            var slug = (JsonString(body, "slug") ?? Slugify(name)).Trim().ToLowerInvariant();
            return Results.Ok(new { organization = await database.CreateHubOrganizationAsync(auth.Session!.UserId, name, slug, context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/organizations/{organizationId:guid}", async (HttpContext context, Guid organizationId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            var organization = await database.GetHubOrganizationAsync(auth.Session!.UserId, organizationId, context.RequestAborted);
            return organization is null ? Fail(context, 404, "ORGANIZATION_NOT_FOUND", "Organization not found.") : Results.Ok(new { organization });
        });
        app.MapPatch("/api/v1/hub/organizations/{organizationId:guid}", async (HttpContext context, Guid organizationId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "organization.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (auth.Principal!.OrganizationId != organizationId) return Fail(context, 403, "TENANT_SCOPE_REQUIRED", "The organization is outside the current session scope.");
            var organization = await database.UpdateHubOrganizationAsync(auth.Session!.UserId, organizationId, await ReadBodyAsync(context), context.RequestAborted);
            return organization is null ? Fail(context, 404, "ORGANIZATION_NOT_FOUND", "Organization not found.") : Results.Ok(new { success = true, organization });
        });

        app.MapGet("/api/v1/hub/stores", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var token = sessions.ReadSessionCookie(context, "HUB");
            if (token is null) return Fail(context, 401, "AUTHENTICATION_REQUIRED", "An app-scoped Hub session is required.");
            if (!database.IsConfigured) return Fail(context, 503, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");
            try
            {
                // This compatibility route is also used by older Hub shells.
                // Resolve the same session, principal, and store index in one
                // Core read rather than repeating three authorization queries.
                var bootstrap = await database.ResolveHubSessionBootstrapAsync(token, context.RequestAborted);
                if (bootstrap is null) return Fail(context, 401, "AUTHENTICATION_REQUIRED", "The Hub session is invalid or expired.");
                var tenantContext = TenantContextValidator.ValidateHeaders(
                    bootstrap.Session.OrganizationId,
                    bootstrap.Session.StoreId,
                    context.Request.Headers["x-tenant-id"].ToString(),
                    context.Request.Headers["x-organization-id"].ToString(),
                    context.Request.Headers["x-store-id"].ToString());
                if (!tenantContext.IsValid) return Fail(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!);
                if (bootstrap.Principal is null) return Fail(context, 403, "MEMBERSHIP_REQUIRED", "An active Hub organization membership is required.");
                return Results.Ok(new { stores = bootstrap.Stores });
            }
            catch (CoreDatabaseException error)
            {
                return Fail(context, 503, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
        });
        app.MapGet("/api/v1/hub/stores/customer-profiles", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { profiles = await database.ListHubCustomerProfilesAsync(auth.Principal!, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/stores", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.create");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { store = await database.CreateHubStoreAsync(auth.Principal!, await ReadBodyAsync(context), context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/stores/{storeId:guid}", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            var store = await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted);
            return store is null ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found.") : Results.Ok(new { store });
        });
        app.MapPatch("/api/v1/hub/stores/{storeId:guid}", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            var store = await database.UpdateHubStoreAsync(auth.Principal!, storeId, await ReadBodyAsync(context), context.RequestAborted);
            return store is null ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found.") : Results.Ok(new { success = true, store });
        });
        app.MapGet("/api/v1/hub/stores/{storeId:guid}/deletion", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            var deletion = await database.GetHubStoreDeletionAsync(auth.Principal!, storeId, context.RequestAborted);
            return Results.Ok(new { deletion });
        });
        app.MapPost("/api/v1/hub/stores/{storeId:guid}/deletion-request", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.delete");
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var reason = (JsonString(body, "reason") ?? "Store deletion requested by an organization administrator.").Trim();
            if (reason.Length is < 1 or > 500) return Fail(context, 400, "INVALID_DELETION_REASON", "A deletion reason between 1 and 500 characters is required.");
            var deletion = await database.RequestHubStoreDeletionAsync(auth.Principal!, storeId, reason, RequestId(context), context.RequestAborted);
            return deletion is null
                ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found or it is already inactive.")
                : Results.Json(new { success = true, deletion }, statusCode: 202);
        });
        app.MapPost("/api/v1/hub/stores/{storeId:guid}/deletion-request/cancel", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.delete");
            if (auth.Failure is not null) return auth.Failure;
            var deletion = await database.CancelHubStoreDeletionAsync(auth.Principal!, storeId, RequestId(context), context.RequestAborted);
            return deletion is null
                ? Fail(context, 404, "DELETION_REQUEST_NOT_FOUND", "There is no active deletion request to cancel.")
                : Results.Ok(new { success = true, deletion });
        });
        app.MapDelete("/api/v1/hub/stores/{storeId:guid}", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.delete");
            if (auth.Failure is not null) return auth.Failure;
            var deletion = await database.RequestHubStoreDeletionAsync(
                auth.Principal!,
                storeId,
                "Store deletion requested through the API delete route.",
                RequestId(context),
                context.RequestAborted);
            return deletion is null
                ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found or it is already inactive.")
                : Results.Json(new { success = true, deletion }, statusCode: 202);
        });
        app.MapPost("/api/v1/hub/stores/{storeId:guid}/duplicate", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.create");
            if (auth.Failure is not null) return auth.Failure;
            var source = await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted);
            if (source is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted))
            {
                return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before duplicating it.");
            }
            var body = await ReadBodyAsync(context);
            using var duplicate = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                name = JsonString(body, "name") ?? $"{source.Name} copy",
                code = JsonString(body, "code") ?? $"{source.Code}-COPY",
                timezone = source.Timezone,
                currency = source.Currency,
                storeMode = source.StoreMode,
                address = source.Address,
                phone = source.Phone,
                taxId = source.TaxId
            }));
            return Results.Ok(new { success = true, store = await database.CreateHubStoreAsync(auth.Principal!, duplicate.RootElement, context.RequestAborted) });
        });

        app.MapGet("/api/v1/hub/stores/{storeId:guid}/applications", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            var applications = await database.ListHubStoreApplicationsAsync(auth.Principal!, storeId, context.RequestAborted);
            return applications is null ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found.") : Results.Ok(new { applications });
        });
        app.MapPatch("/api/v1/hub/stores/{storeId:guid}/applications/{applicationCode}", async (HttpContext context, Guid storeId, string applicationCode, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (!new[] { "PLAY", "POS", "KIOSK", "QUEUE" }.Contains(applicationCode.Trim().ToUpperInvariant(), StringComparer.Ordinal)) return Fail(context, 400, "INVALID_APPLICATION", "The application is not store-scoped.");
            var body = await ReadBodyAsync(context);
            var enabled = JsonBool(body, "enabled") ?? false;
            try
            {
                var application = await database.UpdateHubStoreApplicationAsync(
                    auth.Principal!,
                    storeId,
                    applicationCode,
                    enabled,
                    RequestId(context),
                    IdempotencyKey(context, JsonString(body, "idempotencyKey")),
                    context.RequestAborted);
                return application is null ? Fail(context, 404, "STORE_NOT_FOUND", "Store not found.") : Results.Ok(new { success = true, application });
            }
            catch (HubStoreApplicationIdempotencyConflictException error)
            {
                return Fail(context, 409, "HUB_STORE_APPLICATION_IDEMPOTENCY_CONFLICT", error.Message);
            }
            catch (HubStoreApplicationEntitlementException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
            catch (HubStoreRetentionException error)
            {
                return Fail(context, 409, error.Code, error.Message);
            }
            catch (ArgumentException error)
            {
                return Fail(context, 400, "INVALID_IDEMPOTENCY_KEY", error.Message);
            }
        });
        app.MapGet("/api/v1/hub/stores/{storeId:guid}/applications/{applicationCode}/config", async (HttpContext context, Guid storeId, string applicationCode, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            var normalizedApplicationCode = applicationCode.Trim().ToUpperInvariant();
            if (!IsStoreScopedApplication(normalizedApplicationCode)) return Fail(context, 400, "INVALID_APPLICATION", "The application is not store-scoped.");
            var configuration = await database.GetHubStoreApplicationConfigurationAsync(auth.Principal!, storeId, normalizedApplicationCode, context.RequestAborted);
            return configuration is null
                ? Fail(context, 404, "STORE_APPLICATION_NOT_FOUND", "The store application is not registered.")
                : Results.Ok(new { configuration });
        });
        app.MapPatch("/api/v1/hub/stores/{storeId:guid}/applications/{applicationCode}/config", async (HttpContext context, Guid storeId, string applicationCode, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            var normalizedApplicationCode = applicationCode.Trim().ToUpperInvariant();
            if (!IsStoreScopedApplication(normalizedApplicationCode)) return Fail(context, 400, "INVALID_APPLICATION", "The application is not store-scoped.");
            try
            {
                var configuration = await database.UpdateHubStoreApplicationConfigurationAsync(
                    auth.Principal!,
                    storeId,
                    normalizedApplicationCode,
                    await ReadBodyAsync(context),
                    RequestId(context),
                    context.RequestAborted);
                return configuration is null
                    ? Fail(context, 404, "STORE_APPLICATION_NOT_FOUND", "The store application is not registered.")
                    : Results.Ok(new { success = true, configuration });
            }
            catch (HubApplicationConfigurationValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
            catch (HubStoreRetentionException error)
            {
                return Fail(context, 409, error.Code, error.Message);
            }
        });

        app.MapGet("/api/v1/hub/stores/{storeId:guid}/catalog", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "catalog.read");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            var catalog = await database.ListHubCatalogAsync(auth.Principal!.OrganizationId, storeId, context.RequestAborted);
            return Results.Ok(new
            {
                categories = catalog.Categories,
                products = catalog.Products,
                menus = catalog.Menus,
                modifierGroups = catalog.ModifierGroups,
                productModifierGroups = catalog.ProductModifierGroups
            });
        });
        app.MapPost("/api/v1/hub/stores/{storeId:guid}/catalog/products", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "catalog.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted)) return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before changing its catalog.");
            try
            {
                return Results.Ok(new { product = await database.CreateHubProductAsync(auth.Principal!, storeId, await ReadBodyAsync(context), RequestId(context), context.RequestAborted) });
            }
            catch (HubCatalogValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapPatch("/api/v1/hub/stores/{storeId:guid}/catalog/products/{productId:guid}", async (HttpContext context, Guid storeId, Guid productId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "catalog.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted)) return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before changing its catalog.");
            try
            {
                return Results.Ok(new { product = await database.UpdateHubProductAsync(auth.Principal!, storeId, productId, await ReadBodyAsync(context), RequestId(context), context.RequestAborted) });
            }
            catch (HubCatalogValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapPatch("/api/v1/hub/stores/{storeId:guid}/catalog/products/{productId:guid}/availability", async (HttpContext context, Guid storeId, Guid productId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "catalog.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted)) return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before changing its catalog.");
            try
            {
                return Results.Ok(new { availability = await database.PatchHubAvailabilityAsync(auth.Principal!, storeId, productId, await ReadBodyAsync(context), RequestId(context), context.RequestAborted) });
            }
            catch (HubCatalogValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapGet("/api/v1/hub/stores/{storeId:guid}/customer-profile", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            return Results.Ok(new { profile = await database.GetHubCustomerProfileAsync(auth.Principal!.OrganizationId, storeId, context.RequestAborted) });
        });
        app.MapPut("/api/v1/hub/stores/{storeId:guid}/customer-profile", async (HttpContext context, Guid storeId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 404, "STORE_NOT_FOUND", "Store not found.");
            if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted)) return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before changing its public profile.");
            return Results.Ok(new { profile = await database.UpsertHubCustomerProfileAsync(auth.Principal!.OrganizationId, storeId, await ReadBodyAsync(context), context.RequestAborted) });
        });

        app.MapGet("/api/v1/hub/store-templates", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { templates = await database.ListHubTemplatesAsync(auth.Principal!, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/store-templates", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            try
            {
                return Results.Ok(new { success = true, template = await database.CreateHubTemplateAsync(auth.Principal!, await ReadBodyAsync(context), context.RequestAborted) });
            }
            catch (HubStoreRetentionException error)
            {
                return Fail(context, 409, error.Code, error.Message);
            }
        });
        app.MapDelete("/api/v1/hub/store-templates/{templateId:guid}", async (HttpContext context, Guid templateId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            return await database.DeleteHubTemplateAsync(auth.Principal!, templateId, context.RequestAborted)
                ? Results.Ok(new { success = true, deleted = true })
                : Fail(context, 404, "STORE_TEMPLATE_NOT_FOUND", "Store template not found.");
        });
        app.MapPost("/api/v1/hub/store-templates/{templateId:guid}/instantiate", async (HttpContext context, Guid templateId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.create");
            if (auth.Failure is not null) return auth.Failure;
            var store = await database.InstantiateHubTemplateAsync(auth.Principal!, templateId, await ReadBodyAsync(context), context.RequestAborted);
            return store is null ? Fail(context, 404, "STORE_TEMPLATE_NOT_FOUND", "Store template not found.") : Results.Ok(new { success = true, store });
        });

        app.MapGet("/api/v1/hub/apps", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, apps = await database.ListHubApplicationsAsync(configuration["AEVO_ENVIRONMENT"] ?? "development", context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/applications/{applicationCode}/launch", async (
            HttpContext context,
            string applicationCode,
            HubApplicationLaunchRequest body,
            AppSessionReader sessions,
            CoreDataStore database,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration) =>
        {
            var normalized = applicationCode.Trim().ToUpperInvariant();
            if (!ApplicationCodes.All.Contains(normalized)) return Fail(context, 400, "INVALID_APPLICATION", "Unknown application code.");
            var requiredPermission = body.StoreId is null ? "organization.read" : "store.read";
            var auth = await AuthenticateAsync(context, sessions, database, true, true, requiredPermission);
            if (auth.Failure is not null) return auth.Failure;
            var session = auth.Session!;
            var principal = auth.Principal!;

            async Task<IResult> LaunchFailure(int status, string code, string message, string? audience = null, string? readiness = null)
            {
                await database.RecordApplicationLaunchAsync(
                    session.UserId,
                    normalized,
                    principal.OrganizationId,
                    body.StoreId,
                    "BLOCKED",
                    code,
                    RequestId(context),
                    audience,
                    readiness,
                    context.RequestAborted);
                return Fail(context, status, code, message);
            }

            if (normalized is not ("PLAY" or "POS"))
            {
                return await LaunchFailure(StatusCodes.Status409Conflict, "LAUNCH_NOT_SUPPORTED", "This application does not have a supported Hub callback yet.");
            }

            if (body.StoreId is { } storeId && await database.GetHubStoreAsync(principal, storeId, context.RequestAborted) is null)
            {
                return await LaunchFailure(StatusCodes.Status403Forbidden, "STORE_SCOPE_REQUIRED", "The requested store is outside the current Hub scope.");
            }
            if (body.StoreId is { } pendingStoreId && await database.IsHubStoreDeletionPendingAsync(principal, pendingStoreId, context.RequestAborted))
            {
                return await LaunchFailure(StatusCodes.Status409Conflict, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before opening an application.");
            }

            var target = await database.GetApplicationLaunchTargetAsync(
                normalized,
                configuration["AEVO_ENVIRONMENT"] ?? "development",
                context.RequestAborted);
            if (target is null || target.RegistryStatus != "ACTIVE" || target.LifecycleStatus is "DEPRECATED" or "RETIRED" || !target.StoreScoped)
            {
                return await LaunchFailure(StatusCodes.Status503ServiceUnavailable, "APPLICATION_NOT_READY", "The target application is not ready for Hub launch.", target?.Audience);
            }

            var accessSnapshot = await database.ResolveAccessSnapshotAsync(
                session,
                normalized,
                principal.OrganizationId,
                body.StoreId,
                false,
                context.RequestAborted);
            var authorization = accessSnapshot.Authorization;
            if (authorization is null)
            {
                return await LaunchFailure(StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.", target.Audience);
            }

            var entitlement = accessSnapshot.Entitlement;
            if (entitlement is null)
            {
                return await LaunchFailure(StatusCodes.Status403Forbidden, EntitlementReasonCodes.EntitlementRequired, "The target application entitlement does not allow this launch.", target.Audience);
            }
            if (!entitlement.Allowed)
            {
                return await LaunchFailure(StatusCodes.Status403Forbidden, entitlement.Reason, "The target application entitlement does not allow this launch.", target.Audience);
            }

            var configuredOrigin = configuration[$"AEVO_{normalized}_API_ORIGIN"]
                ?? configuration[$"AEVO_{normalized}_API_URL"]
                ?? target.BaseUrl;
            var readiness = await CheckLaunchReadinessAsync(
                normalized,
                configuredOrigin,
                configuration,
                httpClientFactory,
                context.RequestAborted);
            if (!readiness.Ready)
            {
                return await LaunchFailure(StatusCodes.Status503ServiceUnavailable, "APPLICATION_NOT_READY", "The target application readiness check did not pass.", target.Audience, readiness.Status);
            }

            var returnPath = normalized == "PLAY" ? "/modern" : "/";
            var launchUrl = BuildApplicationLaunchUrl(readiness.Origin, returnPath, body.StoreId);
            await database.RecordApplicationLaunchAsync(
                session.UserId,
                normalized,
                authorization.OrganizationId,
                body.StoreId,
                "READY",
                "ALLOWED",
                RequestId(context),
                target.Audience,
                readiness.Status,
                context.RequestAborted);
            return Results.Ok(new HubApplicationLaunchResponse(
                new HubApplicationLaunch(
                    normalized,
                    target.Audience,
                    target.ContractVersion,
                    "READY",
                    readiness.Status,
                    launchUrl,
                    body.StoreId,
                    readiness.CheckedAt),
                new HubApplicationLaunchAccess(
                    true,
                    "ALLOWED",
                    normalized,
                    authorization.OrganizationId,
                    body.StoreId,
                    authorization.Permissions)));
        });
        app.MapGet("/api/v1/hub/application-connections/{applicationCode}/test", async (HttpContext context, string applicationCode, AppSessionReader sessions, CoreDataStore database, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            var storeIdValue = context.Request.Query["storeId"].ToString().Trim();
            if (!string.IsNullOrWhiteSpace(storeIdValue))
            {
                if (!Guid.TryParse(storeIdValue, out var storeId) || await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null)
                {
                    return Fail(context, 403, "STORE_SCOPE_REQUIRED", "A valid store scope is required.");
                }
                if (await database.IsHubStoreDeletionPendingAsync(auth.Principal!, storeId, context.RequestAborted))
                {
                    return Fail(context, 409, "STORE_RETENTION_ACTIVE", "The store is in its retention window. Cancel the deletion request before testing an application.");
                }
            }
            return await TestApplicationConnectionAsync(context, applicationCode, database, httpClientFactory, configuration);
        });
        app.MapGet("/api/v1/hub/subscriptions", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { subscriptions = await database.GetHubSubscriptionsAsync(auth.Principal!.OrganizationId, null, context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/entitlements/{appId}", async (HttpContext context, string appId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { entitlements = await database.GetHubEntitlementsAsync(auth.Principal!.OrganizationId, appId, null, context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/entitlements/{appId}/check", async (HttpContext context, string appId, AppSessionReader sessions, CoreDataStore database, ILoggerFactory loggerFactory) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            var principal = auth.Principal!;
            var logger = loggerFactory.CreateLogger("Aevo.CoreApi.HubEntitlements");

            var application = EntitlementEvaluator.NormalizeApplicationCode(appId);
            if (application is null)
            {
                return Results.Ok(new
                {
                    appId,
                    application = appId.Trim().ToUpperInvariant(),
                    allowed = false,
                    reason = EntitlementReasonCodes.ApplicationNotRegistered,
                    projectionVersion = EntitlementEvaluator.ProjectionVersion,
                    entitlements = await database.GetHubEntitlementsAsync(principal.OrganizationId, appId, null, context.RequestAborted)
                });
            }

            Guid? storeId = null;
            var storeIdValue = context.Request.Query["storeId"].ToString().Trim();
            if (!string.IsNullOrWhiteSpace(storeIdValue))
            {
                if (!Guid.TryParse(storeIdValue, out var parsedStoreId)) return Fail(context, 400, "INVALID_STORE_SCOPE", "The store scope is invalid.");
                if (await database.GetHubStoreAsync(principal, parsedStoreId, context.RequestAborted) is null)
                {
                    return Results.Ok(new
                    {
                        appId,
                        application,
                        allowed = false,
                        reason = "SCOPE_REQUIRED",
                        projectionVersion = EntitlementEvaluator.ProjectionVersion,
                        organizationId = principal.OrganizationId,
                        storeId = parsedStoreId,
                        entitlements = await database.GetHubEntitlementsAsync(principal.OrganizationId, appId, parsedStoreId, context.RequestAborted)
                    });
                }
                storeId = parsedStoreId;
            }

            try
            {
                var decision = await database.EvaluateApplicationEntitlementAsync(principal.OrganizationId, application, storeId, context.RequestAborted);
                EntitlementDecisionLog(
                    logger,
                    decision.Application,
                    decision.OrganizationId,
                    decision.StoreId,
                    decision.Allowed,
                    decision.Reason,
                    decision.ProjectionVersion,
                    null);
                return Results.Ok(new
                {
                    appId,
                    application = decision.Application,
                    allowed = decision.Allowed,
                    reason = decision.Reason,
                    featureKey = decision.FeatureKey,
                    projectionVersion = decision.ProjectionVersion,
                    organizationId = decision.OrganizationId,
                    storeId = decision.StoreId,
                    planId = decision.PlanId,
                    subscriptionStatus = decision.SubscriptionStatus,
                    entitlementEnabled = decision.EntitlementEnabled,
                    limitValue = decision.LimitValue,
                    quotaLimitValue = decision.QuotaLimitValue,
                    quotaUsage = decision.QuotaUsage,
                    checkedAt = decision.CheckedAt,
                    entitlements = await database.GetHubEntitlementsAsync(principal.OrganizationId, appId, storeId, context.RequestAborted)
                });
            }
            catch (CoreDatabaseException error)
            {
                EntitlementProjectionUnavailableLog(logger, application, principal.OrganizationId, storeId, error);
                return Fail(context, 503, EntitlementReasonCodes.EntitlementProjectionUnavailable, "The entitlement projection is unavailable.");
            }
        });
        app.MapGet("/api/v1/hub/operating-mode", (IConfiguration configuration) =>
        {
            var configurationDecision = EntitlementEvaluator.ValidateConfiguration(
                configuration["AEVO_ENVIRONMENT"],
                configuration["AEVO_ALLOW_UNLIMITED_TESTING"]);
            return Results.Ok(new
            {
                mode = configurationDecision.Environment,
                isUnlimitedTesting = false,
                authorizationBypass = false,
                localSeedModeRequested = configurationDecision.UnlimitedTestingRequested
                    && (configurationDecision.Environment is "development" or "test" or "local"),
                reason = configurationDecision.Reason,
                description = "Development fixtures never bypass Core entitlement and assignment evaluation."
            });
        });

        app.MapGet("/api/v1/hub/members", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, members = await database.ListHubMembersAsync(auth.Principal!, context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/members/applications", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, assignments = await database.ListHubMemberAssignmentsAsync(auth.Principal!, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/members", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            var member = await database.CreateHubMemberAsync(auth.Principal!, await ReadBodyAsync(context), context.RequestAborted);
            return Results.Ok(new { success = true, member });
        });
        app.MapGet("/api/v1/hub/members/{membershipId:guid}/applications", async (HttpContext context, Guid membershipId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, assignments = await database.ListHubMemberAssignmentsAsync(auth.Principal!, membershipId, context.RequestAborted) });
        });
        app.MapPatch("/api/v1/hub/members/{membershipId:guid}/applications/{applicationCode}", async (HttpContext context, Guid membershipId, string applicationCode, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            var assignment = await database.UpdateHubMemberAssignmentAsync(auth.Principal!, membershipId, applicationCode, await ReadBodyAsync(context), context.RequestAborted);
            return assignment is null ? Fail(context, 404, "MEMBER_ASSIGNMENT_NOT_FOUND", "Member application assignment not found.") : Results.Ok(new { success = true, assignment });
        });
        app.MapPatch("/api/v1/hub/members/{membershipId:guid}", async (HttpContext context, Guid membershipId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            var member = await database.UpdateHubMemberAsync(auth.Principal!, membershipId, await ReadBodyAsync(context), context.RequestAborted);
            return member is null ? Fail(context, 404, "MEMBER_NOT_FOUND", "Member not found.") : Results.Ok(new { success = true, member });
        });
        app.MapDelete("/api/v1/hub/members/{membershipId:guid}", async (HttpContext context, Guid membershipId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "member.manage");
            if (auth.Failure is not null) return auth.Failure;
            return await database.DeleteHubMemberAsync(auth.Principal!, membershipId, context.RequestAborted)
                ? Results.Ok(new { success = true })
                : Fail(context, 404, "MEMBER_NOT_FOUND", "Member not found.");
        });

        app.MapGet("/api/v1/hub/devices", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            Guid? storeId = Guid.TryParse(context.Request.Query["storeId"], out var parsed) ? parsed : null;
            return Results.Ok(new { success = true, devices = await database.ListHubDevicesAsync(auth.Principal!, storeId, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/devices", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "devices.manage");
            if (auth.Failure is not null) return auth.Failure;
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            var expires = DateTimeOffset.UtcNow.AddMinutes(15);
            var device = await database.CreateHubDeviceAsync(auth.Principal!, await ReadBodyAsync(context), CoreDataStore.SessionHash(code), expires, context.RequestAborted);
            return Results.Ok(new { success = true, device, pairingCode = code, pairingExpiresAt = expires });
        });
        app.MapPost("/api/v1/hub/devices/{deviceId:guid}/revoke", async (HttpContext context, Guid deviceId, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "devices.manage");
            if (auth.Failure is not null) return auth.Failure;
            return await database.RevokeHubDeviceAsync(auth.Principal!, deviceId, context.RequestAborted)
                ? Results.Ok(new { success = true })
                : Fail(context, 404, "DEVICE_NOT_FOUND", "Device not found.");
        });

        app.MapGet("/api/v1/hub/audit-logs", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "audit.read");
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { logs = await database.ListHubAuditLogsAsync(auth.Principal!.OrganizationId, context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/stats", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, projection = await database.GetHubDashboardProjectionAsync(auth.Principal!.OrganizationId, null, configuration["AEVO_ENVIRONMENT"] ?? "local", context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/overview/organization", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, projection = await database.GetHubDashboardProjectionAsync(auth.Principal!.OrganizationId, null, configuration["AEVO_ENVIRONMENT"] ?? "local", context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/overview/store", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            if (!Guid.TryParse(context.Request.Query["storeId"], out var storeId) || await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "A valid store scope is required.");
            return Results.Ok(new { success = true, projection = await database.GetHubDashboardProjectionAsync(auth.Principal!.OrganizationId, storeId, configuration["AEVO_ENVIRONMENT"] ?? "local", context.RequestAborted) });
        });
        app.MapGet("/api/v1/hub/integrations", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            Guid? storeId = null;
            var rawStoreId = context.Request.Query["storeId"].ToString();
            if (!string.IsNullOrWhiteSpace(rawStoreId))
            {
                if (!Guid.TryParse(rawStoreId, out var parsedStoreId) || await database.GetHubStoreAsync(auth.Principal!, parsedStoreId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "A valid store scope is required.");
                storeId = parsedStoreId;
            }
            return Results.Ok(new { success = true, integrations = await database.ListHubIntegrationsAsync(auth.Principal!.OrganizationId, storeId, context.RequestAborted) });
        });
        app.MapPost("/api/v1/hub/integrations/{providerCode}/connect", async (HttpContext context, string providerCode, HubIntegrationConnectRequest body, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "integration.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (!body.Consent) return Fail(context, 400, "INTEGRATION_CONSENT_REQUIRED", "Explicit consent is required before connecting an integration.");
            if (body.StoreId is { } storeId && await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "The requested store is outside the current Hub scope.");
            try
            {
                var integration = await database.ConnectHubIntegrationAsync(auth.Principal!, providerCode, body.StoreId, body.SecretRef, RequestId(context), context.RequestAborted);
                return Results.Ok(new { success = true, integration });
            }
            catch (HubIntegrationValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapPost("/api/v1/hub/integrations/{providerCode}/rotate", async (HttpContext context, string providerCode, HubIntegrationConnectRequest body, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "integration.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (!body.Consent) return Fail(context, 400, "INTEGRATION_CONSENT_REQUIRED", "Explicit consent is required before rotating an integration secret reference.");
            if (body.StoreId is { } storeId && await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "The requested store is outside the current Hub scope.");
            try
            {
                var integration = await database.RotateHubIntegrationSecretAsync(auth.Principal!, providerCode, body.StoreId, body.SecretRef, RequestId(context), context.RequestAborted);
                return Results.Ok(new { success = true, integration });
            }
            catch (HubIntegrationValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapPost("/api/v1/hub/integrations/{providerCode}/disconnect", async (HttpContext context, string providerCode, HubIntegrationScopeRequest body, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "integration.manage");
            if (auth.Failure is not null) return auth.Failure;
            if (body.StoreId is { } storeId && await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "The requested store is outside the current Hub scope.");
            try
            {
                var integration = await database.DisconnectHubIntegrationAsync(auth.Principal!, providerCode, body.StoreId, RequestId(context), context.RequestAborted);
                return Results.Ok(new { success = true, integration });
            }
            catch (HubIntegrationValidationException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
        });
        app.MapGet("/api/v1/hub/workspace/store", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            if (!Guid.TryParse(context.Request.Query["storeId"], out var storeId) || await database.GetHubStoreAsync(auth.Principal!, storeId, context.RequestAborted) is null) return Fail(context, 403, "STORE_SCOPE_REQUIRED", "A valid store scope is required.");
            return Results.Ok(new { success = true, stats = await database.GetHubStoreStatsAsync(auth.Principal!.OrganizationId, storeId, context.RequestAborted), devices = await database.ListHubDevicesAsync(auth.Principal!, storeId, context.RequestAborted) });
        });

        app.MapGet("/api/v1/me/entitlements", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, entitlements = await database.GetHubResolvedEntitlementsAsync(auth.Principal!.OrganizationId, context.RequestAborted) });
        });
        app.MapGet("/api/v1/me/preferences", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { preferences = await database.GetHubPreferencesAsync(auth.Session!.UserId, context.RequestAborted) });
        });
        app.MapPatch("/api/v1/me/preferences", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var locale = JsonString(await ReadBodyAsync(context), "locale");
            if (locale is not ("en" or "th")) return Fail(context, 400, "INVALID_LOCALE", "The locale must be en or th.");
            return Results.Ok(new { preferences = await database.UpdateHubPreferencesAsync(auth.Session!.UserId, locale, context.RequestAborted) });
        });

        app.MapGet("/api/v1/hub/onboarding/register", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            return Results.Ok(new { success = true, user = User(auth.Session!), next = "/modern" });
        });
        app.MapGet("/api/v1/hub/onboarding/session", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            var session = await database.GetOrCreateHubOnboardingSessionAsync(auth.Session!.UserId, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(session) });
        });
        app.MapPost("/api/v1/hub/onboarding/objectives", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
            var current = await database.GetHubOnboardingSessionAsync(auth.Session!.UserId, sessionId, context.RequestAborted);
            if (current is null) return Fail(context, 404, "ONBOARDING_SESSION_NOT_FOUND", "The onboarding session was not found.");
            var objectives = JsonStringArray(body, "objectives");
            var completed = new HashSet<string>(current.CompletedSteps, StringComparer.Ordinal) { "REGISTER", "OBJECTIVES" };
            var updated = await database.UpdateHubOnboardingSessionAsync(auth.Session.UserId, current.Id, current.OrganizationId, current.StoreId, "ORGANIZATION", objectives, completed, null, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(updated) });
        });
        app.MapPost("/api/v1/hub/onboarding/organization", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var name = Required(body, "name", 200);
            var slug = (JsonString(body, "slug") ?? Slugify(name)).Trim().ToLowerInvariant();
            try
            {
                var organization = await database.CreateHubOrganizationAsync(auth.Session!.UserId, name, slug, body, context.RequestAborted);
                var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
                var session = await database.GetHubOnboardingSessionAsync(auth.Session.UserId, sessionId, context.RequestAborted);
                var onboarding = session is null
                    ? null
                    : await database.UpdateHubOnboardingSessionAsync(
                        auth.Session.UserId,
                        session.Id,
                        organization.Id,
                        null,
                        "STORE",
                        session.Objectives,
                        new HashSet<string>(session.CompletedSteps, StringComparer.Ordinal) { "REGISTER", "OBJECTIVES", "ORGANIZATION" },
                        null,
                        context.RequestAborted);
                return Results.Ok(new { success = true, organizationId = organization.Id, organization, session = onboarding is null ? null : OnboardingSession(onboarding) });
            }
            catch (CoreDatabaseException error)
            {
                return Fail(context, 409, "ORGANIZATION_CREATE_FAILED", error.Message);
            }
        });
        app.MapPost("/api/v1/hub/onboarding/store", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            var principal = auth.Principal!;
            var userId = auth.Session!.UserId;
            var body = await ReadBodyAsync(context);
            var store = await database.CreateHubStoreAsync(principal, body, context.RequestAborted);
            var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
            var current = await database.GetHubOnboardingSessionAsync(userId, sessionId, context.RequestAborted);
            var onboarding = current is null
                ? null
                : await database.UpdateHubOnboardingSessionAsync(userId, current.Id, principal.OrganizationId, store.Id, "APPS", current.Objectives, new HashSet<string>(current.CompletedSteps, StringComparer.Ordinal) { "REGISTER", "OBJECTIVES", "ORGANIZATION", "STORE" }, null, context.RequestAborted);
            return Results.Ok(new { success = true, storeId = store.Id, store, session = onboarding is null ? null : OnboardingSession(onboarding) });
        });
        app.MapPost("/api/v1/hub/onboarding/apps", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            var principal = auth.Principal!;
            var userId = auth.Session!.UserId;
            var body = await ReadBodyAsync(context);
            var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
            var current = await database.GetHubOnboardingSessionAsync(userId, sessionId, context.RequestAborted);
            if (current is null) return Fail(context, 404, "ONBOARDING_SESSION_NOT_FOUND", "The onboarding session was not found.");
            var applicationCodes = JsonStringArray(body, "applications").Concat(JsonStringArray(body, "appIds")).Select(value => value.Trim().ToUpperInvariant()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                foreach (var applicationCode in applicationCodes)
                {
                    await database.UpdateHubMemberAssignmentAsync(principal, principal.MembershipId, applicationCode, JsonDocument.Parse("{\"status\":\"ACTIVE\"}").RootElement, context.RequestAborted);
                    if (current.StoreId is not null && applicationCode is "PLAY" or "POS" or "KIOSK" or "QUEUE")
                    {
                        await database.UpdateHubStoreApplicationAsync(
                            principal,
                            current.StoreId.Value,
                            applicationCode,
                            true,
                            RequestId(context),
                            $"onboarding-store-binding-{current.Id:N}-{applicationCode}",
                            context.RequestAborted);
                    }
                }
            }
            catch (HubStoreApplicationEntitlementException error)
            {
                return Fail(context, error.StatusCode, error.Code, error.Message);
            }
            var objectives = current.Objectives.Concat(applicationCodes).Distinct(StringComparer.Ordinal).ToArray();
            var updated = await database.UpdateHubOnboardingSessionAsync(userId, current.Id, current.OrganizationId, current.StoreId, "RESOURCES", objectives, new HashSet<string>(current.CompletedSteps, StringComparer.Ordinal) { "APPS" }, null, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(updated) });
        });
        app.MapPost("/api/v1/hub/onboarding/booking-setup", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.manage");
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
            var current = await database.GetHubOnboardingSessionAsync(auth.Session!.UserId, sessionId, context.RequestAborted);
            if (current?.OrganizationId is null) return Fail(context, 400, "ORGANIZATION_REQUIRED", "Create an organization before booking setup.");
            var booking = await database.SetupHubOnboardingBookingAsync(current.OrganizationId.Value, current.StoreId, body, context.RequestAborted);
            var updated = await database.UpdateHubOnboardingSessionAsync(auth.Session.UserId, current.Id, current.OrganizationId, current.StoreId, "STAFF", current.Objectives, new HashSet<string>(current.CompletedSteps, StringComparer.Ordinal) { "RESOURCES" }, null, context.RequestAborted);
            return Results.Ok(new { success = true, booking, session = OnboardingSession(updated) });
        });
        app.MapGet("/api/v1/hub/onboarding/progress", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            var session = await database.GetOrCreateHubOnboardingSessionAsync(auth.Session!.UserId, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(session), progress = OnboardingProgress(session) });
        });
        app.MapGet("/api/v1/hub/onboarding/checklist", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false);
            if (auth.Failure is not null) return auth.Failure;
            var session = await database.GetOrCreateHubOnboardingSessionAsync(auth.Session!.UserId, context.RequestAborted);
            var checklist = session.OrganizationId is null ? JsonDocument.Parse("{\"organization\":false,\"stores\":0,\"members\":0,\"enabledApps\":0,\"enabledStoreApps\":0,\"venues\":0}").RootElement.Clone() : await database.GetHubOnboardingChecklistAsync(auth.Session.UserId, session.OrganizationId.Value, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(session), checklist });
        });
        app.MapPost("/api/v1/hub/onboarding/complete", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, false, true);
            if (auth.Failure is not null) return auth.Failure;
            var body = await ReadBodyAsync(context);
            var sessionId = Guid.TryParse(JsonString(body, "sessionId"), out var parsedSessionId) ? parsedSessionId : Guid.Empty;
            var current = await database.GetHubOnboardingSessionAsync(auth.Session!.UserId, sessionId, context.RequestAborted);
            if (current is null) return Fail(context, 404, "ONBOARDING_SESSION_NOT_FOUND", "The onboarding session was not found.");
            var updated = await database.UpdateHubOnboardingSessionAsync(auth.Session.UserId, current.Id, current.OrganizationId, current.StoreId, "COMPLETE", current.Objectives, new HashSet<string>(current.CompletedSteps, StringComparer.Ordinal) { "COMPLETE" }, true, context.RequestAborted);
            if (current.OrganizationId is not null) await database.MarkHubOrganizationOnboardingCompleteAsync(current.OrganizationId.Value, context.RequestAborted);
            return Results.Ok(new { success = true, session = OnboardingSession(updated) });
        });
    }

    private static async Task<IResult> TestApplicationConnectionAsync(
        HttpContext context,
        string applicationCode,
        CoreDataStore database,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        var normalized = applicationCode.Trim().ToUpperInvariant();
        if (!ApplicationCodes.All.Contains(normalized)) return Fail(context, 400, "INVALID_APPLICATION", "Unknown application code.");

        var origin = (configuration[$"AEVO_{normalized}_API_ORIGIN"]
            ?? configuration[$"AEVO_{normalized}_API_URL"]
            ?? configuration[$"AEVO_{normalized}_URL"])?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin))
        {
            return Results.Ok(new
            {
                connection = new
                {
                    application = normalized,
                    configured = false,
                    status = "NOT_CONFIGURED",
                    compatibilityStatus = ApplicationHandshakeStatus.NotConfigured,
                    compatibilityReason = "HANDSHAKE_ORIGIN_NOT_CONFIGURED",
                    capabilities = Array.Empty<string>(),
                    latencyMs = 0,
                    checkedAt = DateTimeOffset.UtcNow
                }
            });
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin)
            || parsedOrigin.Scheme is not ("http" or "https")
            || !string.IsNullOrWhiteSpace(parsedOrigin.UserInfo))
        {
            return Results.Ok(new
            {
                connection = new
                {
                    application = normalized,
                    configured = true,
                    status = "DEGRADED",
                    compatibilityStatus = ApplicationHandshakeStatus.Incompatible,
                    compatibilityReason = "HANDSHAKE_ORIGIN_INVALID",
                    capabilities = Array.Empty<string>(),
                    latencyMs = 0,
                    checkedAt = DateTimeOffset.UtcNow
                }
            });
        }

        var started = DateTimeOffset.UtcNow;
        var checkedAt = DateTimeOffset.UtcNow;
        var compatibilityStatus = ApplicationHandshakeStatus.NotConfigured;
        var compatibilityReason = "HANDSHAKE_NOT_CONFIGURED";
        var protocol = (string?)null;
        var protocolVersion = (string?)null;
        var contractVersion = (string?)null;
        var handshakeEnvironment = (string?)null;
        var capabilities = Array.Empty<string>();
        var httpStatus = (int?)null;
        var healthFallback = false;
        var topLevelStatus = "DEGRADED";
        var lastErrorCode = (string?)null;
        var handshakeSecret = configuration["AEVO_HANDSHAKE_SHARED_SECRET"]?.Trim();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(parsedOrigin, ApplicationHandshake.Path));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.TryAddWithoutValidation("x-request-id", context.Items["aevo.request_id"]?.ToString() ?? Guid.NewGuid().ToString("N"));
            request.Headers.TryAddWithoutValidation("x-aevo-app", "CORE");

            if (!string.IsNullOrWhiteSpace(handshakeSecret))
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var nonce = ApplicationHandshake.CreateNonce();
                request.Headers.TryAddWithoutValidation("x-aevo-handshake-timestamp", timestamp);
                request.Headers.TryAddWithoutValidation("x-aevo-handshake-nonce", nonce);
                request.Headers.TryAddWithoutValidation(
                    "x-aevo-handshake-signature",
                    ApplicationHandshake.CreateSignature(handshakeSecret, timestamp, "GET", ApplicationHandshake.Path, nonce, normalized));
            }
            else
            {
                request.RequestUri = new Uri(parsedOrigin, "/health");
                healthFallback = true;
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            httpStatus = (int)response.StatusCode;
            if (healthFallback)
            {
                topLevelStatus = response.IsSuccessStatusCode ? "DEGRADED" : "OFFLINE";
                compatibilityStatus = ApplicationHandshakeStatus.HealthOnly;
                compatibilityReason = response.IsSuccessStatusCode ? "HANDSHAKE_NOT_CONFIGURED" : "HEALTH_CHECK_FAILED";
                lastErrorCode = response.IsSuccessStatusCode ? null : $"HTTP_{httpStatus}";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                using var healthResponse = await client.GetAsync(new Uri(parsedOrigin, "/health"), timeout.Token);
                httpStatus = (int)healthResponse.StatusCode;
                healthFallback = true;
                topLevelStatus = healthResponse.IsSuccessStatusCode ? "DEGRADED" : "OFFLINE";
                compatibilityStatus = ApplicationHandshakeStatus.HealthOnly;
                compatibilityReason = healthResponse.IsSuccessStatusCode ? "HANDSHAKE_NOT_SUPPORTED" : "HEALTH_CHECK_FAILED";
                lastErrorCode = healthResponse.IsSuccessStatusCode ? null : $"HTTP_{httpStatus}";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                compatibilityStatus = ApplicationHandshakeStatus.Unauthorized;
                compatibilityReason = "HANDSHAKE_REQUEST_UNAUTHORIZED";
                topLevelStatus = "DEGRADED";
                lastErrorCode = $"HTTP_{httpStatus}";
            }
            else if (!response.IsSuccessStatusCode)
            {
                compatibilityStatus = ApplicationHandshakeStatus.Incompatible;
                compatibilityReason = "HANDSHAKE_HTTP_ERROR";
                topLevelStatus = "DEGRADED";
                lastErrorCode = $"HTTP_{httpStatus}";
            }
            else
            {
                var body = await ReadResponseBodyAsync(response, timeout.Token);
                using var document = JsonDocument.Parse(body);
                var decision = ApplicationHandshake.Evaluate(normalized, document.RootElement, checkedAt);
                compatibilityStatus = decision.Status;
                compatibilityReason = decision.Reason;
                protocol = decision.Protocol;
                protocolVersion = decision.ProtocolVersion;
                contractVersion = decision.ContractVersion;
                handshakeEnvironment = decision.Environment;
                capabilities = decision.Capabilities.ToArray();
                topLevelStatus = decision.Compatible ? "CONNECTED" : "DEGRADED";
                lastErrorCode = decision.Compatible ? null : decision.Reason;
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            compatibilityStatus = ApplicationHandshakeStatus.Timeout;
            compatibilityReason = "HANDSHAKE_TIMEOUT";
            topLevelStatus = "TIMEOUT";
            lastErrorCode = "PROBE_TIMEOUT";
        }
        catch (JsonException)
        {
            compatibilityStatus = ApplicationHandshakeStatus.Incompatible;
            compatibilityReason = "HANDSHAKE_INVALID_RESPONSE";
            topLevelStatus = "DEGRADED";
            lastErrorCode = "INVALID_JSON";
        }
        catch (HttpRequestException)
        {
            compatibilityStatus = ApplicationHandshakeStatus.Offline;
            compatibilityReason = "HANDSHAKE_FETCH_FAILED";
            topLevelStatus = "OFFLINE";
            lastErrorCode = "PROBE_FETCH_FAILED";
        }

        var now = DateTimeOffset.UtcNow;
        var metadata = JsonSerializer.SerializeToElement(new
        {
            compatibilityStatus,
            compatibilityReason,
            protocol,
            protocolVersion,
            contractVersion,
            environment = handshakeEnvironment,
            capabilities,
            handshakePath = ApplicationHandshake.Path,
            checkedAt
        });
        try
        {
            await database.RecordConnectionProbeAsync(new ConnectionProbeUpdate(
                normalized,
                (configuration["AEVO_ENVIRONMENT"] ?? "local").Trim().ToLowerInvariant(),
                topLevelStatus switch
                {
                    "CONNECTED" => "connected",
                    "TIMEOUT" or "OFFLINE" => "not_connected",
                    _ => "degraded"
                },
                origin,
                healthFallback ? "/health" : ApplicationHandshake.Path,
                checkedAt,
                Math.Max(0, (int)(now - started).TotalMilliseconds),
                lastErrorCode,
                metadata), context.RequestAborted);
        }
        catch (CoreDatabaseException error)
        {
            ApplicationHandshakeLog(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aevo.CoreApi.HubApi"), normalized, compatibilityStatus, compatibilityReason, error);
        }

        ApplicationHandshakeLog(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aevo.CoreApi.HubApi"), normalized, compatibilityStatus, compatibilityReason, null);
        return Results.Ok(new
        {
            connection = new
            {
                application = normalized,
                configured = true,
                status = topLevelStatus,
                httpStatus,
                latencyMs = Math.Max(0, (int)(now - started).TotalMilliseconds),
                checkedAt,
                compatibilityStatus,
                compatibilityReason,
                protocol,
                protocolVersion,
                contractVersion,
                environment = handshakeEnvironment,
                capabilities,
                handshakePath = healthFallback ? "/health" : ApplicationHandshake.Path
            }
        });
    }

    private static async Task<LaunchReadinessResult> CheckLaunchReadinessAsync(
        string application,
        string? configuredOrigin,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (!Uri.TryCreate(configuredOrigin?.Trim(), UriKind.Absolute, out var parsedOrigin)
            || parsedOrigin.Scheme is not ("http" or "https")
            || !string.IsNullOrWhiteSpace(parsedOrigin.UserInfo))
        {
            return new(false, "NOT_CONFIGURED", "APP_ORIGIN_NOT_CONFIGURED", string.Empty, checkedAt);
        }

        var handshakeSecret = configuration["AEVO_HANDSHAKE_SHARED_SECRET"]?.Trim();
        if (string.IsNullOrWhiteSpace(handshakeSecret))
        {
            return new(false, "NOT_CONFIGURED", "HANDSHAKE_NOT_CONFIGURED", parsedOrigin.ToString().TrimEnd('/'), checkedAt);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(parsedOrigin, ApplicationHandshake.Path));
            request.Headers.Accept.ParseAdd("application/json");
            var timestamp = checkedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nonce = ApplicationHandshake.CreateNonce();
            request.Headers.TryAddWithoutValidation("x-aevo-handshake-timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("x-aevo-handshake-nonce", nonce);
            request.Headers.TryAddWithoutValidation(
                "x-aevo-handshake-signature",
                ApplicationHandshake.CreateSignature(handshakeSecret, timestamp, "GET", ApplicationHandshake.Path, nonce, application));
            using var response = await httpClientFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, response.StatusCode == System.Net.HttpStatusCode.NotFound ? "OFFLINE" : "DEGRADED", $"HANDSHAKE_HTTP_{(int)response.StatusCode}", parsedOrigin.ToString().TrimEnd('/'), checkedAt);
            }
            var body = await ReadResponseBodyAsync(response, timeout.Token);
            using var document = JsonDocument.Parse(body);
            var decision = ApplicationHandshake.Evaluate(application, document.RootElement, checkedAt);
            return new(
                decision.Compatible,
                decision.Compatible ? "CONNECTED" : decision.Status switch
                {
                    ApplicationHandshakeStatus.Timeout => "TIMEOUT",
                    ApplicationHandshakeStatus.Offline => "OFFLINE",
                    _ => "DEGRADED"
                },
                decision.Reason,
                parsedOrigin.ToString().TrimEnd('/'),
                decision.CheckedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "TIMEOUT", "HANDSHAKE_TIMEOUT", parsedOrigin.ToString().TrimEnd('/'), checkedAt);
        }
        catch (HttpRequestException)
        {
            return new(false, "OFFLINE", "HANDSHAKE_FETCH_FAILED", parsedOrigin.ToString().TrimEnd('/'), checkedAt);
        }
        catch (JsonException)
        {
            return new(false, "DEGRADED", "HANDSHAKE_INVALID_RESPONSE", parsedOrigin.ToString().TrimEnd('/'), checkedAt);
        }
    }

    private static string BuildApplicationLaunchUrl(string origin, string returnPath, Guid? storeId)
    {
        var parsedOrigin = new Uri(origin, UriKind.Absolute);
        var basePath = parsedOrigin.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(parsedOrigin)
        {
            Path = $"{basePath}/api/auth/start",
            Query = storeId is null
                ? $"returnTo={Uri.EscapeDataString(returnPath)}"
                : $"returnTo={Uri.EscapeDataString(returnPath)}&storeId={Uri.EscapeDataString(storeId.Value.ToString())}"
        };
        return builder.Uri.ToString();
    }

    private static async Task<byte[]> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > ApplicationHandshake.MaxResponseBytes) throw new JsonException("Handshake response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > ApplicationHandshake.MaxResponseBytes) throw new JsonException("Handshake response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static object OnboardingSession(HubOnboardingSessionRecord session) => new
    {
        id = session.Id,
        userId = session.UserId,
        organizationId = session.OrganizationId,
        storeId = session.StoreId,
        currentStep = session.CurrentStep,
        objectives = session.Objectives,
        completedSteps = session.CompletedSteps,
        isCompleted = session.IsCompleted,
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt
    };

    private static object OnboardingProgress(HubOnboardingSessionRecord session) => new
    {
        organization = session.OrganizationId is not null,
        store = session.StoreId is not null,
        apps = session.CompletedSteps.Contains("APPS", StringComparer.Ordinal),
        booking = session.CompletedSteps.Contains("RESOURCES", StringComparer.Ordinal),
        staff = session.CompletedSteps.Contains("STAFF", StringComparer.Ordinal),
        completed = session.IsCompleted
    };

    private static async Task<(CoreSession? Session, HubPrincipalRecord? Principal, IResult? Failure)> AuthenticateAsync(HttpContext context, AppSessionReader sessions, CoreDataStore database, bool requirePrincipal, bool mutation = false, string? permission = null)
    {
        var token = sessions.ReadSessionCookie(context, "HUB");
        if (token is null) return (null, null, Fail(context, 401, "AUTHENTICATION_REQUIRED", "An app-scoped Hub session is required."));
        if (!database.IsConfigured) return (null, null, Fail(context, 503, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured."));
        try
        {
            var bootstrap = await database.ResolveHubSessionBootstrapAsync(token, context.RequestAborted);
            if (bootstrap is null) return (null, null, Fail(context, 401, "AUTHENTICATION_REQUIRED", "The Hub session is invalid or expired."));
            var session = bootstrap.Session;
            var tenantContext = TenantContextValidator.ValidateHeaders(
                session.OrganizationId,
                session.StoreId,
                context.Request.Headers["x-tenant-id"].ToString(),
                context.Request.Headers["x-organization-id"].ToString(),
                context.Request.Headers["x-store-id"].ToString());
            if (!tenantContext.IsValid) return (null, null, Fail(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!));
            var principal = bootstrap.Principal;
            if (requirePrincipal && principal is null) return (session, null, Fail(context, 403, "MEMBERSHIP_REQUIRED", "An active Hub organization membership is required."));
            if (permission is not null && principal is not null && !principal.Permissions.Contains(permission, StringComparer.Ordinal)) return (session, principal, Fail(context, 403, "PERMISSION_REQUIRED", "The current Hub role cannot perform this operation."));
            if (mutation && !CoreDataStore.VerifyCsrf(session, context.Request.Headers["x-csrf-token"].ToString())) return (session, principal, Fail(context, 403, "CSRF_INVALID", "A valid CSRF token is required for this operation."));
            return (session, principal, null);
        }
        catch (CoreDatabaseException error)
        {
            return (null, null, Fail(context, 503, "CORE_API_DATABASE_UNAVAILABLE", error.Message));
        }
    }

    private static async Task<(JsonElement? Body, IResult? Failure)> NormalizeFavoriteAsync(
        CoreDataStore database,
        CoreSession session,
        HubPrincipalRecord? principal,
        JsonElement body,
        HttpContext context)
    {
        var kind = JsonString(body, "kind")?.Trim().ToUpperInvariant();
        var targetKey = JsonString(body, "targetKey")?.Trim();
        var targetId = body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("targetId", out var targetIdValue)
            && targetIdValue.ValueKind == JsonValueKind.String
            && Guid.TryParse(targetIdValue.GetString(), out var parsedTargetId)
            ? parsedTargetId
            : (Guid?)null;
        var position = JsonInt(body, "position") ?? 0;

        if (kind == "MENU")
        {
            var menu = targetKey switch
            {
                "organize.overview" => ("Hub overview", "/modern", "overview"),
                "organize.stores" => ("Stores & branches", "/modern/stores", "store"),
                "organize.team" => ("Team & access", "/modern/settings#team-heading", "team"),
                "organize.apps" => ("Applications", "/modern/settings#apps-heading", "apps"),
                "organize.billing" => ("Billing & subscription", "/modern/settings#apps-heading", "billing"),
                "workspace.devices" => ("Hardware & terminals", "/modern/stores", "devices"),
                "workspace.apps" => ("Applications", "/modern/settings#apps-heading", "apps"),
                "workspace.billing" => ("Billing & plans", "/modern/settings#apps-heading", "billing"),
                _ => ((string Label, string Href, string Icon)?)null
            };
            return menu is null
                ? (null, Fail(context, 400, "INVALID_FAVORITE_TARGET", "The requested menu cannot be pinned."))
                : (JsonSerializer.SerializeToElement(new { kind = "MENU", targetKey, label = menu.Value.Label, href = menu.Value.Href, iconKey = menu.Value.Icon, position }), null);
        }

        if (targetId is null) return (null, Fail(context, 400, "FAVORITE_TARGET_REQUIRED", "A target identifier is required."));
        if (kind == "ORGANIZATION")
        {
            var organization = await database.GetHubOrganizationAsync(session.UserId, targetId.Value, context.RequestAborted);
            if (organization is null) return (null, Fail(context, 403, "TENANT_SCOPE_REQUIRED", "The organization is outside the current Hub scope."));
            return (JsonSerializer.SerializeToElement(new
            {
                kind,
                targetKey = $"organization:{organization.Id}",
                label = organization.Name,
                href = "/modern/settings",
                iconKey = "organization",
                position
            }), null);
        }

        if (kind == "STORE" && principal is not null)
        {
            var store = await database.GetHubStoreAsync(principal, targetId.Value, context.RequestAborted);
            if (store is null) return (null, Fail(context, 403, "STORE_SCOPE_REQUIRED", "The store is outside the current Hub scope."));
            return (JsonSerializer.SerializeToElement(new
            {
                kind,
                targetKey = $"store:{store.Id}",
                label = $"{store.Name} · {store.Code}",
                href = $"/modern/stores/{Uri.EscapeDataString(store.Id.ToString())}",
                iconKey = "store",
                position
            }), null);
        }

        return (null, Fail(context, 400, "INVALID_FAVORITE_KIND", "The favorite kind is not supported."));
    }

    private static bool IsStoreScopedApplication(string applicationCode) =>
        applicationCode is "PLAY" or "POS" or "KIOSK" or "QUEUE";

    private static object User(CoreSession session) => new { id = session.UserId.ToString(), email = session.Email, displayName = session.DisplayName };
    private static async Task<JsonElement> ReadBodyAsync(HttpContext context) => await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
    private static IResult Fail(HttpContext context, int status, string code, string message) => Results.Json(new { error = new { code, message, requestId = context.Items["aevo.request_id"]?.ToString() } }, statusCode: status);
    private static string RequestId(HttpContext context) => context.Items["aevo.request_id"]?.ToString() ?? Guid.NewGuid().ToString("N");
    private static string IdempotencyKey(HttpContext context, string? bodyValue)
    {
        var headerValue = context.Request.Headers["idempotency-key"].ToString().Trim();
        var value = string.IsNullOrWhiteSpace(headerValue) ? bodyValue?.Trim() : headerValue;
        return string.IsNullOrWhiteSpace(value) ? RequestId(context) : value;
    }
    private static string? JsonString(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] JsonStringArray(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().Take(32).ToArray();
    }
    private static bool? JsonBool(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static int? JsonInt(JsonElement body, string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;
    private static string Required(JsonElement body, string name, int max) { var value = JsonString(body, name)?.Trim(); if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidOperationException($"A valid {name} is required."); return value; }
    private static string Slugify(string value) => string.Join('-', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace("/", "-");
}
