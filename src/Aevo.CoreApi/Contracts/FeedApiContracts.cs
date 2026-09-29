using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevo.CoreApi.Contracts;

/// <summary>
/// Versioned Feed boundary. FEED-001 publishes the contract and private data
/// seam; FEED-003 supplies the public handler and session/cursor primitives.
/// </summary>
public static class FeedApiContract
{
    public const string Version = "v1";
    public const string SchemaVersion = "1";
    public const string Release = "2026-09-23";
    public const string Route = "/api/v1/public/feed";
    public const string EventRoute = "/api/v1/public/feed/events";
    public const string FeedbackRoute = "/api/v1/public/feed/feedback";
    public const string FeedbackHistoryRoute = "/api/v1/public/feed/feedback/history";
    public const string ApplicationCode = "GO";
    public const string DefaultTab = "for_you";
    public const int DefaultPageSize = 24;
    public const int MaxPageSize = 50;
    public const int MaxQueryLength = 200;
    public const int MaxAreaLength = 120;
    public const int MaxCursorLength = 4096;
    public const int MaxEventBatchSize = 100;
    public const int MaxEventBodyBytes = 512 * 1024;
    public const int MaxEventIdLength = 128;
    public const int MaxEventItemTokenLength = 4096;
    public const int MaxEventMetadataBytes = 4096;
    public const int MaxFeedbackBodyBytes = 16 * 1024;
    public const int MaxFeedbackReasonLength = 64;
    public const int DefaultFeedbackHistoryLimit = 20;
    public const int MaxFeedbackHistoryLimit = 50;
    public const int MinCoarseRadiusMeters = 100;
    public const int MaxCoarseRadiusMeters = 50_000;
    public const int MaxVibeLength = 32;
    public const int MaxCategoryLength = 64;
    public const int MaxDiscoveryDateLength = 10;
    public const int MinPartySize = 1;
    public const int MaxPartySize = 20;

    public static readonly IReadOnlyList<string> Routes = new[] { Route, EventRoute, FeedbackRoute, FeedbackHistoryRoute };

    public static readonly IReadOnlySet<string> SupportedTabs = new HashSet<string>(StringComparer.Ordinal)
    {
        "for_you", "following", "nearby"
    };

    public static readonly IReadOnlySet<string> SupportedEventNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "impression", "engaged_view", "open", "click", "save", "share",
        "trace_start", "trace_complete", "place_open", "booking_click"
    };
}

public sealed record FeedCoarseLocationContract(
    string? Area = null,
    string? Geohash = null,
    int? RadiusMeters = null);

/// <summary>
/// Request-local discovery intent. It is included in the signed request
/// fingerprint but is not a durable taste/profile write.
/// </summary>
public sealed record FeedDiscoveryIntentContract(
    string? Vibe = null,
    string? Category = null,
    string? Date = null,
    int? PartySize = null);

public sealed record FeedRequestContract(
    string Surface,
    string? Tab = null,
    string? Query = null,
    string? Area = null,
    FeedCoarseLocationContract? CoarseLocation = null,
    string? Cursor = null,
    int? Limit = null,
    FeedDiscoveryIntentContract? DiscoveryIntent = null);

[JsonConverter(typeof(FeedItemContractJsonConverter))]
public interface IFeedItemContract
{
    string ItemType { get; }
    string Id { get; }
    string ItemToken { get; }
}

/// <summary>
/// Keeps the public Feed item union concrete when it is serialized through
/// the internal common interface. Without an explicit converter,
/// System.Text.Json emits only the interface identity fields and silently
/// drops the TRACE/PLACE payload needed by public clients.
/// </summary>
public sealed class FeedItemContractJsonConverter : JsonConverter<IFeedItemContract>
{
    public override IFeedItemContract Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (!root.TryGetProperty("itemType", out var itemType)
            || itemType.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Feed itemType is required.");
        }

