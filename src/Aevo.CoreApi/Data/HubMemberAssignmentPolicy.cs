using System.Text.Json;

namespace Aevo.CoreApi.Data;

public sealed class HubMemberAssignmentException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class HubMemberAssignmentPolicy
{
    private static readonly HashSet<string> WorkforceApplications = new(StringComparer.Ordinal)
    {
        "PLAY",
        "POS",
        "KIOSK",
        "QUEUE"
    };

    public static bool IsOrganizationWideRole(string? role)
        => string.Equals(role?.Trim(), "OWNER", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role?.Trim(), "ADMIN", StringComparison.OrdinalIgnoreCase);

    public static bool IsWorkforceApplicationCode(string? applicationCode)
        => WorkforceApplications.Contains(applicationCode?.Trim().ToUpperInvariant() ?? string.Empty);

    public static string NormalizeApplicationCode(string? applicationCode)
    {
        var normalized = applicationCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!IsWorkforceApplicationCode(normalized))
        {
            throw new HubMemberAssignmentException(
                "INVALID_APPLICATION",
                "Only workforce applications can be assigned to organization members.",
                400);
        }

        return normalized;
    }

    public static string NormalizeAssignmentStatus(string? status)
    {
        var normalized = status is null ? "ACTIVE" : status.Trim().ToUpperInvariant();
        if (normalized is not ("ACTIVE" or "REVOKED"))
        {
            throw new HubMemberAssignmentException(
                "INVALID_ASSIGNMENT_STATUS",
                "The assignment status must be ACTIVE or REVOKED.",
                400);
        }

        return normalized;
    }

    public static string ReadAssignmentStatus(JsonElement body, string propertyName = "status")
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(propertyName, out var value)) return "ACTIVE";
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new HubMemberAssignmentException(
                "INVALID_ASSIGNMENT_STATUS",
                "The assignment status must be ACTIVE or REVOKED.",
                400);
        }

        return NormalizeAssignmentStatus(value.GetString());
    }

    public static Guid[] ReadStoreIds(JsonElement body, string propertyName = "storeIds")
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(propertyName, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw InvalidStoreScope();

        var storeIds = new List<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Guid.TryParse(item.GetString(), out var storeId)
                || storeId == Guid.Empty)
            {
                throw InvalidStoreScope();
            }

            if (!storeIds.Contains(storeId)) storeIds.Add(storeId);
        }

        return storeIds.Order().ToArray();
    }

    public static string[] ReadApplicationCodes(JsonElement body)
    {
        var applicationCodes = new List<string>();
        ReadApplicationCodeArray(body, "applicationCodes", applicationCodes);
        ReadApplicationCodeArray(body, "applications", applicationCodes);

        return applicationCodes
            .Select(NormalizeApplicationCode)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static void ValidateStoreScope(
        string actorRole,
        IReadOnlySet<Guid> actorStoreIds,
        IReadOnlySet<Guid> organizationStoreIds,
        Guid[] requestedStoreIds)
    {
        var isOrganizationWide = IsOrganizationWideRole(actorRole);
        if (requestedStoreIds.Length == 0)
        {
            if (isOrganizationWide) return;
            throw ScopeRequired();
        }

        foreach (var storeId in requestedStoreIds)
        {
            if (!organizationStoreIds.Contains(storeId)
                || (!isOrganizationWide && !actorStoreIds.Contains(storeId)))
            {
                throw ScopeRequired();
            }
        }
    }

    public static bool CanReadAssignment(string actorRole, IReadOnlySet<Guid> actorStoreIds, Guid? assignmentStoreId)
        => IsOrganizationWideRole(actorRole)
            || (assignmentStoreId is { } storeId && actorStoreIds.Contains(storeId));

    private static void ReadApplicationCodeArray(JsonElement body, string propertyName, List<string> destination)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(propertyName, out var value)) return;
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new HubMemberAssignmentException(
                "INVALID_APPLICATION",
                "Application assignments must be an array of registered workforce application codes.",
                400);
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new HubMemberAssignmentException(
                    "INVALID_APPLICATION",
                    "Application assignments must be an array of registered workforce application codes.",
                    400);
            }

            destination.Add(item.GetString()!);
        }
    }

    private static HubMemberAssignmentException ScopeRequired()
        => new(
            "SCOPE_REQUIRED",
            "The requested store scope is outside the current member's authorized stores.",
            403);

    private static HubMemberAssignmentException InvalidStoreScope()
        => new(
            "INVALID_STORE_SCOPE",
            "Store scope must contain valid store identifiers.",
            400);
}
