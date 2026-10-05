using System.Text.Json;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class HubMemberAssignmentPolicyTests
{
    [Theory]
    [InlineData("PLAY")]
    [InlineData("POS")]
    [InlineData("KIOSK")]
    [InlineData("QUEUE")]
    public void WorkforceApplicationsCanBeAssigned(string applicationCode)
        => Assert.Equal(applicationCode, HubMemberAssignmentPolicy.NormalizeApplicationCode(applicationCode));

    [Theory]
    [InlineData("HUB")]
    [InlineData("ADMIN")]
    [InlineData("GO")]
    [InlineData("DIGITAL_SIGN")]
    [InlineData("UNKNOWN")]
    public void OrganizationMembersCannotReceiveNonWorkforceApplicationAssignments(string applicationCode)
    {
        var error = Assert.Throws<HubMemberAssignmentException>(
            () => HubMemberAssignmentPolicy.NormalizeApplicationCode(applicationCode));

        Assert.Equal("INVALID_APPLICATION", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("REVOKED")]
    public void ExistingAssignmentStatusesAreAccepted(string status)
        => Assert.Equal(status, HubMemberAssignmentPolicy.NormalizeAssignmentStatus(status));

    [Theory]
    [InlineData("SUSPENDED")]
    [InlineData("DISABLED")]
    [InlineData("unexpected")]
    public void UnsupportedAssignmentStatusesAreRejected(string status)
    {
        var error = Assert.Throws<HubMemberAssignmentException>(
            () => HubMemberAssignmentPolicy.NormalizeAssignmentStatus(status));

        Assert.Equal("INVALID_ASSIGNMENT_STATUS", error.Code);
    }

    [Fact]
    public void PresentNonStringAssignmentStatusIsRejectedInsteadOfDefaultingToActive()
    {
        using var body = JsonDocument.Parse("{\"status\":7}");

        var error = Assert.Throws<HubMemberAssignmentException>(
            () => HubMemberAssignmentPolicy.ReadAssignmentStatus(body.RootElement));

        Assert.Equal("INVALID_ASSIGNMENT_STATUS", error.Code);
    }

    [Fact]
    public void StoreIdsAreStrictAndCanonicalizedForEquivalentRequests()
    {
        var firstStoreId = Guid.NewGuid();
        var secondStoreId = Guid.NewGuid();
        using var firstBody = JsonDocument.Parse($"{{\"storeIds\":[\"{secondStoreId:D}\",\"{firstStoreId:D}\",\"{firstStoreId:D}\"]}}");
        using var secondBody = JsonDocument.Parse($"{{\"storeIds\":[\"{firstStoreId:D}\",\"{secondStoreId:D}\"]}}");

        var first = HubMemberAssignmentPolicy.ReadStoreIds(firstBody.RootElement);
        var second = HubMemberAssignmentPolicy.ReadStoreIds(secondBody.RootElement);

        Assert.Equal(2, first.Length);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("{\"storeIds\":[\"not-a-guid\"]}")]
    [InlineData("{\"storeIds\":\"store-id\"}")]
    [InlineData("{\"storeIds\":[null]}")]
    public void MalformedStoreScopesFailClosed(string request)
    {
        using var body = JsonDocument.Parse(request);

        var error = Assert.Throws<HubMemberAssignmentException>(
            () => HubMemberAssignmentPolicy.ReadStoreIds(body.RootElement));

        Assert.Equal("INVALID_STORE_SCOPE", error.Code);
    }

    [Fact]
    public void StoreScopedActorsCanOnlyAssignAndReadTheirOwnOrganizationStores()
    {
        var ownStore = Guid.NewGuid();
        var anotherStore = Guid.NewGuid();
        var actorStores = new HashSet<Guid> { ownStore };
        var organizationStores = new HashSet<Guid> { ownStore, anotherStore };

        HubMemberAssignmentPolicy.ValidateStoreScope("STORE_MANAGER", actorStores, organizationStores, [ownStore]);
        Assert.True(HubMemberAssignmentPolicy.CanReadAssignment("STORE_MANAGER", actorStores, ownStore));
        Assert.False(HubMemberAssignmentPolicy.CanReadAssignment("STORE_MANAGER", actorStores, anotherStore));
        Assert.False(HubMemberAssignmentPolicy.CanReadAssignment("STORE_MANAGER", actorStores, null));

        var outsideScope = Assert.Throws<HubMemberAssignmentException>(() =>
            HubMemberAssignmentPolicy.ValidateStoreScope("STORE_MANAGER", actorStores, organizationStores, [anotherStore]));
        Assert.Equal("SCOPE_REQUIRED", outsideScope.Code);

        var outsideOrganization = Guid.NewGuid();
        var crossTenant = Assert.Throws<HubMemberAssignmentException>(() =>
            HubMemberAssignmentPolicy.ValidateStoreScope("OWNER", actorStores, organizationStores, [outsideOrganization]));
        Assert.Equal("SCOPE_REQUIRED", crossTenant.Code);
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("ADMIN")]
    public void OrganizationAdministratorsMayManageOrganizationWideAssignmentScope(string role)
    {
        var storeId = Guid.NewGuid();
        var organizationStores = new HashSet<Guid> { storeId };

        HubMemberAssignmentPolicy.ValidateStoreScope(role, new HashSet<Guid>(), organizationStores, []);
        HubMemberAssignmentPolicy.ValidateStoreScope(role, new HashSet<Guid>(), organizationStores, [storeId]);
        Assert.True(HubMemberAssignmentPolicy.CanReadAssignment(role, new HashSet<Guid>(), null));
    }

    [Fact]
    public void StoreScopedActorsCannotCreateOrganizationWideAssignments()
    {
        var ownStore = Guid.NewGuid();
        var error = Assert.Throws<HubMemberAssignmentException>(() =>
            HubMemberAssignmentPolicy.ValidateStoreScope(
                "BRANCH_MANAGER",
                new HashSet<Guid> { ownStore },
                new HashSet<Guid> { ownStore },
                []));

        Assert.Equal("SCOPE_REQUIRED", error.Code);
        Assert.Equal(403, error.StatusCode);
    }
}
