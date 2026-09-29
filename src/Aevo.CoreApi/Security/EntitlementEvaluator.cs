namespace Aevo.CoreApi.Security;

public static class EntitlementReasonCodes
{
    public const string Allowed = "ALLOWED";
    public const string ApplicationNotRegistered = "APP_NOT_REGISTERED";
    public const string ApplicationDisabled = "APPLICATION_DISABLED";
    public const string EntitlementRequired = "ENTITLEMENT_REQUIRED";
    public const string EntitlementInactive = "ENTITLEMENT_INACTIVE";
    public const string EntitlementExpired = "ENTITLEMENT_EXPIRED";
    public const string EntitlementLimitExceeded = "ENTITLEMENT_LIMIT_EXCEEDED";
    public const string StoreApplicationDisabled = "STORE_APPLICATION_DISABLED";
    public const string EntitlementProjectionUnavailable = "ENTITLEMENT_PROJECTION_UNAVAILABLE";
    public const string DevelopmentBypassForbidden = "DEVELOPMENT_BYPASS_FORBIDDEN";
}

public sealed record EntitlementSubscriptionSnapshot(
    string PlanId,
    string Status,
    DateTimeOffset? TrialEnd,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd);

public sealed record EntitlementFeatureSnapshot(
    string FeatureKey,
    bool Enabled,
    int? LimitValue);

public sealed record ApplicationEntitlementSnapshot(
    string ApplicationCode,
    bool ApplicationRegistered,
    bool ApplicationActive,
    EntitlementSubscriptionSnapshot? Subscription,
    EntitlementFeatureSnapshot? Entitlement,
    bool StoreApplicationAccessKnown,
    bool StoreApplicationEnabled,
    EntitlementFeatureSnapshot? StoreApplicationQuota = null,
    int StoreApplicationUsage = 0);

public sealed record ApplicationEntitlementDecision(
    bool Allowed,
    string Application,
    string FeatureKey,
    string Reason,
    string ProjectionVersion,
    Guid OrganizationId,
    Guid? StoreId,
    string? PlanId,
    string? SubscriptionStatus,
    bool? EntitlementEnabled,
    int? LimitValue,
    int? QuotaLimitValue,
    int QuotaUsage,
    DateTimeOffset CheckedAt);

public sealed record EntitlementConfigurationDecision(
    bool Valid,
    string Reason,
    string Environment,
    bool UnlimitedTestingRequested);

/// <summary>
/// Pure, deterministic authorization rules for commercial application access.
/// This class deliberately has no development-mode bypass. Local fixtures must
/// create an explicit subscription/entitlement projection just like any other
/// environment.
/// </summary>
public static class EntitlementEvaluator
{
    public const string ProjectionVersion = "organization-entitlements-v1";

