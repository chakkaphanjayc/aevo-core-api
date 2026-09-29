using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevo.CoreApi.Contracts;
using Microsoft.AspNetCore.Http;

namespace Aevo.CoreApi.Feed;

public sealed record FeedRequestNormalizationResult(
    NormalizedFeedRequest? Request,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsValid => Request is not null;

    public static FeedRequestNormalizationResult Invalid(string message) =>
        new(null, "INVALID_FEED_REQUEST", message);
}

public static class FeedRequestNormalizer
{
    private static readonly HashSet<string> AllowedQueryKeys = new(StringComparer.Ordinal)
    {
        "surface", "tab", "q", "area", "coarseLocation", "cursor", "limit",
        "vibe", "category", "date", "party"
    };

    public static FeedRequestNormalizationResult Normalize(IQueryCollection query)
    {
        if (query.Keys.Any(key => !AllowedQueryKeys.Contains(key)))
        {
            return FeedRequestNormalizationResult.Invalid("Only the versioned Feed request fields are accepted.");
        }

        foreach (var key in query.Keys)
        {
            if (query[key].Count > 1)
            {
                return FeedRequestNormalizationResult.Invalid("A Feed request field may only be supplied once.");
            }
        }

        var surface = Single(query, "surface");
        if (surface is null) return FeedRequestNormalizationResult.Invalid("The Feed surface is required.");
        surface = NormalizeText(surface, 32);
        if (!string.Equals(surface, "explore", StringComparison.Ordinal))
        {
            return FeedRequestNormalizationResult.Invalid("The requested Feed surface is not supported.");
        }

        var tab = Single(query, "tab") is { Length: > 0 } rawTab
            ? NormalizeText(rawTab, 32)
            : FeedApiContract.DefaultTab;
        if (tab is not ("for_you" or "following" or "nearby"))
        {
            return FeedRequestNormalizationResult.Invalid("The requested Feed tab is not supported.");
        }

        var queryText = NormalizeOptional(Single(query, "q"), FeedApiContract.MaxQueryLength);
        if (!queryText.IsValid) return FeedRequestNormalizationResult.Invalid(queryText.Error!);

        var area = NormalizeOptional(Single(query, "area"), FeedApiContract.MaxAreaLength);
        if (!area.IsValid) return FeedRequestNormalizationResult.Invalid(area.Error!);

        var vibe = NormalizeOptional(Single(query, "vibe"), FeedApiContract.MaxVibeLength);
        if (!vibe.IsValid) return FeedRequestNormalizationResult.Invalid(vibe.Error!);
        if (vibe.Value is not null && !DiscoveryContract.Vibes.Contains(vibe.Value))
        {
            return FeedRequestNormalizationResult.Invalid("The requested discovery vibe is not supported.");
        }

        var category = NormalizeOptional(Single(query, "category"), FeedApiContract.MaxCategoryLength);
        if (!category.IsValid) return FeedRequestNormalizationResult.Invalid(category.Error!);
        if (string.Equals(category.Value, "all", StringComparison.Ordinal)) category = new TextResult(true, null);

        var date = NormalizeDate(Single(query, "date"));
        if (!date.IsValid) return FeedRequestNormalizationResult.Invalid(date.Error!);

        var partySize = ParsePartySize(Single(query, "party"));
        if (!partySize.IsValid) return FeedRequestNormalizationResult.Invalid(partySize.Error!);

        var coarse = ParseCoarseLocation(Single(query, "coarseLocation"));
        if (!coarse.IsValid) return FeedRequestNormalizationResult.Invalid(coarse.Error!);
        if (area.Value is not null && coarse.Value?.Area is not null && !string.Equals(area.Value, coarse.Value.Area, StringComparison.Ordinal))
        {
            return FeedRequestNormalizationResult.Invalid("The top-level area and coarse location area must match.");
        }

        var cursor = Single(query, "cursor")?.Trim();
        if (cursor is { Length: > FeedApiContract.MaxCursorLength })
        {
            return FeedRequestNormalizationResult.Invalid("The Feed cursor is too large.");
        }
        if (cursor is { Length: 0 }) cursor = null;

        var limit = FeedApiContract.DefaultPageSize;
        var rawLimit = Single(query, "limit");
        if (rawLimit is not null
            && (!int.TryParse(rawLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > FeedApiContract.MaxPageSize))
        {
            return FeedRequestNormalizationResult.Invalid("The Feed page size is outside the allowed range.");
        }

        return new FeedRequestNormalizationResult(new NormalizedFeedRequest(
            surface,
            tab,
            queryText.Value,
            area.Value,
            coarse.Value,
            cursor,
            limit,
            vibe.Value is null && category.Value is null && date.Value is null && partySize.Value is null
                ? null
                : new FeedDiscoveryIntentContract(vibe.Value, category.Value, date.Value, partySize.Value)));
    }

    private static CoarseLocationResult ParseCoarseLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new CoarseLocationResult(null);

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new CoarseLocationResult(null, "The coarse location must be an object.");
            }

