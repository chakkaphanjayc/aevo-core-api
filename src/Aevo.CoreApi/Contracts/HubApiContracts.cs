namespace Aevo.CoreApi.Contracts;

/// <summary>
/// The Core API owns this route inventory. It is intentionally kept beside
/// the .NET boundary so a frontend cannot silently invent a second Hub API.
/// The generated TypeScript package mirrors the same version and templates.
/// </summary>
public static class HubApiContract
{
    public const string Version = "v1";
    public const string Release = "2026-09-26";

    public static readonly IReadOnlyList<string> Routes = new[]
    {
        "/api/v1/hub/contract",
        "/api/v1/hub/me",
        "/api/v1/hub/bootstrap",
        "/api/v1/hub/favorites",
        "/api/v1/hub/favorites/:favoriteId",
        "/api/v1/hub/organizations",
        "/api/v1/hub/organizations/:organizationId",
        "/api/v1/hub/stores",
        "/api/v1/hub/stores/customer-profiles",
        "/api/v1/hub/stores/:storeId",
        "/api/v1/hub/stores/:storeId/applications",
        "/api/v1/hub/stores/:storeId/applications/:applicationCode",
        "/api/v1/hub/stores/:storeId/applications/:applicationCode/config",
        "/api/v1/hub/stores/:storeId/catalog",
        "/api/v1/hub/stores/:storeId/catalog/products",
        "/api/v1/hub/stores/:storeId/catalog/products/:productId",
        "/api/v1/hub/stores/:storeId/catalog/products/:productId/availability",
        "/api/v1/hub/stores/:storeId/customer-profile",
        "/api/v1/hub/stores/:storeId/duplicate",
        "/api/v1/hub/store-templates",
        "/api/v1/hub/store-templates/:templateId",
        "/api/v1/hub/store-templates/:templateId/instantiate",
        "/api/v1/hub/apps",
        "/api/v1/hub/applications/:applicationCode/launch",
        "/api/v1/hub/application-connections/:applicationCode/test",
        "/api/v1/hub/subscriptions",
        "/api/v1/hub/entitlements/:appId",
        "/api/v1/hub/entitlements/:appId/check",
        "/api/v1/hub/operating-mode",
        "/api/v1/hub/members",
        "/api/v1/hub/members/applications",
        "/api/v1/hub/members/:membershipId",
        "/api/v1/hub/members/:membershipId/applications",
        "/api/v1/hub/members/:membershipId/applications/:applicationCode",
        "/api/v1/hub/devices",
        "/api/v1/hub/devices/:deviceId/revoke",
        "/api/v1/hub/audit-logs",
        "/api/v1/hub/stats",
        "/api/v1/hub/overview/organization",
        "/api/v1/hub/overview/store",
        "/api/v1/hub/workspace/store",
        "/api/v1/hub/integrations",
        "/api/v1/hub/integrations/:providerCode/connect",
        "/api/v1/hub/integrations/:providerCode/disconnect",
        "/api/v1/hub/integrations/:providerCode/rotate",
        "/api/v1/me/entitlements",
        "/api/v1/me/preferences",
        "/api/v1/hub/onboarding/register",
        "/api/v1/hub/onboarding/session",
        "/api/v1/hub/onboarding/objectives",
        "/api/v1/hub/onboarding/organization",
        "/api/v1/hub/onboarding/store",
        "/api/v1/hub/onboarding/apps",
        "/api/v1/hub/onboarding/booking-setup",
        "/api/v1/hub/onboarding/progress",
        "/api/v1/hub/onboarding/checklist",
        "/api/v1/hub/onboarding/complete"
    };

    public static readonly IReadOnlyList<string> ImplementedRoutes = Routes;

    public static string ImplementationStatus => ImplementedRoutes.Count == Routes.Count ? "ready" : "partial";

    public static bool Contains(string path) => Routes.Contains(path, StringComparer.Ordinal);
}

public sealed record HubContractMetadata(
    string ContractVersion,
    string Release,
    string Service,
    int RouteCount,
    int ImplementedRouteCount,
    string ImplementationStatus,
    IReadOnlyList<string> Routes);

public sealed record HubOrganizationSummary(
    string Id,
    string Name,
    string Slug,
    string Status,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record HubStoreSummary(
    string Id,
    string OrganizationId,
    string Code,
    string Name,
    string Timezone,
    string Currency,
    string Status,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record HubApplicationSummary(
    string Id,
    string Name,
    string Status,
    string? Description = null,
    IReadOnlyList<string>? Features = null);

public sealed record HubStoreApplicationSummary(
    string ApplicationCode,
    string Status,
    bool ApplicationActive,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? Features = null);

public sealed record HubEntitlementSummary(
    string FeatureKey,
    bool Enabled,
    int? LimitValue = null,
    string? Source = null);

public sealed record HubBootstrapResponse(
    HubOrganizationSummary? Organization,
    IReadOnlyList<HubOrganizationSummary> Organizations,
    IReadOnlyList<HubStoreSummary> Stores,
    IReadOnlyList<HubApplicationSummary> Applications,
    IReadOnlyList<HubEntitlementSummary> Entitlements,
    AccessResponse Access);

public sealed record HubApplicationLaunchRequest(Guid? StoreId = null);

public sealed record HubApplicationLaunch(
    string Application,
    string Audience,
    string ContractVersion,
    string ProvisioningStatus,
    string Readiness,
    string Url,
    Guid? StoreId,
    DateTimeOffset CheckedAt);

public sealed record HubApplicationLaunchAccess(
    bool Allowed,
    string Reason,
    string Application,
    Guid OrganizationId,
    Guid? StoreId,
    IReadOnlyList<string> Permissions);

public sealed record HubApplicationLaunchResponse(
    HubApplicationLaunch Launch,
    HubApplicationLaunchAccess Access);

public sealed record HubIntegrationScopeRequest(Guid? StoreId = null);

public sealed record HubIntegrationConnectRequest(
    string SecretRef,
    bool Consent,
    Guid? StoreId = null);