    private static readonly Dictionary<string, string> ApplicationFeatures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PLAY"] = "booking",
            ["POS"] = "pos",
            ["KIOSK"] = "kiosk",
            ["QUEUE"] = "queue"
        };

    public static bool IsCommercialApplication(string? application)
        => NormalizeApplicationCode(application) is not null;

    public static string? NormalizeApplicationCode(string? application)
    {
        var normalized = application?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return ApplicationFeatures.ContainsKey(normalized) ? normalized : null;
    }

    public static string? FeatureKeyForApplication(string? application)
    {
        var normalized = NormalizeApplicationCode(application);
        return normalized is null ? null : ApplicationFeatures[normalized];
    }

    public static EntitlementConfigurationDecision ValidateConfiguration(string? environment, string? unlimitedTestingValue)
    {
        var normalizedEnvironment = string.IsNullOrWhiteSpace(environment)
            ? "development"
            : environment.Trim().ToLowerInvariant();
        var unlimitedTestingRequested = string.Equals(unlimitedTestingValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var productionLike = normalizedEnvironment is "production" or "staging";
        return productionLike && unlimitedTestingRequested
            ? new(false, EntitlementReasonCodes.DevelopmentBypassForbidden, normalizedEnvironment, true)
            : new(true, EntitlementReasonCodes.Allowed, normalizedEnvironment, unlimitedTestingRequested);
    }

    public static ApplicationEntitlementDecision Evaluate(
        Guid organizationId,
        Guid? storeId,
        string application,
        ApplicationEntitlementSnapshot snapshot,
        DateTimeOffset now)
    {
        var normalizedApplication = NormalizeApplicationCode(application);
        var featureKey = FeatureKeyForApplication(application) ?? application.Trim().ToLowerInvariant();
        var checkedAt = now.ToUniversalTime();

        if (normalizedApplication is null || !snapshot.ApplicationRegistered)
        {
            return Decision(false, application, featureKey, EntitlementReasonCodes.ApplicationNotRegistered, organizationId, storeId, snapshot, checkedAt);
        }

        if (!snapshot.ApplicationActive)
        {
            return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.ApplicationDisabled, organizationId, storeId, snapshot, checkedAt);
        }

        if (storeId is not null && (!snapshot.StoreApplicationAccessKnown || !snapshot.StoreApplicationEnabled))
        {
            return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.StoreApplicationDisabled, organizationId, storeId, snapshot, checkedAt);
        }

        if (snapshot.Subscription is null || snapshot.Entitlement is null)
        {
            return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementRequired, organizationId, storeId, snapshot, checkedAt);
        }

        if (!IsSubscriptionCurrent(snapshot.Subscription, checkedAt))
        {
            var reason = IsSubscriptionExpired(snapshot.Subscription, checkedAt)
                ? EntitlementReasonCodes.EntitlementExpired
                : EntitlementReasonCodes.EntitlementInactive;
            return Decision(false, normalizedApplication, featureKey, reason, organizationId, storeId, snapshot, checkedAt);
        }

        if (!string.Equals(snapshot.Entitlement.FeatureKey, featureKey, StringComparison.OrdinalIgnoreCase))
        {
            return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementRequired, organizationId, storeId, snapshot, checkedAt);
        }

        if (!snapshot.Entitlement.Enabled)
        {
            return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementInactive, organizationId, storeId, snapshot, checkedAt);
        }

        if (storeId is not null)
        {
            if (snapshot.StoreApplicationQuota is null)
            {
                return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementRequired, organizationId, storeId, snapshot, checkedAt);
            }

            if (!snapshot.StoreApplicationQuota.Enabled)
            {
                return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementInactive, organizationId, storeId, snapshot, checkedAt);
            }

            if (snapshot.StoreApplicationQuota.LimitValue is { } quotaLimit
                && snapshot.StoreApplicationUsage > quotaLimit)
            {
                return Decision(false, normalizedApplication, featureKey, EntitlementReasonCodes.EntitlementLimitExceeded, organizationId, storeId, snapshot, checkedAt);
            }
        }

        return Decision(true, normalizedApplication, featureKey, EntitlementReasonCodes.Allowed, organizationId, storeId, snapshot, checkedAt);
    }

    private static bool IsSubscriptionCurrent(EntitlementSubscriptionSnapshot subscription, DateTimeOffset now)
    {
        if (subscription.Status is not ("ACTIVE" or "TRIALING" or "GRACE_PERIOD")) return false;
        if (subscription.CurrentPeriodStart is { } startsAt && startsAt > now) return false;

        // `starter` is the canonical permanent Free tier.  It has no billing
        // period end; access is still gated by the explicit subscription row
        // and resolved organization entitlements.
        if (string.Equals(subscription.PlanId, "starter", StringComparison.OrdinalIgnoreCase)
            && subscription.Status == "ACTIVE") return true;

        var end = subscription.Status == "TRIALING"
            ? subscription.TrialEnd ?? subscription.CurrentPeriodEnd
            : subscription.CurrentPeriodEnd;
        return end is { } endsAt && endsAt > now;
    }

    private static bool IsSubscriptionExpired(EntitlementSubscriptionSnapshot subscription, DateTimeOffset now)
    {
        var end = subscription.Status == "TRIALING"
            ? subscription.TrialEnd ?? subscription.CurrentPeriodEnd
            : subscription.CurrentPeriodEnd;
        return end is { } endsAt && endsAt <= now;
    }

    private static ApplicationEntitlementDecision Decision(
        bool allowed,
        string application,
        string featureKey,
        string reason,
        Guid organizationId,
        Guid? storeId,
        ApplicationEntitlementSnapshot snapshot,
        DateTimeOffset checkedAt)
        => new(
            allowed,
            application,
            featureKey,
            reason,
            ProjectionVersion,
            organizationId,
            storeId,
            snapshot.Subscription?.PlanId,
            snapshot.Subscription?.Status,
            snapshot.Entitlement?.Enabled,
            snapshot.Entitlement?.LimitValue,
            snapshot.StoreApplicationQuota?.LimitValue,
            snapshot.StoreApplicationUsage,
            checkedAt);
}
