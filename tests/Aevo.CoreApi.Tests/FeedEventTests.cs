using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedEventTests
{
    private const string CursorSecret = "feed-event-unit-test-secret-012345678901234567890";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AcceptsAnImpressionAtTheDocumentedVisibilityThreshold()
    {
        var fixture = CreateFixture();
        var itemToken = fixture.Signer.CreateItemToken(fixture.Context, "TRACE", "trace-1", 3);
        var candidate = new FeedEventContract(
            "1",
            "event-1",
            "impression",
            fixture.Context.FeedSessionId,
            itemToken,
            FixedNow,
            3,
            "TRACE",
            new FeedEventMetadataContract("web", "explore", "for_you", "NEW_TRACE", 0.5, 1_000));

        var result = FeedEventValidator.Validate(
            candidate,
            fixture.Signer.VerifyItemToken(itemToken, FixedNow),
            fixture.Principal,
            FixedNow);

        Assert.True(result.IsValid);
        Assert.Equal("TRACE", result.Event!.ItemType);
        Assert.Equal("baseline", result.Event.ConfigVersion);
        Assert.Equal("deterministic-v1", result.Event.RankingVersion);
    }

    [Fact]
    public void RejectsAnImpressionBelowTheVisibilityThreshold()
    {
        var fixture = CreateFixture();
        var itemToken = fixture.Signer.CreateItemToken(fixture.Context, "PLACE", "place-1", 0);
        var candidate = new FeedEventContract(
            "1",
            "event-2",
            "impression",
            fixture.Context.FeedSessionId,
            itemToken,
            FixedNow,
            0,
            "PLACE",
            new FeedEventMetadataContract("pwa", "explore", "nearby", "NEARBY_PLACE", 0.49, 1_000));

        var result = FeedEventValidator.Validate(
            candidate,
            fixture.Signer.VerifyItemToken(itemToken, FixedNow),
            fixture.Principal,
            FixedNow);

        Assert.False(result.IsValid);
        Assert.Equal("EVENT_METADATA_INVALID", result.ErrorCode);
    }

    [Fact]
    public void RejectsAnItemTokenBoundToAnotherPrincipal()
    {
        var fixture = CreateFixture();
        var itemToken = fixture.Signer.CreateItemToken(fixture.Context, "TRACE", "trace-2", 0);
        var otherPrincipal = FeedPrincipalFactory.Anonymous(new string('b', 43));
        var candidate = new FeedEventContract(
            "1",
            "event-3",
            "open",
            fixture.Context.FeedSessionId,
            itemToken,
            FixedNow,
            0,
            "TRACE");

        var result = FeedEventValidator.Validate(
            candidate,
            fixture.Signer.VerifyItemToken(itemToken, FixedNow),
            otherPrincipal,
            FixedNow);

        Assert.False(result.IsValid);
        Assert.Equal("ITEM_TOKEN_PRINCIPAL_MISMATCH", result.ErrorCode);
    }

    [Fact]
    public void RejectsAnExpiredItemTokenBeforePersistence()
    {
        var fixture = CreateFixture(FixedNow.AddMinutes(-6));
        var itemToken = fixture.Signer.CreateItemToken(fixture.Context, "PLACE", "place-2", 1);

        var verification = fixture.Signer.VerifyItemToken(itemToken, FixedNow);

        Assert.False(verification.IsValid);
    }

    [Fact]
    public void RejectsAMissingItemTokenWithoutThrowing()
    {
        var fixture = CreateFixture();
        var candidate = new FeedEventContract(
            "1",
            "event-missing-token",
            "open",
            fixture.Context.FeedSessionId,
            null!,
            FixedNow,
            0,
            "TRACE");

        var result = FeedEventValidator.Validate(
            candidate,
            fixture.Signer.VerifyItemToken(string.Empty, FixedNow),
            fixture.Principal,
            FixedNow);

        Assert.False(result.IsValid);
        Assert.Equal("ITEM_TOKEN_INVALID", result.ErrorCode);
    }

    [Fact]
    public void SamplingIsDeterministicForTheSamePrincipalAndEvent()
    {
        var principal = FeedPrincipalFactory.Anonymous(new string('c', 43));

        var first = FeedEventValidator.ShouldSample(principal.CursorBinding, "event-4", 50);
        var second = FeedEventValidator.ShouldSample(principal.CursorBinding, "event-4", 50);

        Assert.Equal(first, second);
        Assert.True(FeedEventValidator.ShouldSample(principal.CursorBinding, "event-4", 100));
        Assert.False(FeedEventValidator.ShouldSample(principal.CursorBinding, "event-4", 0));
    }

    private static Fixture CreateFixture(DateTimeOffset? startedAt = null)
    {
        var principal = FeedPrincipalFactory.Anonymous(new string('a', 43));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AEVO_FEED_SESSION_TTL_SECONDS"] = "300" })
            .Build();
        var signer = new FeedCursorSigner(CursorSecret);
        var runtime = new FeedConfigRuntimeSnapshot(
            "DETERMINISTIC_BASELINE",
            "HEALTHY",
            null,
            null,
            0,
            FeedConfigDefaults.Config,
            FeedConfigDefaults.Json,
            false,
            null,
            FixedNow);
        var request = new NormalizedFeedRequest("explore", "for_you", null, null, null, null, 24);
        var factory = new FeedSessionContextFactory(signer, configuration);
        var context = factory.Create(principal, request, runtime, startedAt ?? FixedNow).Context!;
        return new Fixture(principal, signer, context);
    }

    private sealed record Fixture(
        FeedPrincipal Principal,
        FeedCursorSigner Signer,
        FeedSessionContext Context);
}
