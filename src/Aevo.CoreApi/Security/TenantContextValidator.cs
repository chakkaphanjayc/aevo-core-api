namespace Aevo.CoreApi.Security;

public sealed record TenantContextValidation(
    bool IsValid,
    int StatusCode,
    string? Code,
    string? Message)
{
    public static TenantContextValidation Valid { get; } = new(true, 200, null, null);
}

/// <summary>
/// Validates client-provided tenant hints against the server-resolved session.
/// A request may narrow an unbound session, but it may never widen or replace
/// an organization/store scope already bound to the app session.
/// </summary>
public static class TenantContextValidator
{
    public static TenantContextValidation ValidateHeaders(
        Guid? sessionOrganizationId,
        Guid? sessionStoreId,
        string? tenantHeader,
        string? organizationHeader,
        string? storeHeader)
    {
        var tenant = ParseOptionalGuid(tenantHeader, "x-tenant-id");
        if (!tenant.IsValid) return tenant.Validation;

        var organization = ParseOptionalGuid(organizationHeader, "x-organization-id");
        if (!organization.IsValid) return organization.Validation;

        if (tenant.Value is not null && organization.Value is not null && tenant.Value != organization.Value)
        {
            return Invalid(400, "TENANT_CONTEXT_CONFLICT", "The tenant and organization headers must identify the same organization.");
        }

        var requestedOrganizationId = organization.Value ?? tenant.Value;
        var store = ParseOptionalGuid(storeHeader, "x-store-id");
        if (!store.IsValid) return store.Validation;

        return ValidateScope(sessionOrganizationId, sessionStoreId, requestedOrganizationId, store.Value);
    }

    public static TenantContextValidation ValidateScope(
        Guid? sessionOrganizationId,
        Guid? sessionStoreId,
        Guid? requestedOrganizationId,
        Guid? requestedStoreId)
    {
        if (sessionOrganizationId is not null
            && requestedOrganizationId is not null
            && sessionOrganizationId != requestedOrganizationId)
        {
            return Invalid(403, "TENANT_CONTEXT_MISMATCH", "The requested organization is outside the app session scope.");
        }

        if (sessionStoreId is not null
            && requestedStoreId is not null
            && sessionStoreId != requestedStoreId)
        {
            return Invalid(403, "TENANT_CONTEXT_MISMATCH", "The requested store is outside the app session scope.");
        }

        return TenantContextValidation.Valid;
    }

    private static (bool IsValid, Guid? Value, TenantContextValidation Validation) ParseOptionalGuid(string? raw, string headerName)
    {
        var value = raw?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return (true, null, TenantContextValidation.Valid);
        return Guid.TryParse(value, out var parsed)
            ? (true, parsed, TenantContextValidation.Valid)
            : (false, null, Invalid(400, "TENANT_CONTEXT_INVALID", $"The {headerName} header must be a UUID."));
    }

    private static TenantContextValidation Invalid(int statusCode, string code, string message) =>
        new(false, statusCode, code, message);
}
