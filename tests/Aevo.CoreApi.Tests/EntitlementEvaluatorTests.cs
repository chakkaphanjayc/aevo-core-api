using Aevo.CoreApi.Security;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class EntitlementEvaluatorTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid StoreId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingEntitlementDeniesAccess()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            null,
            "POS",
            Snapshot(includeEntitlement: false),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.EntitlementRequired, decision.Reason);
        Assert.Equal("pos", decision.FeatureKey);
    }

    [Fact]
    public void ExpiredSubscriptionDeniesAccessEvenWhenEntitlementRowIsEnabled()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            null,
            "POS",
            Snapshot(subscription: new EntitlementSubscriptionSnapshot(
                "business",
                "ACTIVE",
                Now.AddDays(-30),
                Now.AddDays(-31),
                Now.AddSeconds(-1))),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.EntitlementExpired, decision.Reason);
    }

    [Fact]
    public void WrongStoreDeniesAccessBeforeFeatureEvaluation()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            StoreId,
            "POS",
            Snapshot(storeApplicationAccessKnown: false, storeApplicationEnabled: false),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.StoreApplicationDisabled, decision.Reason);
    }

    [Fact]
    public void RevokedEntitlementDeniesAccess()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            null,
            "POS",
            Snapshot(entitlement: new EntitlementFeatureSnapshot("pos", false, null)),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.EntitlementInactive, decision.Reason);
    }

    [Fact]
    public void ValidSubscriptionEntitlementAndStoreBindingAllowAccess()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            StoreId,
            "POS",
            Snapshot(storeApplicationAccessKnown: true, storeApplicationEnabled: true),
            Now);

        Assert.True(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.Allowed, decision.Reason);
        Assert.Equal(EntitlementEvaluator.ProjectionVersion, decision.ProjectionVersion);
    }

    [Fact]
    public void FreeTierDeniesTheSecondActiveStoreApplicationBinding()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            StoreId,
            "POS",
            Snapshot(
                storeApplicationAccessKnown: true,
                storeApplicationEnabled: true,
                storeApplicationUsage: 2),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.EntitlementLimitExceeded, decision.Reason);
        Assert.Equal(1, decision.QuotaLimitValue);
        Assert.Equal(2, decision.QuotaUsage);
    }

    [Fact]
    public void PermanentFreeTierDoesNotRequireAPeriodEnd()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            null,
            "POS",
            Snapshot(subscription: new EntitlementSubscriptionSnapshot(
                "starter",
                "ACTIVE",
                null,
                Now.AddDays(-1),
                null)),
            Now);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void ProductionCannotEnableUnlimitedTestingConfiguration()
    {
        var decision = EntitlementEvaluator.ValidateConfiguration("production", "true");

        Assert.False(decision.Valid);
        Assert.Equal(EntitlementReasonCodes.DevelopmentBypassForbidden, decision.Reason);
    }

    [Fact]
    public void UnknownApplicationDoesNotBecomeEntitledThroughAFeatureName()
    {
        var decision = EntitlementEvaluator.Evaluate(
            OrganizationId,
            null,
            "UNKNOWN",
            Snapshot(),
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EntitlementReasonCodes.ApplicationNotRegistered, decision.Reason);
    }

    private static ApplicationEntitlementSnapshot Snapshot(
        EntitlementSubscriptionSnapshot? subscription = null,
        EntitlementFeatureSnapshot? entitlement = null,
        bool includeEntitlement = true,
        bool storeApplicationAccessKnown = true,
        bool storeApplicationEnabled = true,
        int storeApplicationUsage = 0)
        => new(
            "POS",
            true,
            true,
            subscription ?? new EntitlementSubscriptionSnapshot(
                "business",
                "ACTIVE",
                Now.AddDays(30),
                Now.AddDays(-1),
                Now.AddDays(30)),
            includeEntitlement ? entitlement ?? new EntitlementFeatureSnapshot("pos", true, null) : null,
            storeApplicationAccessKnown,
            storeApplicationEnabled,
            new EntitlementFeatureSnapshot("store_application_bindings", true, 1),
            storeApplicationUsage);
}
