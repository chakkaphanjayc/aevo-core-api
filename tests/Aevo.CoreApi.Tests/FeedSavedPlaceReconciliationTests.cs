using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedSavedPlaceReconciliationTests
{
    [Fact]
    public void NormalizeDefaultsToBoundedDryRunAndRequiresIdempotency()
    {
        var service = CreateService();

        var error = Assert.Throws<FeedSavedPlaceReconciliationException>(() =>
            service.Normalize(new FeedSavedPlaceReconciliationRequestContract()));

        Assert.Equal("LEGACY_RECONCILIATION_IDEMPOTENCY_INVALID", error.Code);
    }

    [Fact]
    public void NormalizeRejectsUnconfirmedLegacyDelete()
    {
        var service = CreateService();

        var error = Assert.Throws<FeedSavedPlaceReconciliationException>(() =>
            service.Normalize(new FeedSavedPlaceReconciliationRequestContract(
                Mode: "migrate_and_delete",
                Limit: 10,
                IdempotencyKey: "legacy-delete-1",
                Reason: "controlled test cleanup")));

        Assert.Equal("LEGACY_RECONCILIATION_DELETE_CONFIRMATION_REQUIRED", error.Code);
    }

    [Fact]
    public void NormalizeUppercasesModeAndClampsNothingSilently()
    {
        var service = CreateService();

        var normalized = service.Normalize(new FeedSavedPlaceReconciliationRequestContract(
            Mode: "migrate",
            Limit: 10,
            ConfirmLegacyDelete: false,
            IdempotencyKey: "legacy-migrate-1",
            Reason: "controlled test migration"));

        Assert.Equal("MIGRATE", normalized.Mode);
        Assert.Equal(10, normalized.Limit);
        Assert.Equal("controlled test migration", normalized.Reason);
    }

    [Fact]
    public void MutationAndDestructiveModesAreExplicit()
    {
        Assert.False(FeedSavedPlaceReconciliationService.IsMutation("DRY_RUN"));
        Assert.True(FeedSavedPlaceReconciliationService.IsMutation("MIGRATE"));
        Assert.True(FeedSavedPlaceReconciliationService.IsDestructive("MIGRATE_AND_DELETE"));
    }

    private static FeedSavedPlaceReconciliationService CreateService()
    {
        var configuration = new ConfigurationBuilder().Build();
        return new FeedSavedPlaceReconciliationService(
            new CoreDataStore(configuration, NullLogger<CoreDataStore>.Instance));
    }
}
