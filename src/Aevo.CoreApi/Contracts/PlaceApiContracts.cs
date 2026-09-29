using System.Text.Json;

namespace Aevo.CoreApi.Contracts;

/// <summary>
/// MAP-001 transport vocabulary. The DTOs are shared by the projection-gated
/// Core handlers and the cross-repository adapters.
/// </summary>
public static class PlaceApiContract
{
    public const string Version = "v1";
    public const string SchemaVersion = "1";
    public const string Release = "2026-09-27";
    public const string MapRoute = "/api/v1/public/places/map";
    public const string SearchRoute = "/api/v1/public/places/search";
    public const string NearbyRoute = "/api/v1/public/places/nearby";
    public const string DetailRoute = "/api/v1/public/places/:placeId";
    public const string SavedPlacesRoute = "/api/v1/public/me/saved-places";
    public const string SaveRoute = "/api/v1/public/places/:placeId/save";
    public const string AdminPlacesRoute = "/api/v1/admin/map/places";
    public const string AdminPlaceRoute = "/api/v1/admin/map/places/:placeId";
    public const string AdminLegacyMappingsRoute = "/api/v1/admin/map/legacy-mappings";
    public const string AdminPlaceSourceLinksRoute = "/api/v1/admin/map/source-links/:sourceLinkId";
    public const int DefaultOverlayLimit = 300;
    public const int MaxOverlayLimit = 500;
    public const int DefaultListLimit = 24;
    public const int MaxListLimit = 50;
    public const double MaxViewportLongitudeSpan = 90;
    public const double MaxViewportLatitudeSpan = 60;
    public const int MaxRedirectHops = 5;

    public static readonly IReadOnlyList<string> Routes = new[]
    {
        MapRoute,
        SearchRoute,
        NearbyRoute,
        DetailRoute,
        SavedPlacesRoute,
        SaveRoute
    };
}

public sealed record PlaceBoundsContract(
    double West,
    double South,
    double East,
    double North);

public sealed record PlaceGeoPointContract(
    double Longitude,
    double Latitude);

/// <remarks>
/// Coordinates are the GeoJSON coordinate array. Type is Point, Polygon, or
/// MultiPolygon; this generic envelope keeps serialization stable while the
/// canonical geometry model expands without changing Place IDs.
/// </remarks>
public sealed record PlaceGeometryContract(
    string Type,
    JsonElement Coordinates);

public sealed record PlaceLocalizedNameContract(
    string Locale,
    string Value,
    string Kind);

public sealed record PlaceAddressContract(
    string? CountryCode,
    string? Country,
    string? AdministrativeArea1,
    string? AdministrativeArea2,
    string? Locality,
    string? Neighborhood,
    string? Street,
    string? HouseNumber,
    string? PostalCode,
    string? FormattedAddress,
    IReadOnlyList<PlaceLocalizedNameContract> LocalizedAddresses);

public sealed record PlaceVerificationContract(
    string Status,
    string? Label,
    DateTimeOffset? VerifiedAt,
    string? VerificationRevision);

public sealed record PlaceCategoryContract(
    string Id,
    string Label,
    IReadOnlyList<PlaceLocalizedNameContract> LocalizedLabels);

public sealed record PlaceFreshnessContract(
    string State,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ExpiresAt,
    string? SourceRevision);

public sealed record PlaceCapabilitySummaryContract(
    bool Bookable,
    bool QueueSupported,
    bool AevoPlayPartner,
    bool CommerceEnabled,
    string? PublicBookingRoute,
    PlaceFreshnessContract Freshness);

public sealed record PlaceBusinessLinkContract(
    string Relationship,
    string? OrganizationId,
    string? BusinessId,
    string? BranchId,
    string? StoreId,
    string? VenueId,
    string? DisplayName,
    bool IsPrimary,
    string VerificationStatus);

public sealed record PlaceAttributionContract(
    string SourceKind,
    string? SourceId,
    string Label,
    string? Url,
    string? License,
    string? RequiredText);

public sealed record PlaceRedirectMetadataContract(
    string FromPlaceId,
    string ToPlaceId,
    string Reason,
    int Hops,
    DateTimeOffset ResolvedAt);

public sealed record PlaceSummaryContract(
    string Id,
    string Slug,
    string Name,
    IReadOnlyList<PlaceLocalizedNameContract> LocalizedNames,
    PlaceCategoryContract Category,
    string Status,
    PlaceGeoPointContract? DisplayPoint,
    PlaceGeoPointContract? LabelPoint,
    string? Area,
    PlaceAddressContract? Address,
    string? ParentPlaceId,
    PlaceVerificationContract Verification,
    PlaceBusinessLinkContract? Business,
    PlaceCapabilitySummaryContract Capabilities,
    IReadOnlyList<PlaceAttributionContract> Attribution,
    PlaceRedirectMetadataContract? RedirectFrom);

