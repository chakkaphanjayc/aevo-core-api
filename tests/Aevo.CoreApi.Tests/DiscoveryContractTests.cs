using Aevo.CoreApi.Contracts;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class DiscoveryContractTests
{
    [Fact]
    public void FirstClassCandidatesAreTraceAndPlaceOnly()
    {
        Assert.True(DiscoveryContract.IsCandidateType("TRACE"));
        Assert.True(DiscoveryContract.IsCandidateType("PLACE"));
        Assert.False(DiscoveryContract.IsCandidateType("POST"));
        Assert.False(DiscoveryContract.IsCandidateType("TRACER"));
    }

    [Fact]
    public void ReasonCodesDoNotAcceptPrivateScoresOrExactLocation()
    {
        Assert.True(DiscoveryContract.IsReasonCode("BECAUSE_VIBE"));
        Assert.True(DiscoveryContract.IsReasonCode("COMMUNITY_CONFIDENCE"));
        Assert.False(DiscoveryContract.IsReasonCode("raw_affinity_score"));
        Assert.False(DiscoveryContract.IsReasonCode("PRIVATE_LOCATION"));
    }

    [Fact]
    public void ImpressionIsVeryWeakAndBookingCompletionIsStrongPositive()
    {
        Assert.Equal("VERY_WEAK", DiscoveryContract.SignalClasses["DISCOVERY_IMPRESSION"]);
        Assert.Equal("STRONG_POSITIVE", DiscoveryContract.SignalClasses["BOOKING_COMPLETED"]);
        Assert.Equal("TRUST_QUALITY", DiscoveryContract.SignalClasses["REPORT_SUBMITTED"]);
        Assert.False(DiscoveryContract.IsSignalName("impression"));
    }

    [Fact]
    public void LegacyFeedEventsMapConservativelyWithoutTurningSharesIntoTaste()
    {
        Assert.Equal("DISCOVERY_IMPRESSION", DiscoveryContract.MapLegacyFeedEventToSignal("impression", "PLACE"));
        Assert.Equal("SAVE_TRACE", DiscoveryContract.MapLegacyFeedEventToSignal("save", "TRACE"));
        Assert.Equal("BOOKING_OPEN", DiscoveryContract.MapLegacyFeedEventToSignal("booking_click", "PLACE"));
        Assert.Null(DiscoveryContract.MapLegacyFeedEventToSignal("trace_complete", "PLACE"));
        Assert.Null(DiscoveryContract.MapLegacyFeedEventToSignal("share", "TRACE"));
    }

    [Fact]
    public void SuccessGuardrailAndOperationalMetricsAreDistinctFamilies()
    {
        Assert.Contains("BOOKING_COMPLETED", DiscoveryContract.OutcomeMetrics);
        Assert.Contains("UNSAFE_CONTENT_EXPOSURE", DiscoveryContract.GuardrailMetrics);
        Assert.Contains("GENERATOR_LATENCY_MS", DiscoveryContract.OperationalMetrics);
        Assert.True(DiscoveryContract.IsMetric("BOOKING_COMPLETED"));
        Assert.True(DiscoveryContract.IsMetric("UNSAFE_CONTENT_EXPOSURE"));
        Assert.False(DiscoveryContract.IsMetric("FOLLOWER_GROWTH"));
    }

    [Fact]
    public void PublicEvidenceAllowlistExcludesPrivateModerationAndRankingFields()
    {
        Assert.Contains("ratingConfidence", DiscoveryContract.PublicEvidenceFields);
        Assert.DoesNotContain("rawScore", DiscoveryContract.PublicEvidenceFields);
        Assert.DoesNotContain("moderationReason", DiscoveryContract.PublicEvidenceFields);
        Assert.DoesNotContain("exactVisitTime", DiscoveryContract.PublicEvidenceFields);
    }
}