            string? area = null;
            string? geohash = null;
            int? radiusMeters = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "area":
                        if (property.Value.ValueKind != JsonValueKind.String) return new CoarseLocationResult(null, "The coarse location area must be text.");
                        var normalizedArea = NormalizeOptional(property.Value.GetString(), FeedApiContract.MaxAreaLength);
                        if (!normalizedArea.IsValid) return new CoarseLocationResult(null, normalizedArea.Error);
                        area = normalizedArea.Value;
                        break;
                    case "geohash":
                        if (property.Value.ValueKind != JsonValueKind.String) return new CoarseLocationResult(null, "The coarse geohash must be text.");
                        geohash = property.Value.GetString()?.Trim().ToLowerInvariant();
                        if (geohash is null or { Length: 0 } || geohash.Length > 32 || !geohash.All(IsGeohashCharacter))
                        {
                            return new CoarseLocationResult(null, "The coarse geohash is invalid.");
                        }
                        break;
                    case "radiusMeters":
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsedRadius)
                            || parsedRadius is < FeedApiContract.MinCoarseRadiusMeters or > FeedApiContract.MaxCoarseRadiusMeters)
                        {
                            return new CoarseLocationResult(null, "The coarse location radius is outside the allowed range.");
                        }
                        radiusMeters = parsedRadius;
                        break;
                    default:
                        return new CoarseLocationResult(null, "The coarse location contains an unsupported field.");
                }
            }

            return area is null && geohash is null && radiusMeters is null
                ? new CoarseLocationResult(null)
                : new CoarseLocationResult(new FeedCoarseLocationContract(area, geohash, radiusMeters));
        }
        catch (JsonException)
        {
            return new CoarseLocationResult(null, "The coarse location is not valid JSON.");
        }
    }

    private static string? Single(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var value) ? value.ToString().Trim() : null;

    private static TextResult NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return new TextResult(true, null);
        var normalized = NormalizeText(value, maximumLength);
        return normalized.Length > maximumLength
            ? new TextResult(false, null, "A Feed text field is too long.")
            : new TextResult(true, normalized);
    }

    private static TextResult NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new TextResult(true, null);
        var normalized = value.Trim();
        return normalized.Length == FeedApiContract.MaxDiscoveryDateLength
            && DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? new TextResult(true, normalized)
            : new TextResult(false, null, "The discovery date must use the yyyy-MM-dd format.");
    }

    private static PartySizeResult ParsePartySize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new PartySizeResult(true, null);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= FeedApiContract.MinPartySize and <= FeedApiContract.MaxPartySize
            ? new PartySizeResult(true, parsed)
            : new PartySizeResult(false, null, "The discovery party size is outside the allowed range.");
    }

    private static string NormalizeText(string value, int maximumLength)
    {
        var normalized = Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), "\\s+", " ", RegexOptions.CultureInvariant);
        if (normalized.Length > maximumLength) return normalized;
        return normalized.ToLowerInvariant();
    }

    private static bool IsGeohashCharacter(char character) => character switch
    {
        >= '0' and <= '9' => true,
        >= 'b' and <= 'h' => true,
        >= 'j' and <= 'k' => true,
        >= 'm' and <= 'n' => true,
        >= 'p' and <= 'z' => true,
        _ => false
    };

    private sealed record TextResult(bool IsValid, string? Value, string? Error = null);

    private sealed record PartySizeResult(bool IsValid, int? Value, string? Error = null);

    private sealed record CoarseLocationResult(FeedCoarseLocationContract? Value, string? Error = null)
    {
        public bool IsValid => Error is null;
    }
}
