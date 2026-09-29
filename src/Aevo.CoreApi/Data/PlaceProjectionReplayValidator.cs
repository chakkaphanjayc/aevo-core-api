using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed record PlaceProjectionReplayFixture(
    string Label,
    PlaceSummaryContract? Summary);

public sealed record PlaceProjectionReplayOptions(
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset ObservedAt,
    TimeSpan Ttl,
    int MaxPayloadBytes = 64 * 1024);

public sealed record PlaceProjectionReplayFailure(
    string Label,
    string Code,
    string Message);

public sealed record PlaceProjectionReplayReport(
    int InputCount,
    int PublishedCount,
    int FailedCount,
    int DuplicatePlaceCount,
    int MaxPayloadBytes,
    string ReplayFingerprint,
    bool IsDeterministic,
    bool IsWithinPayloadBudget,
    IReadOnlyList<PlaceProjectionBuildResult> Projections,
    IReadOnlyList<PlaceProjectionReplayFailure> Failures)
{
    public bool IsValid =>
        InputCount > 0
        && FailedCount == 0
        && IsDeterministic
        && IsWithinPayloadBudget;
}

/// <summary>
/// Replays labelled public Place fixtures without touching the database. It
/// makes the projection gate explicit: canonical IDs must be unique, the
/// same fixture must produce the same payload twice, and the payload must stay
/// inside the static/public byte budget.
/// </summary>
public static class PlaceProjectionReplayValidator
{
    private const int MaxFixtures = 500;

