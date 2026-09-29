using Aevo.CoreApi.Security;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class TenantContextValidatorTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherOrganizationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid StoreId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OtherStoreId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public void AllowsMatchingHeaders()
    {
        var result = TenantContextValidator.ValidateHeaders(OrganizationId, StoreId, OrganizationId.ToString(), OrganizationId.ToString(), StoreId.ToString());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RejectsMismatchedTenantAndOrganizationHeaders()
    {
        var result = TenantContextValidator.ValidateHeaders(OrganizationId, null, OrganizationId.ToString(), OtherOrganizationId.ToString(), null);

        Assert.False(result.IsValid);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("TENANT_CONTEXT_CONFLICT", result.Code);
    }

    [Fact]
    public void RejectsOrganizationHeaderOutsideSessionScope()
    {
        var result = TenantContextValidator.ValidateHeaders(OrganizationId, null, null, OtherOrganizationId.ToString(), null);

        Assert.False(result.IsValid);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("TENANT_CONTEXT_MISMATCH", result.Code);
    }

    [Fact]
    public void RejectsStoreHeaderOutsideSessionScope()
    {
        var result = TenantContextValidator.ValidateHeaders(OrganizationId, StoreId, null, OrganizationId.ToString(), OtherStoreId.ToString());

        Assert.False(result.IsValid);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("TENANT_CONTEXT_MISMATCH", result.Code);
    }

    [Fact]
    public void RejectsMalformedScopeHeader()
    {
        var result = TenantContextValidator.ValidateHeaders(OrganizationId, null, null, "not-a-uuid", null);

        Assert.False(result.IsValid);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("TENANT_CONTEXT_INVALID", result.Code);
    }

    [Fact]
    public void AllowsUnboundSessionToNarrowToAValidScope()
    {
        var result = TenantContextValidator.ValidateScope(null, null, OrganizationId, StoreId);

        Assert.True(result.IsValid);
    }
}