        return itemType.GetString() switch
        {
            "TRACE" => root.Deserialize<FeedTraceItemContract>(options)
                ?? throw new JsonException("TRACE Feed item could not be deserialized."),
            "PLACE" => root.Deserialize<FeedPlaceItemContract>(options)
                ?? throw new JsonException("PLACE Feed item could not be deserialized."),
            _ => throw new JsonException("Unsupported Feed itemType.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        IFeedItemContract value,
        JsonSerializerOptions options)
    {
        switch (value)
        {
            case FeedTraceItemContract trace:
                JsonSerializer.Serialize(writer, trace, options);
                return;
            case FeedPlaceItemContract place:
                JsonSerializer.Serialize(writer, place, options);
                return;
            default:
                throw new JsonException($"Unsupported Feed item contract type: {value.GetType().Name}.");
        }
    }
}

public sealed record FeedTraceItemContract(
    string ItemType,
    string Id,
    string ItemToken,
    string Slug,
    string Title,
    string Description,
    string CreatorName,
    string Area,
    IReadOnlyList<string> TopicTags,
    int StopCount,
    string ReasonCode,
    DateTimeOffset PublishedAt) : IFeedItemContract;

public sealed record FeedPlaceReferenceContract(
    string Namespace,
    string ExternalId,
    string SourceVersion,
    string? CanonicalPlaceId,
    string ResolutionStatus,
    bool Redirected,
    string? RedirectReason,
    string ResolverVersion);

public sealed record FeedPlaceItemContract(
    string ItemType,
    string Id,
    string ItemToken,
    string Slug,
    string Name,
    string Description,
    string Area,
    string Category,
    string? ImageUrl,
    string ReasonCode,
    FeedPlaceReferenceContract? PlaceReference = null) : IFeedItemContract;

public sealed record FeedResponseContract(
    string FeedSessionId,
    string ConfigVersion,
    string RankingVersion,
    IReadOnlyList<IFeedItemContract> Items,
    string? NextCursor,
    bool Degraded,
    string? RequestId = null,
    IReadOnlyList<DiscoveryModuleContract>? Modules = null);

public sealed record FeedEventMetadataContract(
    string? ClientPlatform = null,
    string? Surface = null,
    string? Tab = null,
    string? ReasonCode = null,
    double? VisibleRatio = null,
    int? VisibleDurationMs = null);

public sealed record FeedEventContract(
    string SchemaVersion,
    string EventId,
    string EventName,
    string FeedSessionId,
    string ItemToken,
    DateTimeOffset OccurredAt,
    int? Position = null,
    string? Source = null,
    FeedEventMetadataContract? Metadata = null);

public sealed record FeedEventBatchContract(
    IReadOnlyList<FeedEventContract>? Events);

public sealed record FeedEventResultContract(
    string EventId,
    string Status,
    string? ErrorCode = null);

public sealed record FeedEventBatchResponseContract(
    int Accepted,
    int Duplicates,
    int SampledOut,
    int Rejected,
    IReadOnlyList<FeedEventResultContract> Results,
    string? RequestId = null);

public sealed record FeedEventHealthContract(
    long Pending,
    long Processing,
    long Failed,
    long DeadLetter,
    long AcceptedLast24Hours,
    long ProcessedLast24Hours,
    DateTimeOffset? LastProcessedAt,
    string ConsumerVersion,
    string? RequestId = null);

public sealed record FeedDiscoveryOutcomeSummaryContract(
    long Impressions,
    long Opens,
    long PlaceOpens,
    long Saves,
    long TraceStarts,
    long TraceCompletes,
    long BookingClicks);

public sealed record FeedDiscoveryGuardrailSummaryContract(
    long CurrentActiveHides,
    long HideTransitions,
    long EventDeadLetters);

public sealed record FeedDiscoveryEvaluationContract(
    int WindowDays,
    string EvaluationStatus,
    string RankingMode,
    bool LiveServing,
    FeedDiscoveryOutcomeSummaryContract Outcomes,
    FeedDiscoveryGuardrailSummaryContract Guardrails,
    double? OpenRate,
    double? BookingClickRate,
    DateTimeOffset GeneratedAt,
    string? RequestId = null);

public sealed record FeedNegativeFeedbackRequestContract(
    string SchemaVersion,
    string FeedSessionId,
    string ItemToken,
    string Action,
    string? ReasonCode = null);

public sealed record FeedNegativeFeedbackResponseContract(
    string ItemType,
    string ItemId,
    string Action,
    bool Active,
    DateTimeOffset UpdatedAt,
    string? RequestId = null);

public sealed record FeedNegativeFeedbackHistoryEntryContract(
    string ItemType,
    string ItemId,
    string Action,
    string? ReasonCode,
    bool Active,
    DateTimeOffset CreatedAt);

public sealed record FeedNegativeFeedbackHistoryResponseContract(
    IReadOnlyList<FeedNegativeFeedbackHistoryEntryContract> Entries,
    string? RequestId = null);