public sealed record PlaceMapOverlayPlacePropertiesContract(
    string FeatureKind,
    string PlaceId,
    string Name,
    string CategoryId,
    string MarkerKind,
    string VerificationStatus,
    bool Bookable,
    bool AevoPlayPartner);

public sealed record PlaceMapOverlayFeatureContract(
    string Type,
    string Id,
    PlaceGeometryContract Geometry,
    PlaceMapOverlayPlacePropertiesContract Properties);

public sealed record PlaceResponseMetadataContract(
    string ContractVersion,
    string SchemaVersion,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset GeneratedAt,
    PlaceFreshnessContract Freshness,
    string? RequestId = null);

public sealed record PlaceMapOverlayRequestContract(
    PlaceBoundsContract Bounds,
    double Zoom,
    string? Query = null,
    IReadOnlyList<string>? CategoryIds = null,
    int? Limit = null,
    bool SavedOnly = false);

public sealed record PlaceMapOverlayDensityContract(
    string Mode,
    int FeatureCount,
    int ClusterCount,
    int MaxFeatures);

public sealed record PlaceMapOverlayResponseContract(
    string ContractVersion,
    string SchemaVersion,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset GeneratedAt,
    PlaceFreshnessContract Freshness,
    PlaceBoundsContract Bounds,
    double Zoom,
    IReadOnlyList<PlaceMapOverlayFeatureContract> Features,
    bool Truncated,
    PlaceMapOverlayDensityContract Density,
    string? RequestId = null);

public sealed record PlaceSearchRequestContract(
    string? Query = null,
    PlaceBoundsContract? Bounds = null,
    string? Area = null,
    IReadOnlyList<string>? CategoryIds = null,
    string? Sort = null,
    string? Cursor = null,
    int? Limit = null,
    bool SavedOnly = false);

public sealed record PlaceSearchResultContract(
    string Id,
    string Slug,
    string Name,
    IReadOnlyList<PlaceLocalizedNameContract> LocalizedNames,
    PlaceCategoryContract Category,
    string Status,
    PlaceGeoPointContract? DisplayPoint,
    PlaceGeoPointContract? LabelPoint,
    string? Area,
    PlaceAddressContract? Address,
    string? ParentPlaceId,
    PlaceVerificationContract Verification,
    PlaceBusinessLinkContract? Business,
    PlaceCapabilitySummaryContract Capabilities,
    IReadOnlyList<PlaceAttributionContract> Attribution,
    PlaceRedirectMetadataContract? RedirectFrom,
    int Rank,
    double? DistanceMeters,
    IReadOnlyList<string> MatchReasons);

public sealed record PlaceSearchResponseContract(
    string ContractVersion,
    string SchemaVersion,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset GeneratedAt,
    PlaceFreshnessContract Freshness,
    IReadOnlyList<PlaceSearchResultContract> Data,
    string? NextCursor,
    int? TotalApproximate,
    bool Truncated,
    string? RequestId = null);

public sealed record PlaceNearbyRequestContract(
    PlaceGeoPointContract Origin,
    int RadiusMeters,
    IReadOnlyList<string>? CategoryIds = null,
    string? Cursor = null,
    int? Limit = null);

public sealed record PlaceNearbyResponseContract(
    string ContractVersion,
    string SchemaVersion,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset GeneratedAt,
    PlaceFreshnessContract Freshness,
    PlaceGeoPointContract Origin,
    int RadiusMeters,
    IReadOnlyList<PlaceSearchResultContract> Data,
    string? NextCursor,
    bool Truncated,
    string? RequestId = null);

public sealed record PlaceDetailContract(
    string Id,
    string Slug,
    string Name,
    IReadOnlyList<PlaceLocalizedNameContract> LocalizedNames,
    PlaceCategoryContract Category,
    string Status,
    PlaceGeoPointContract? DisplayPoint,
    PlaceGeoPointContract? LabelPoint,
    string? Area,
    PlaceAddressContract? Address,
    string? ParentPlaceId,
    PlaceVerificationContract Verification,
    PlaceBusinessLinkContract? Business,
    PlaceCapabilitySummaryContract Capabilities,
    IReadOnlyList<PlaceAttributionContract> Attribution,
    PlaceRedirectMetadataContract? RedirectFrom,
    PlaceGeometryContract? CanonicalGeometry,
    PlaceGeoPointContract? Centroid,
    PlaceGeometryContract? BoundingGeometry,
    IReadOnlyList<string> Children,
    IReadOnlyList<PlaceBusinessLinkContract> BusinessLinks,
    IReadOnlyList<JsonElement> FieldProvenance,
    IReadOnlyList<JsonElement> LegacyReferences);

public sealed record PlaceDetailResponseContract(
    string ContractVersion,
    string SchemaVersion,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset GeneratedAt,
    PlaceFreshnessContract Freshness,
    PlaceDetailContract Place,
    string? RequestId = null);

