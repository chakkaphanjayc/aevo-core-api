using Aevo.CoreApi.Contracts;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class DiscoverySafetyContractTests
{
    [Fact]
    public void PublicUgcRequiresApprovedStatusAndPublicVariant()
    {
        Assert.True(DiscoverySafetyContract.IsPublicEligible("PHOTO", "APPROVED", true));
        Assert.False(DiscoverySafetyContract.IsPublicEligible("PHOTO", "QUARANTINED", true));
        Assert.False(DiscoverySafetyContract.IsPublicEligible("PHOTO", "APPROVED", false));
        Assert.False(DiscoverySafetyContract.IsPublicEligible("PHOTO", "APPROVED", true, "MODERATION_UNAVAILABLE"));
    }

    [Fact]
    public void ExplicitSexualContentAndUnknownKindsAreNeverPublic()
    {
        Assert.Contains("SEXUAL_OR_EXPLICIT", DiscoverySafetyContract.SafetyReasonCodes);
        Assert.False(DiscoverySafetyContract.IsPublicEligible("PHOTO", "REJECTED", false, "SEXUAL_OR_EXPLICIT"));
        Assert.False(DiscoverySafetyContract.IsPublicEligible("UNKNOWN", "APPROVED", true));
    }
}

