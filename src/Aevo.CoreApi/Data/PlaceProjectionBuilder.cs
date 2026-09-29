using System.Text.Json;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed record PlaceProjectionBuildResult(
    Guid PlaceId,
    string ProjectionVersion,
    string SourceRevision,
    string FreshnessState,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    JsonElement Payload);

/// <summary>
/// Pure, replayable builder for the compact public Place projection. The
/// input is deliberately limited to PlaceSummaryContract, so live booking,
/// queue, wait, and inventory state cannot enter a static projection by
/// accident.
/// </summary>
public static class PlaceProjectionBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    public static PlaceProjectionBuildResult Build(
        PlaceSummaryContract summary,
        string projectionVersion,
        string sourceRevision,
        DateTimeOffset observedAt,
        TimeSpan ttl)
    {
        if (!Guid.TryParse(summary.Id, out var placeId) || placeId == Guid.Empty)
        {
            throw new ArgumentException("The public Place projection requires a canonical UUID.", nameof(summary));
        }
        if (string.IsNullOrWhiteSpace(projectionVersion) || projectionVersion.Length > 128)
        {
            throw new ArgumentException("Projection version is required and must be bounded.", nameof(projectionVersion));
        }
        if (string.IsNullOrWhiteSpace(sourceRevision) || sourceRevision.Length > 256)
        {
            throw new ArgumentException("Source revision is required and must be bounded.", nameof(sourceRevision));
        }
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Projection TTL must be between one tick and thirty days.");
        }

        var payload = JsonSerializer.SerializeToElement(summary, SerializerOptions);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The public Place projection payload must be an object.");
        }

        return new PlaceProjectionBuildResult(
            placeId,
            projectionVersion.Trim(),
            sourceRevision.Trim(),
            "fresh",
            observedAt,
            observedAt.Add(ttl),
            payload);
    }
}
