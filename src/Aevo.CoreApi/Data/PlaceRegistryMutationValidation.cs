using System.Text.Json;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Data;

public sealed class PlaceMutationRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PlaceRegistryMutationValidation
{
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "candidate", "visible", "limited", "under_review", "closed", "removed", "merged"
    };

    private static readonly HashSet<string> SourceKinds = new(StringComparer.Ordinal)
    {
        "aevo_admin", "verified_business", "community", "booking_domain", "osm", "overture", "government", "derived"
    };

    public static Guid ValidateUpsert(PlaceAdminPlaceMutationRequestContract request)
    {
        if (request.Summary is null) throw Invalid("PLACE_SUMMARY_REQUIRED", "A Place summary is required.");
        if (!Guid.TryParse(request.Summary.Id, out var placeId) || placeId == Guid.Empty)
        {
            throw Invalid("PLACE_ID_INVALID", "Place summary id must be a non-empty UUID.");
        }
        if (request.ExpectedRevision is < 1) throw Invalid("PLACE_REVISION_INVALID", "Expected revision must be a positive integer.");
        if (request.Summary.Slug.Trim().Length is < 1 or > 240) throw Invalid("PLACE_SLUG_INVALID", "Place slug must be between 1 and 240 characters.");
        if (request.Summary.Name.Trim().Length is < 1 or > 500) throw Invalid("PLACE_NAME_INVALID", "Place name must be between 1 and 500 characters.");
        if (!Statuses.Contains(request.Summary.Status.Trim().ToLowerInvariant())) throw Invalid("PLACE_STATUS_INVALID", "Place status is not supported.");
        if (request.Summary.Category is null || string.IsNullOrWhiteSpace(request.Summary.Category.Id)) throw Invalid("PLACE_CATEGORY_INVALID", "Place category id is required.");
        ValidatePoint(request.Summary.DisplayPoint, "displayPoint");
        ValidatePoint(request.Summary.LabelPoint, "labelPoint");
        ValidateParent(placeId, request.Summary.ParentPlaceId);
        ValidateGeometry(request.CanonicalGeometry, "canonicalGeometry");
        ValidateGeometry(request.BoundingGeometry, "boundingGeometry");
        ValidateSource(request.SourceKind, request.SourceId, request.SourceRevision);
        ValidateReason(request.Reason);
        if (!string.IsNullOrWhiteSpace(request.EvidenceReference) && request.EvidenceReference.Trim().Length > 1024)
        {
            throw Invalid("PLACE_EVIDENCE_REFERENCE_INVALID", "Evidence reference cannot exceed 1024 characters.");
        }
        if (!string.IsNullOrWhiteSpace(request.ProjectionVersion) && request.ProjectionVersion.Trim().Length > 128)
        {
            throw Invalid("PLACE_PROJECTION_VERSION_INVALID", "Projection version cannot exceed 128 characters.");
        }
        return placeId;
    }

    public static void ValidateDelete(PlaceAdminDeleteRequestContract request)
    {
        if (request.ExpectedRevision is null or < 1) throw Invalid("PLACE_REVISION_REQUIRED", "Delete requires the current Place revision.");
        ValidateReason(request.Reason);
    }

    public static void ValidateLegacyMappingDelete(PlaceAdminLegacyMappingDeleteRequestContract request)
    {
        ValidateLegacyIdentity(request.Namespace, request.ExternalId, request.SourceVersion);
        ValidateReason(request.Reason);
    }

    public static void ValidateSourceLinkDelete(PlaceAdminSourceLinkDeleteRequestContract request)
    {
        ValidateReason(request.Reason);
    }

    private static void ValidatePoint(PlaceGeoPointContract? point, string field)
    {
        if (point is null) return;
        if (!double.IsFinite(point.Longitude) || point.Longitude is < -180 or > 180)
        {
            throw Invalid("PLACE_COORDINATE_INVALID", $"{field}.longitude is outside the allowed range.");
        }
        if (!double.IsFinite(point.Latitude) || point.Latitude is < -90 or > 90)
        {
            throw Invalid("PLACE_COORDINATE_INVALID", $"{field}.latitude is outside the allowed range.");
        }
    }

    private static void ValidateParent(Guid placeId, string? parentPlaceId)
    {
        if (string.IsNullOrWhiteSpace(parentPlaceId)) return;
        if (!Guid.TryParse(parentPlaceId, out var parentId) || parentId == Guid.Empty || parentId == placeId)
        {
            throw Invalid("PLACE_PARENT_INVALID", "Parent Place must be a different non-empty UUID.");
        }
    }

    private static void ValidateGeometry(PlaceGeometryContract? geometry, string field)
    {
        if (geometry is null) return;
        if (geometry.Type is not ("Point" or "Polygon" or "MultiPolygon") || geometry.Coordinates.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("PLACE_GEOMETRY_INVALID", $"{field} must be a Point, Polygon, or MultiPolygon with array coordinates.");
        }
    }

    private static void ValidateSource(string sourceKind, string? sourceId, string sourceRevision)
    {
        if (!SourceKinds.Contains(sourceKind.Trim().ToLowerInvariant())) throw Invalid("PLACE_SOURCE_KIND_INVALID", "Source kind is not supported.");
        if (!string.IsNullOrWhiteSpace(sourceId) && sourceId.Trim().Length > 500) throw Invalid("PLACE_SOURCE_ID_INVALID", "Source id cannot exceed 500 characters.");
        if (string.IsNullOrWhiteSpace(sourceRevision) || sourceRevision.Trim().Length > 256) throw Invalid("PLACE_SOURCE_REVISION_INVALID", "Source revision is required and must be bounded.");
    }

    private static void ValidateLegacyIdentity(string @namespace, string externalId, string sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(@namespace) || @namespace.Trim().Length > 128)
        {
            throw Invalid("LEGACY_NAMESPACE_INVALID", "Legacy namespace is required and must be bounded.");
        }
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Trim().Length > 500)
        {
            throw Invalid("LEGACY_EXTERNAL_ID_INVALID", "Legacy external id is required and must be bounded.");
        }
        if (string.IsNullOrWhiteSpace(sourceVersion) || sourceVersion.Trim().Length > 128)
        {
            throw Invalid("LEGACY_SOURCE_VERSION_INVALID", "Legacy source version is required and must be bounded.");
        }
    }

    private static void ValidateReason(string reason)
    {
        if (reason.Trim().Length is < 3 or > 500) throw Invalid("REASON_REQUIRED", "An administrative reason between 3 and 500 characters is required.");
    }

    private static PlaceMutationRequestException Invalid(string code, string message) => new(code, message);
}
