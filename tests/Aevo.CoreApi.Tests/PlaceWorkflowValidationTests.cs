using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class PlaceWorkflowValidationTests
{
    private static readonly string[] NameField = ["name"];

    [Fact]
    public void ClaimsRequireExactlyOneBusinessOrBranchTarget()
    {
        var request = new PlaceClaimRequestContract(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NameField,
            Array.Empty<PlaceWorkflowEvidenceContract>(),
            "claim-key-1");

        var error = Assert.Throws<PlaceWorkflowRequestException>(() => PlaceWorkflowValidation.ValidateClaim(request));
        Assert.Equal("CLAIM_TARGET_REQUIRED", error.Code);
    }

    [Fact]
    public void SubmissionsRejectLiveStateFields()
    {
        var request = new PlaceSubmissionRequestContract(
            Guid.NewGuid(),
            "edit",
            JsonSerializer.SerializeToElement(new { name = "Cafe", availability = true }),
            Array.Empty<PlaceWorkflowEvidenceContract>(),
            "submission-key-1");

        var error = Assert.Throws<PlaceWorkflowRequestException>(() => PlaceWorkflowValidation.ValidateSubmission(request));
        Assert.Equal("LIVE_STATE_NOT_ALLOWED", error.Code);
    }

    [Fact]
    public void ValidSubmissionStaysWithinTheStaticPlaceBoundary()
    {
        var request = new PlaceSubmissionRequestContract(
            Guid.NewGuid(),
            "edit",
            JsonSerializer.SerializeToElement(new { name = "คาเฟ่", address = new { locality = "Bangkok" } }),
            new[] { new PlaceWorkflowEvidenceContract("other", "opaque-evidence-token-1") },
            "submission-key-1");

        PlaceWorkflowValidation.ValidateSubmission(request);
    }

    [Fact]
    public void DecisionsRequireReasonAndIdempotency()
    {
        var request = new PlaceWorkflowDecisionRequestContract("approve", "approved", "short");
        var error = Assert.Throws<PlaceWorkflowRequestException>(() => PlaceWorkflowValidation.ValidateDecision(request));
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", error.Code);
    }
}
