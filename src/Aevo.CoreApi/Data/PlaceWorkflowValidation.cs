using System.Text.Json;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed class PlaceWorkflowRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Shared validation for future Place workflow commands. It deliberately does
/// not authorize an organization, resolve a target, or write a canonical row;
/// those decisions remain server-side service/repository responsibilities.
/// </summary>
public static class PlaceWorkflowValidation
{
    private static readonly HashSet<string> ClaimFields = new(StringComparer.Ordinal)
    {
        "name", "localizedNames", "category", "geometry", "displayPoint", "address", "businessLink", "capability", "verification", "status"
    };

    private static readonly HashSet<string> SubmissionFields = new(StringComparer.Ordinal)
    {
        "name", "localizedNames", "category", "canonicalGeometry", "address"
    };

    private static readonly HashSet<string> LiveStateFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "slots", "queue", "wait", "waitTime", "inventory", "availability", "liveState", "nextAvailableAt", "queueLength"
    };

    public static void ValidateClaim(PlaceClaimRequestContract request)
    {
        if (request.RequestedFields is null || request.Evidence is null)
        {
            throw Invalid("CLAIM_PAYLOAD_INVALID", "Claim fields and evidence are required.");
        }
        if (request.BusinessId is null == (request.BranchId is null))
        {
            throw Invalid("CLAIM_TARGET_REQUIRED", "Exactly one business or branch target is required.");
        }
        ValidateIdempotency(request.IdempotencyKey);
        ValidateFields(request.RequestedFields, ClaimFields);
        ValidateEvidence(request.Evidence);
    }

    public static void ValidateSubmission(PlaceSubmissionRequestContract request)
    {
        if (request.SubmissionType is not ("new_place" or "edit" or "duplicate" or "closure" or "report"))
        {
            throw Invalid("SUBMISSION_TYPE_INVALID", "Submission type is not supported.");
        }
        if (request.SubmissionType is not "new_place" && request.TargetPlaceId is null)
        {
            throw Invalid("SUBMISSION_TARGET_REQUIRED", "This submission type requires a target Place.");
        }
        if (request.ProposedChanges.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("SUBMISSION_FIELDS_INVALID", "proposedChanges must be a JSON object.");
        }
        var keys = request.ProposedChanges.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Any(key => LiveStateFields.Contains(key)))
        {
            throw Invalid("LIVE_STATE_NOT_ALLOWED", "Live slots, queue, wait, inventory, and availability cannot be submitted as Place fields.");
        }
        if (keys.Any(key => !SubmissionFields.Contains(key)))
        {
            throw Invalid("SUBMISSION_FIELDS_INVALID", "proposedChanges contains a field outside the Place submission contract.");
        }
        if (request.ProposedChanges.GetRawText().Length > 32_768)
        {
            throw Invalid("SUBMISSION_TOO_LARGE", "proposedChanges exceeds the allowed size.");
        }
        ValidateIdempotency(request.IdempotencyKey);
        ValidateEvidence(request.Evidence);
    }

    public static void ValidateRelationship(PlaceRelationshipRequestContract request)
    {
        if (request.RelationshipType is not ("business" or "branch" or "store" or "venue"))
        {
            throw Invalid("RELATIONSHIP_TYPE_INVALID", "Relationship type is not supported.");
        }
        if (request.TargetId == Guid.Empty) throw Invalid("RELATIONSHIP_TARGET_REQUIRED", "A relationship target is required.");
        ValidateIdempotency(request.IdempotencyKey);
    }

    public static void ValidateDecision(PlaceWorkflowDecisionRequestContract request)
    {
        if (request.Decision is not ("approve" or "reject" or "needs_info" or "revoke" or "withdraw"))
        {
            throw Invalid("WORKFLOW_DECISION_INVALID", "Workflow decision is not supported.");
        }
        if (request.Reason.Trim().Length is < 3 or > 500)
        {
            throw Invalid("REASON_REQUIRED", "A workflow decision reason between 3 and 500 characters is required.");
        }
        ValidateIdempotency(request.IdempotencyKey);
    }

    private static void ValidateFields(IReadOnlyList<string> fields, HashSet<string> allowed)
    {
        if (fields.Count is < 1 or > 10 || fields.Any(field => !allowed.Contains(field)))
        {
            throw Invalid("REQUESTED_FIELDS_INVALID", "Requested fields are outside the Place workflow contract.");
        }
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw Invalid("REQUESTED_FIELDS_DUPLICATED", "Requested fields must be unique.");
        }
    }

    private static void ValidateEvidence(IReadOnlyList<PlaceWorkflowEvidenceContract> evidence)
    {
        if (evidence.Count > 5) throw Invalid("EVIDENCE_LIMIT_EXCEEDED", "At most five evidence references may be submitted.");
        foreach (var item in evidence)
        {
            if (item.EvidenceType is not ("business_registration" or "domain_control" or "phone_control" or "address_document" or "other"))
            {
                throw Invalid("EVIDENCE_TYPE_INVALID", "Evidence type is not supported.");
            }
            if (string.IsNullOrWhiteSpace(item.EvidenceToken) || item.EvidenceToken.Trim().Length is < 16 or > 1024)
            {
                throw Invalid("EVIDENCE_TOKEN_INVALID", "Evidence must use an opaque storage token.");
            }
        }
    }

    private static void ValidateIdempotency(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length is < 8 or > 200)
        {
            throw Invalid("IDEMPOTENCY_KEY_REQUIRED", "An idempotency key between 8 and 200 characters is required.");
        }
    }

    private static PlaceWorkflowRequestException Invalid(string code, string message) => new(code, message);
}