public sealed record FeedSavedPlaceResponseContract(
    Guid PlaceId,
    bool Saved,
    bool Changed,
    DateTimeOffset UpdatedAt,
    string? RequestId = null);

public sealed record FeedSavedPlaceEntryContract(
    Guid PlaceId,
    DateTimeOffset SavedAt);

public sealed record FeedSavedPlacesResponseContract(
    IReadOnlyList<FeedSavedPlaceEntryContract> SavedPlaces,
    string? RequestId = null);

public sealed record PlaceWorkflowEvidenceContract(
    string EvidenceType,
    string EvidenceToken);

public sealed record PlaceClaimRequestContract(
    Guid? BusinessId,
    Guid? BranchId,
    IReadOnlyList<string> RequestedFields,
    IReadOnlyList<PlaceWorkflowEvidenceContract> Evidence,
    string? IdempotencyKey = null);

public sealed record PlaceSubmissionRequestContract(
    Guid? TargetPlaceId,
    string SubmissionType,
    JsonElement ProposedChanges,
    IReadOnlyList<PlaceWorkflowEvidenceContract> Evidence,
    string? IdempotencyKey = null);

public sealed record PlaceRelationshipRequestContract(
    string RelationshipType,
    Guid TargetId,
    bool IsPrimary,
    string? IdempotencyKey = null);

public sealed record PlaceWorkflowDecisionRequestContract(
    string Decision,
    string Reason,
    string? IdempotencyKey = null);

public sealed record PlaceWorkflowMutationResponseContract(
    bool Success,
    Guid WorkflowId,
    string AggregateType,
    string Status,
    bool ReviewRequired,
    string? RequestId = null);

public sealed record PlaceAdminPlaceMutationRequestContract(
    PlaceSummaryContract Summary,
    PlaceGeometryContract? CanonicalGeometry,
    PlaceGeometryContract? BoundingGeometry,
    string SourceKind,
    string? SourceId,
    string? EvidenceReference,
    string SourceRevision,
    string Reason,
    long? ExpectedRevision = null,
    string? ProjectionVersion = null);

public sealed record PlaceAdminDeleteRequestContract(
    string Reason,
    long? ExpectedRevision = null);

public sealed record PlaceAdminMutationResponseContract(
    bool Success,
    Guid PlaceId,
    long Revision,
    string Operation,
    string Status,
    string ProjectionVersion,
    string SourceRevision,
    bool ProjectionPublished,
    string? RequestId = null);

public sealed record PlaceAdminPlaceResourceContract(
    bool Success,
    Guid PlaceId,
    long Revision,
    PlaceSummaryContract Summary,
    PlaceGeometryContract? CanonicalGeometry,
    PlaceGeometryContract? BoundingGeometry,
    string SourceKind,
    string? SourceId,
    string? EvidenceReference,
    string SourceRevision,
    string? ProjectionVersion,
    string? ProjectionStatus,
    string? FreshnessState,
    DateTimeOffset? ProjectionGeneratedAt);

public sealed record PlaceAdminLegacyMappingDeleteRequestContract(
    string Namespace,
    string ExternalId,
    string SourceVersion,
    string Reason,
    bool AllowLinked = false);

public sealed record PlaceAdminSourceLinkDeleteRequestContract(
    string Reason,
    bool AllowLinked = false);

public sealed record PlaceProjectionReplayFixtureContract(
    string Label,
    PlaceSummaryContract? Summary);

public sealed record PlaceProjectionReplayRequestContract(
    Guid RunId,
    Guid ActorId,
    string ProjectionVersion,
    string SourceRevision,
    DateTimeOffset ObservedAt,
    int TtlSeconds,
    IReadOnlyList<PlaceProjectionReplayFixtureContract> Fixtures,
    string Reason);

public sealed record PlaceProjectionReplayFailureContract(
    string Label,
    string Code,
    string Message);

public sealed record PlaceProjectionReplayResponseContract(
    bool Success,
    Guid RunId,
    string Status,
    int InputCount,
    int PublishedCount,
    int FailedCount,
    int MissingPlaceCount,
    int RejectedPlaceCount,
    string? FailureCode,
    string ReplayFingerprint,
    bool IsDeterministic,
    bool IsWithinPayloadBudget,
    bool AlreadyApplied,
    IReadOnlyList<PlaceProjectionReplayFailureContract> Failures,
    string? RequestId = null);

public sealed record PlaceAdminLegacyDeleteResponseContract(
    bool Success,
    string RecordKind,
    string RecordKey,
    string Namespace,
    string ExternalId,
    string SourceVersion,
    Guid? PlaceId,
    bool WasLinked,
    string? RequestId = null);

public sealed record AdminPlaceWorkflowRecord(
    Guid WorkflowId,
    string AggregateType,
    Guid? PlaceId,
    Guid? OrganizationId,
    Guid? ActorId,
    string Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    int EvidenceCount,
    IReadOnlyList<string> RequestedFields,
    JsonElement Summary);
