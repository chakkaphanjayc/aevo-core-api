using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class FeedModerationCursorTests
{
    [Fact]
    public void EncodesAndDecodesAStatusScopedKeysetCursor()
    {
        var reportId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var createdAt = new DateTimeOffset(2026, 9, 26, 5, 30, 45, TimeSpan.Zero);
        var report = new FeedModerationReport(
            reportId.ToString("D"),
            "TRACE",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "Test reporter",
            "spam",
            "Test details",
            "OPEN",
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            null,
            null,
            null,
            "",
            createdAt);

        var cursor = FeedModerationCursor.Encode("OPEN", report);

        Assert.True(FeedModerationCursor.TryDecode(cursor, "OPEN", out var decodedCreatedAt, out var decodedReportId));
        Assert.Equal(createdAt, decodedCreatedAt);
        Assert.Equal(reportId, decodedReportId);
    }

    [Fact]
    public void RejectsMalformedAndCrossStatusCursors()
    {
        Assert.False(FeedModerationCursor.TryDecode("not-a-cursor", "OPEN", out _, out _));

        var report = new FeedModerationReport(
            "33333333-3333-4333-8333-333333333334",
            "TRACE",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "Test reporter",
            "spam",
            "Test details",
            "OPEN",
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            null,
            null,
            null,
            "",
            DateTimeOffset.UtcNow);

        var cursor = FeedModerationCursor.Encode("OPEN", report);

        Assert.False(FeedModerationCursor.TryDecode(cursor, "REVIEWING", out _, out _));
    }
}