    private static readonly HashSet<string> ForbiddenLiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "availability",
        "currentAvailability",
        "currentPrice",
        "estimatedWaitMinutes",
        "holds",
        "inventory",
        "liveAvailability",
        "liveBookingState",
        "liveState",
        "queue",
        "queueLength",
        "slots",
        "wait",
        "waitTimeMinutes"
    };

    public static PlaceProjectionReplayReport Validate(
        IEnumerable<PlaceProjectionReplayFixture> fixtures,
        PlaceProjectionReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var fixtureList = fixtures.ToArray();
        var projections = new List<PlaceProjectionBuildResult>(fixtureList.Length);
        var failures = new List<PlaceProjectionReplayFailure>();
        var seenPlaceIds = new HashSet<Guid>();
        var duplicatePlaceCount = 0;
        var maxPayloadBytes = 0;

        if (fixtureList.Length is < 1 or > MaxFixtures)
        {
            failures.Add(new PlaceProjectionReplayFailure(
                "batch",
                "batch-size-exceeded",
                $"A projection replay batch must contain between one and {MaxFixtures} fixtures."));
        }

        foreach (var fixture in fixtureList)
        {
            var label = fixture?.Label ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 128)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "invalid-label",
                    "A replay fixture label is required and must be at most 128 characters."));
                continue;
            }

            if (fixture is null)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "fixture-required",
                    "A replay fixture is required."));
                continue;
            }

            if (fixture.Summary is null)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "summary-required",
                    "A replay fixture must contain a Place summary."));
                continue;
            }

            if (!Guid.TryParse(fixture.Summary.Id, out var placeId) || placeId == Guid.Empty)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "invalid-place-id",
                    "The fixture does not contain a non-empty canonical UUID."));
                continue;
            }

            if (!seenPlaceIds.Add(placeId))
            {
                duplicatePlaceCount++;
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "duplicate-place-id",
                    $"Canonical Place {placeId:D} appears more than once in the replay set."));
                continue;
            }

            if (!string.Equals(fixture.Summary.Status?.Trim(), "visible", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fixture.Summary.Status?.Trim(), "limited", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "non-public-status",
                    "Only visible or limited Places can be published to the public projection."));
                continue;
            }

            try
            {
                var first = PlaceProjectionBuilder.Build(
                    fixture.Summary,
                    options.ProjectionVersion,
                    options.SourceRevision,
                    options.ObservedAt,
                    options.Ttl);
                var second = PlaceProjectionBuilder.Build(
                    fixture.Summary,
                    options.ProjectionVersion,
                    options.SourceRevision,
                    options.ObservedAt,
                    options.Ttl);
                var firstJson = first.Payload.GetRawText();
                var secondJson = second.Payload.GetRawText();
                var payloadBytes = Encoding.UTF8.GetByteCount(firstJson);
                maxPayloadBytes = Math.Max(maxPayloadBytes, payloadBytes);

                if (!string.Equals(firstJson, secondJson, StringComparison.Ordinal))
                {
                    failures.Add(new PlaceProjectionReplayFailure(
                        label,
                        "nondeterministic-payload",
                        "Replaying the same fixture produced different JSON payloads."));
                    continue;
                }

                if (!IsStaticPayload(first.Payload, out var forbiddenPath))
                {
                    failures.Add(new PlaceProjectionReplayFailure(
                        label,
                        "live-field-in-static-payload",
                        $"The public projection contains forbidden live-state field {forbiddenPath}."));
                    continue;
                }

                if (payloadBytes > options.MaxPayloadBytes)
                {
                    failures.Add(new PlaceProjectionReplayFailure(
                        label,
                        "payload-budget-exceeded",
                        $"The payload is {payloadBytes} UTF-8 bytes; the budget is {options.MaxPayloadBytes} bytes."));
                    continue;
                }

                projections.Add(first);
            }
            catch (ArgumentException error)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "projection-build-failed",
                    error.Message));
            }
            catch (InvalidOperationException error)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "projection-build-failed",
                    error.Message));
            }
            catch (JsonException error)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "projection-build-failed",
                    error.Message));
            }
            catch (NullReferenceException error)
            {
                failures.Add(new PlaceProjectionReplayFailure(
                    label,
                    "projection-build-failed",
                    error.Message));
            }
        }

        var fingerprint = CreateFingerprint(projections, options);
        return new PlaceProjectionReplayReport(
            fixtureList.Length,
            projections.Count,
            failures.Count,
            duplicatePlaceCount,
            maxPayloadBytes,
            fingerprint,
            !failures.Any(failure => failure.Code == "nondeterministic-payload"),
            !failures.Any(failure => failure.Code == "payload-budget-exceeded"),
            projections,
            failures);
    }

    public static bool IsStaticPayload(JsonElement payload, out string? forbiddenPropertyPath)
    {
        forbiddenPropertyPath = null;
        return !FindForbiddenProperty(payload, "$", ref forbiddenPropertyPath);
    }

    private static bool FindForbiddenProperty(
        JsonElement element,
        string path,
        ref string? forbiddenPropertyPath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (ForbiddenLiveProperties.Contains(property.Name))
                {
                    forbiddenPropertyPath = propertyPath;
                    return true;
                }

                if (FindForbiddenProperty(property.Value, propertyPath, ref forbiddenPropertyPath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (FindForbiddenProperty(item, $"{path}[{index}]", ref forbiddenPropertyPath))
                {
                    return true;
                }
                index++;
            }
        }

        return false;
    }

    private static string CreateFingerprint(
        IReadOnlyList<PlaceProjectionBuildResult> projections,
        PlaceProjectionReplayOptions options)
    {
        var canonicalRows = projections
            .OrderBy(projection => projection.PlaceId)
            .Select(projection => $"{projection.PlaceId:D}\n{projection.Payload.GetRawText()}");
        var input = string.Join(
            "\n",
            new[]
            {
                options.ProjectionVersion.Trim(),
                options.SourceRevision.Trim(),
                options.ObservedAt.ToUniversalTime().ToString("O"),
                options.Ttl.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }.Concat(canonicalRows));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static void ValidateOptions(PlaceProjectionReplayOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectionVersion) || options.ProjectionVersion.Length > 128)
        {
            throw new ArgumentException("Projection version is required and must be bounded.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.SourceRevision) || options.SourceRevision.Length > 256)
        {
            throw new ArgumentException("Source revision is required and must be bounded.", nameof(options));
        }
        if (options.Ttl <= TimeSpan.Zero || options.Ttl > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Projection TTL must be between one tick and thirty days.");
        }
        if (options.MaxPayloadBytes is < 1 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Projection payload budget must be between one byte and one MiB.");
        }
    }
}
