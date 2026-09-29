using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedFeatureProjectionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 26, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TasteRequiresMinimumEvidence()
    {
        var withoutEnoughEvidence = FeedFeatureProjection.ApplyTasteDecay(
            affinity: 1,
            evidenceCount: FeedFeatureProjection.MinimumTasteEvidence - 1,
            calculatedAt: FixedNow,
            asOf: FixedNow);

        var withMinimumEvidence = FeedFeatureProjection.ApplyTasteDecay(
            affinity: 1,
            evidenceCount: FeedFeatureProjection.MinimumTasteEvidence,
            calculatedAt: FixedNow,
            asOf: FixedNow);

        Assert.Equal(0, withoutEnoughEvidence);
        Assert.True(withMinimumEvidence > 0);
    }

    [Fact]
    public void TasteDecaysWithAgeAndEventuallyFallsBackToNeutral()
    {
        var fresh = FeedFeatureProjection.ApplyTasteDecay(1, 5, FixedNow, FixedNow);
        var old = FeedFeatureProjection.ApplyTasteDecay(
            1,
            5,
            FixedNow.AddDays(-FeedFeatureProjection.TasteHalfLifeDays),
            FixedNow);
        var stale = FeedFeatureProjection.ApplyTasteDecay(
            1,
            5,
            FixedNow.AddDays(-(FeedFeatureProjection.MaximumTasteAgeDays + 1)),
            FixedNow);

        Assert.Equal(1, fresh, precision: 6);
        Assert.InRange(old, 0.49, 0.51);
        Assert.Equal(0, stale);
    }

    [Fact]
    public void MissingOrFutureProjectionCannotCreateTaste()
    {
        Assert.Equal(0, FeedFeatureProjection.ApplyTasteDecay(1, 5, null, FixedNow));
        Assert.Equal(0, FeedFeatureProjection.ApplyTasteDecay(1, 5, FixedNow.AddMinutes(1), FixedNow));
    }

    [Fact]
    public void NegativeTasteRemainsBoundedAndKeepsItsDirection()
    {
        var result = FeedFeatureProjection.ApplyTasteDecay(-100, 5, FixedNow, FixedNow);

        Assert.Equal(-1, result);
    }
}
