using System.Globalization;
using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Microsoft.AspNetCore.Http;

namespace Aevo.CoreApi.Data;

public sealed class PlaceProjectionUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PlacePublicReadService(CoreDataStore database, PlaceCursorSigner? cursorSigner = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PlaceMapOverlayResponseContract> GetMapOverlayAsync(
        PlaceMapOverlayRequestContract request,
        string requestId,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? savedPlaceIds = null)
    {
        EnsureSavedFilterResolved(request.SavedOnly, savedPlaceIds);
        var limit = Math.Clamp(request.Limit ?? PlaceApiContract.DefaultOverlayLimit, 1, PlaceApiContract.MaxOverlayLimit);
        IReadOnlyList<PlaceProjectionRow> rows;
        try
        {
            rows = await database.ReadPlaceProjectionRowsAsync(
                request.Query,
                null,
                request.Bounds,
                request.CategoryIds,
                limit + 1,
                null,
                null,
                cancellationToken,
                savedPlaceIds);
        }
        catch (CoreDatabaseException error)
        {
            throw new PlaceProjectionUnavailableException("Place public projection is not available.", error);
        }

        var truncated = rows.Count > limit;
        var features = rows
            .Take(limit)
            .Select(row => ToOverlayFeature(ReadSummary(row), row.PlaceId))
            .Where(feature => feature is not null)
            .Cast<PlaceMapOverlayFeatureContract>()
            .ToArray();
        var metadata = Metadata(rows, requestId);
        return new PlaceMapOverlayResponseContract(
            metadata.ContractVersion,
            metadata.SchemaVersion,
            metadata.ProjectionVersion,
            metadata.SourceRevision,
            metadata.GeneratedAt,
            metadata.Freshness,
            request.Bounds,
            request.Zoom,
            features,
            truncated,
            new PlaceMapOverlayDensityContract("points", features.Length, 0, limit),
            requestId);
    }

    public async Task<PlaceSearchResponseContract> SearchAsync(
        PlaceSearchRequestContract request,
        string requestId,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? savedPlaceIds = null,
        string? principalBinding = null)
    {
        EnsureSavedFilterResolved(request.SavedOnly, savedPlaceIds, principalBinding, requirePrincipalBinding: true);
        var limit = Math.Clamp(request.Limit ?? PlaceApiContract.DefaultListLimit, 1, PlaceApiContract.MaxListLimit);
        var fingerprint = PlaceCursorSigner.Fingerprint(request, principalBinding);
        var after = VerifyCursor(request.Cursor, "search", fingerprint);
        IReadOnlyList<PlaceProjectionRow> rows;
        try
        {
            rows = await database.ReadPlaceProjectionRowsAsync(
                request.Query,
                request.Area,
                request.Bounds,
                request.CategoryIds,
                limit + 1,
                after,
                null,
                cancellationToken,
                savedPlaceIds);
        }
        catch (CoreDatabaseException error)
        {
            throw new PlaceProjectionUnavailableException("Place public projection is not available.", error);
        }

        var truncated = rows.Count > limit;
        var results = rows.Take(limit)
            .Select((row, index) => ToSearchResult(ReadSummary(row), index + 1, null))
            .ToArray();
        var nextCursor = truncated
            ? CreateSearchCursor(request, rows[limit - 1], principalBinding)
            : null;
        var metadata = Metadata(rows, requestId);
        return new PlaceSearchResponseContract(
            metadata.ContractVersion,
            metadata.SchemaVersion,
            metadata.ProjectionVersion,
            metadata.SourceRevision,
            metadata.GeneratedAt,
            metadata.Freshness,
            results,
            nextCursor,
            null,
            truncated,
            requestId);
    }

    public async Task<PlaceNearbyResponseContract> NearbyAsync(
        PlaceNearbyRequestContract request,
        string requestId,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit ?? PlaceApiContract.DefaultListLimit, 1, PlaceApiContract.MaxListLimit);
        var fingerprint = PlaceCursorSigner.Fingerprint(request);
        var after = VerifyCursor(request.Cursor, "nearby", fingerprint);
        var latitudeDelta = request.RadiusMeters / 111_000d;
        var longitudeDelta = request.RadiusMeters / (111_000d * Math.Max(0.1d, Math.Cos(request.Origin.Latitude * Math.PI / 180d)));
        var bounds = new PlaceBoundsContract(
            Math.Max(-180, request.Origin.Longitude - longitudeDelta),
            Math.Max(-90, request.Origin.Latitude - latitudeDelta),
            Math.Min(180, request.Origin.Longitude + longitudeDelta),
            Math.Min(90, request.Origin.Latitude + latitudeDelta));
        IReadOnlyList<PlaceProjectionRow> rows;
        try
        {
            rows = await database.ReadPlaceProjectionRowsAsync(
                null,
                null,
                bounds,
                request.CategoryIds,
                501,
                after,
                request.Origin,
                cancellationToken);
        }
        catch (CoreDatabaseException error)
        {
            throw new PlaceProjectionUnavailableException("Place public projection is not available.", error);
        }

        var results = rows
            .Select(row => (row, summary: ReadSummary(row)))
            .Select(item => (item.row, item.summary, point: item.summary.DisplayPoint ?? item.summary.LabelPoint))
            .Where(item => item.point is not null)
            .Select(item => (item.row, item.summary, distance: item.row.QueryDistanceMeters ?? DistanceMeters(request.Origin, item.point!)))
            .Where(item => item.distance <= request.RadiusMeters)
            .Where(item => after is null
                || item.distance > after.DistanceMeters.GetValueOrDefault() + 0.01
                || Math.Abs(item.distance - after.DistanceMeters.GetValueOrDefault()) <= 0.01
                    && item.row.PlaceId.CompareTo(after.PlaceId) > 0)
            .OrderBy(item => item.distance)
            .ThenBy(item => item.summary.Id, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToArray();
        var truncated = results.Length > limit;
        var nextCursor = truncated
            ? CreateNearbyCursor(request, results[limit - 1].row, results[limit - 1].distance)
            : null;
        var metadata = Metadata(results.Select(item => item.row).ToArray(), requestId);
        return new PlaceNearbyResponseContract(
            metadata.ContractVersion,
            metadata.SchemaVersion,
            metadata.ProjectionVersion,
            metadata.SourceRevision,
            metadata.GeneratedAt,
            metadata.Freshness,
            request.Origin,
            request.RadiusMeters,
            results.Take(limit).Select((item, index) => ToSearchResult(item.summary, index + 1, item.distance)).ToArray(),
            nextCursor,
            truncated,
            requestId);
    }

    public async Task<PlaceDetailResponseContract> GetDetailAsync(
        Guid placeId,
        string requestId,
        CancellationToken cancellationToken)
    {
        PlaceRedirectLookup redirectLookup;
        PlaceProjectionRow? row;
        try
        {
            redirectLookup = await database.ResolvePlaceRedirectAsync(placeId, cancellationToken);
            if (!redirectLookup.Resolution.IsSafeRead || !redirectLookup.Resolution.ResolvedPlaceId.HasValue)
            {
                throw new PlaceRedirectResolutionException("The requested Place redirect graph is invalid.");
            }
            row = await database.ReadPlaceProjectionDetailAsync(redirectLookup.Resolution.ResolvedPlaceId.Value, cancellationToken);
        }
        catch (CoreDatabaseException error)
        {
            throw new PlaceProjectionUnavailableException("Place public projection is not available.", error);
        }
        if (row is null) throw new KeyNotFoundException("Place was not found.");

        PlaceDetailContract detail;
        try
        {
            detail = ReadDetail(row.Payload);
        }
        catch (JsonException error)
        {
            throw new PlaceProjectionUnavailableException("Place detail projection is invalid.", error);
        }
        if (!Guid.TryParse(detail.Id, out var detailId) || detailId != row.PlaceId)
        {
            throw new PlaceProjectionUnavailableException("Place detail projection identity is invalid.");
        }
        if (redirectLookup.Resolution.Redirected && redirectLookup.Resolution.ResolvedPlaceId.HasValue)
        {
            var reason = redirectLookup.Resolution.RedirectReason ?? "merged";
            detail = detail with
            {
                RedirectFrom = new PlaceRedirectMetadataContract(
                    placeId.ToString("D"),
                    redirectLookup.Resolution.ResolvedPlaceId.Value.ToString("D"),
                    reason,
                    redirectLookup.Resolution.RedirectHops,
                    DateTimeOffset.UtcNow)
            };
        }
        var metadata = Metadata(new[] { row }, requestId);
        return new PlaceDetailResponseContract(
            metadata.ContractVersion,
            metadata.SchemaVersion,
            metadata.ProjectionVersion,
            metadata.SourceRevision,
            metadata.GeneratedAt,
            metadata.Freshness,
            detail,
            requestId);
    }

    private static PlaceSummaryContract ReadSummary(PlaceProjectionRow row)
    {
        try
        {
            var summary = JsonSerializer.Deserialize<PlaceSummaryContract>(row.Payload.GetRawText(), JsonOptions)
                ?? throw new JsonException("Place summary projection was empty.");
            if (!Guid.TryParse(summary.Id, out var summaryId) || summaryId != row.PlaceId)
            {
                throw new JsonException("Place summary projection identity is invalid.");
            }
            return summary;
        }
        catch (JsonException error)
        {
            throw new PlaceProjectionUnavailableException("Place summary projection is invalid.", error);
        }
    }

    private static PlaceDetailContract ReadDetail(JsonElement payload)
    {
        var summary = JsonSerializer.Deserialize<PlaceSummaryContract>(payload.GetRawText(), JsonOptions)
            ?? throw new JsonException("Place detail projection is empty.");

        return new PlaceDetailContract(
            summary.Id,
            summary.Slug,
            summary.Name,
            summary.LocalizedNames,
            summary.Category,
            summary.Status,
            summary.DisplayPoint,
            summary.LabelPoint,
            summary.Area,
            summary.Address,
            summary.ParentPlaceId,
            summary.Verification,
            summary.Business,
            summary.Capabilities,
            summary.Attribution,
            summary.RedirectFrom,
            ReadOptional<PlaceGeometryContract>(payload, "canonicalGeometry"),
            ReadOptional<PlaceGeoPointContract>(payload, "centroid"),
            ReadOptional<PlaceGeometryContract>(payload, "boundingGeometry"),
            ReadArray<string>(payload, "children"),
            ReadArray<PlaceBusinessLinkContract>(payload, "businessLinks"),
            ReadJsonArray(payload, "fieldProvenance"),
            ReadJsonArray(payload, "legacyReferences"));
    }

    private static T? ReadOptional<T>(JsonElement payload, string propertyName)
        where T : class
    {
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException($"Place detail property {propertyName} must be an object or null.");
        }
        return JsonSerializer.Deserialize<T>(property.GetRawText(), JsonOptions)
            ?? throw new JsonException($"Place detail property {propertyName} is invalid.");
    }

    private static T[] ReadArray<T>(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<T>();
        }
        if (property.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException($"Place detail property {propertyName} must be an array or null.");
        }
        return JsonSerializer.Deserialize<T[]>(property.GetRawText(), JsonOptions) ?? Array.Empty<T>();
    }

    private static JsonElement[] ReadJsonArray(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<JsonElement>();
        }
        if (property.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException($"Place detail property {propertyName} must be an array or null.");
        }
        return property.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private PlaceCursorAnchor? VerifyCursor(string? token, string surface, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (cursorSigner is null || !cursorSigner.IsConfigured)
        {
            throw new InvalidPlaceCursorException("Place cursor signing is not configured.");
        }

        var verification = cursorSigner.Verify(token, DateTimeOffset.UtcNow);
        var anchor = verification.Anchor;
        if (!verification.IsValid
            || anchor is null
            || !string.Equals(anchor.Surface, surface, StringComparison.Ordinal)
            || !string.Equals(anchor.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            || (surface == "search" && anchor.DistanceMeters is not null)
            || (surface == "nearby" && anchor.DistanceMeters is null))
        {
            throw new InvalidPlaceCursorException("The Place cursor is invalid, expired, or bound to another query.");
        }
        return anchor;
    }

    private string? CreateSearchCursor(PlaceSearchRequestContract request, PlaceProjectionRow row, string? principalBinding)
    {
        if (cursorSigner is null || !cursorSigner.IsConfigured)
        {
            throw new PlaceProjectionUnavailableException("Place cursor signing is not configured.");
        }
        return cursorSigner.CreateSearchCursor(request, row, principalBinding);
    }

    private string? CreateNearbyCursor(PlaceNearbyRequestContract request, PlaceProjectionRow row, double distanceMeters)
    {
        if (cursorSigner is null || !cursorSigner.IsConfigured)
        {
            throw new PlaceProjectionUnavailableException("Place cursor signing is not configured.");
        }
        return cursorSigner.CreateNearbyCursor(request, row, distanceMeters);
    }

    private static PlaceMapOverlayFeatureContract? ToOverlayFeature(PlaceSummaryContract summary, Guid rowPlaceId)
    {
        var point = summary.DisplayPoint ?? summary.LabelPoint;
        if (point is null) return null;
        return new PlaceMapOverlayFeatureContract(
            "Feature",
            rowPlaceId.ToString(),
            new PlaceGeometryContract("Point", JsonSerializer.SerializeToElement(new[] { point.Longitude, point.Latitude })),
            new PlaceMapOverlayPlacePropertiesContract(
                "place",
                summary.Id,
                summary.Name,
                summary.Category.Id,
                summary.Category.Id,
                summary.Verification.Status,
                summary.Capabilities.Bookable,
                summary.Capabilities.AevoPlayPartner));
    }

    private static PlaceSearchResultContract ToSearchResult(PlaceSummaryContract summary, int rank, double? distanceMeters) =>
        new(
            summary.Id,
            summary.Slug,
            summary.Name,
            summary.LocalizedNames,
            summary.Category,
            summary.Status,
            summary.DisplayPoint,
            summary.LabelPoint,
            summary.Area,
            summary.Address,
            summary.ParentPlaceId,
            summary.Verification,
            summary.Business,
            summary.Capabilities,
            summary.Attribution,
            summary.RedirectFrom,
            rank,
            distanceMeters,
            Array.Empty<string>());

    private static PlaceResponseMetadataContract Metadata(IReadOnlyList<PlaceProjectionRow> rows, string requestId)
    {
        var first = rows.Count == 0 ? null : rows[0];
        var freshnessState = rows.Any(row => row.FreshnessState == "stale") ? "stale" : first?.FreshnessState ?? "unknown";
        var projectionVersion = MetadataValue(rows.Select(row => row.ProjectionVersion));
        var sourceRevision = MetadataValue(rows.Select(row => row.SourceRevision));
        return new PlaceResponseMetadataContract(
            PlaceApiContract.Version,
            PlaceApiContract.SchemaVersion,
            projectionVersion,
            sourceRevision,
            first?.GeneratedAt ?? DateTimeOffset.UtcNow,
            new PlaceFreshnessContract(
                freshnessState,
                first?.ObservedAt,
                first?.ExpiresAt,
                sourceRevision),
            requestId);
    }

    internal static string MetadataValue(IEnumerable<string> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return distinct.Length switch
        {
            0 => "unknown",
            1 => distinct[0],
            _ => "mixed"
        };
    }

    private static void EnsureSavedFilterResolved(
        bool savedOnly,
        IReadOnlySet<Guid>? savedPlaceIds,
        string? principalBinding = null,
        bool requirePrincipalBinding = false)
    {
        if (!savedOnly) return;
        if (savedPlaceIds is null)
        {
            throw new PlaceProjectionUnavailableException(
                "Saved-only Place reads require a Core-resolved saved Place set.");
        }
        if (requirePrincipalBinding && string.IsNullOrWhiteSpace(principalBinding))
        {
            throw new PlaceProjectionUnavailableException(
                "Saved-only Place searches require a Core-resolved session binding.");
        }
    }

    private static double DistanceMeters(PlaceGeoPointContract origin, PlaceGeoPointContract target)
    {
        const double earthRadiusMeters = 6_371_000;
        var latitude1 = origin.Latitude * Math.PI / 180;
        var latitude2 = target.Latitude * Math.PI / 180;
        var latitudeDelta = (target.Latitude - origin.Latitude) * Math.PI / 180;
        var longitudeDelta = (target.Longitude - origin.Longitude) * Math.PI / 180;
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(latitude1) * Math.Cos(latitude2) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return earthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public static class PlaceRequestParser
{
    public static bool TryMap(IQueryCollection query, out PlaceMapOverlayRequestContract request, out string error)
    {
        request = default!;
        if (!TryBounds(query, true, out var bounds, out error)) return false;
        if (!TryNumber(query, "zoom", 0, 24, true, out var zoom, out error)) return false;
        if (!TryLimit(query, PlaceApiContract.DefaultOverlayLimit, PlaceApiContract.MaxOverlayLimit, out var limit, out error)) return false;
        var text = OptionalText(query["q"].ToString(), 200, out error);
        if (error.Length > 0) return false;
        if (!TryBoolean(query, "savedOnly", out var savedOnly, out error)) return false;
        request = new PlaceMapOverlayRequestContract(bounds!, zoom, text, Categories(query["categoryId"].ToString(), out error), limit, savedOnly);
        return error.Length == 0;
    }

    public static bool TrySearch(IQueryCollection query, out PlaceSearchRequestContract request, out string error)
    {
        request = default!;
        if (!TryBounds(query, false, out var bounds, out error)) return false;
        var text = OptionalText(query["q"].ToString(), 200, out error);
        if (error.Length > 0) return false;
        var area = OptionalText(query["area"].ToString(), 120, out error);
        if (error.Length > 0) return false;
        if (!TryBoolean(query, "savedOnly", out var savedOnly, out error)) return false;
        var sort = query["sort"].ToString().Trim();
        if (sort.Length > 0 && sort is not ("relevance" or "distance" or "rating" or "updated"))
        {
            error = "sort must be relevance, distance, rating, or updated.";
            return false;
        }
        if (text is null && area is null && bounds is null)
        {
            error = "search requires q, area, or bounds.";
            return false;
        }
        if (!TryLimit(query, PlaceApiContract.DefaultListLimit, PlaceApiContract.MaxListLimit, out var limit, out error)) return false;
        request = new PlaceSearchRequestContract(text, bounds, area, Categories(query["categoryId"].ToString(), out error), string.IsNullOrWhiteSpace(sort) ? null : sort, query["cursor"].ToString().Trim() is { Length: > 0 } cursor ? cursor : null, limit, savedOnly);
        return error.Length == 0;
    }

    public static bool TryNearby(IQueryCollection query, out PlaceNearbyRequestContract request, out string error)
    {
        request = default!;
        if (!TryNumber(query, "longitude", -180, 180, true, out var longitude, out error)) return false;
        if (!TryNumber(query, "latitude", -90, 90, true, out var latitude, out error)) return false;
        if (!int.TryParse(query["radiusMeters"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var radius) || radius is < 1 or > 50_000)
        {
            error = "radiusMeters must be between 1 and 50000.";
            return false;
        }
        if (!TryLimit(query, PlaceApiContract.DefaultListLimit, PlaceApiContract.MaxListLimit, out var limit, out error)) return false;
        request = new PlaceNearbyRequestContract(
            new PlaceGeoPointContract(longitude, latitude),
            radius,
            Categories(query["categoryId"].ToString(), out error),
            query["cursor"].ToString().Trim() is { Length: > 0 } cursor ? cursor : null,
            limit);
        return error.Length == 0;
    }

    public static bool TryPlaceId(string raw, out Guid placeId, out string error)
    {
        if (!Guid.TryParse(raw, out placeId))
        {
            error = "placeId must be a UUID.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryBounds(IQueryCollection query, bool required, out PlaceBoundsContract? bounds, out string error)
    {
        bounds = null;
        var names = new[] { "west", "south", "east", "north" };
        var present = names.Count(name => !string.IsNullOrWhiteSpace(query[name].ToString()));
        if (present == 0 && !required)
        {
            error = string.Empty;
            return true;
        }
        if (present != names.Length)
        {
            error = "west, south, east, and north must be supplied together.";
            return false;
        }
        if (!TryNumber(query, "west", -180, 180, true, out var west, out error)
            || !TryNumber(query, "south", -90, 90, true, out var south, out error)
            || !TryNumber(query, "east", -180, 180, true, out var east, out error)
            || !TryNumber(query, "north", -90, 90, true, out var north, out error)) return false;
        if (west >= east || south >= north)
        {
            error = "bounds must be a non-empty rectangle and cannot cross the antimeridian in V1.";
            return false;
        }
        if (east - west > PlaceApiContract.MaxViewportLongitudeSpan
            || north - south > PlaceApiContract.MaxViewportLatitudeSpan)
        {
            error = $"bounds cannot exceed {PlaceApiContract.MaxViewportLongitudeSpan.ToString(CultureInfo.InvariantCulture)} longitude degrees or {PlaceApiContract.MaxViewportLatitudeSpan.ToString(CultureInfo.InvariantCulture)} latitude degrees.";
            return false;
        }
        bounds = new PlaceBoundsContract(west, south, east, north);
        error = string.Empty;
        return true;
    }

    private static bool TryNumber(IQueryCollection query, string name, double min, double max, bool required, out double value, out string error)
    {
        var raw = query[name].ToString().Trim();
        if (raw.Length == 0 && !required)
        {
            value = 0;
            error = string.Empty;
            return true;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value) || value < min || value > max)
        {
            error = $"{name} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryLimit(IQueryCollection query, int defaultValue, int maxValue, out int limit, out string error)
    {
        var raw = query["limit"].ToString().Trim();
        if (raw.Length == 0)
        {
            limit = defaultValue;
            error = string.Empty;
            return true;
        }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > maxValue)
        {
            error = $"limit must be between 1 and {maxValue}.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryBoolean(IQueryCollection query, string name, out bool value, out string error)
    {
        var raw = query[name].ToString().Trim();
        if (raw.Length == 0)
        {
            value = false;
            error = string.Empty;
            return true;
        }
        if (!bool.TryParse(raw, out value))
        {
            error = $"{name} must be true or false.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static string? OptionalText(string raw, int maxLength, out string error)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            error = string.Empty;
            return null;
        }
        if (value.Length > maxLength)
        {
            error = $"text value cannot exceed {maxLength} characters.";
            return null;
        }
        error = string.Empty;
        return value;
    }

    private static string[]? Categories(string raw, out string error)
    {
        var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            error = string.Empty;
            return null;
        }
        if (values.Length > 20 || values.Any(value => value.Length > 80))
        {
            error = "categoryId accepts at most 20 values of 80 characters each.";
            return null;
        }
        error = string.Empty;
        return values;
    }
}
