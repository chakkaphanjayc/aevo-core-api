using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.Feed;

public sealed record ValidatedFeedEvent(
    string EventId,
    string EventName,
    string FeedSessionId,
    string ItemToken,
    string ItemType,
    string ItemId,
    int? Position,
    string? Source,
    DateTimeOffset OccurredAt,
    string ConfigVersion,
    string RankingVersion,
    string? ExperimentVariant,
    string MetadataJson,
    string EventHash);

public sealed record FeedEventValidationResult(
    ValidatedFeedEvent? Event,
    string? ErrorCode = null)
{
    public bool IsValid => Event is not null;

    public static FeedEventValidationResult Invalid(string errorCode) => new(null, errorCode);
}

public static class FeedEventValidator
{
    private static readonly JsonSerializerOptions MetadataOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly HashSet<string> TraceOnlyEvents = new(StringComparer.Ordinal)
    {
        "trace_start", "trace_complete"
    };

    private static readonly HashSet<string> PlaceOnlyEvents = new(StringComparer.Ordinal)
    {
        "place_open", "booking_click"
    };

    public static FeedEventValidationResult Validate(
        FeedEventContract candidate,
        FeedItemTokenVerification tokenVerification,
        FeedPrincipal principal,
        DateTimeOffset now)
    {
        if (!tokenVerification.IsValid || tokenVerification.Payload is null)
        {
            return FeedEventValidationResult.Invalid("ITEM_TOKEN_INVALID");
        }

        if (tokenVerification.IsLegacy || tokenVerification.Payload.IsLegacy)
        {
            return FeedEventValidationResult.Invalid("ITEM_TOKEN_VERSION_UNSUPPORTED");
        }

        var token = tokenVerification.Payload;
        if (string.IsNullOrWhiteSpace(candidate.SchemaVersion)
            || !string.Equals(candidate.SchemaVersion, FeedApiContract.SchemaVersion, StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("EVENT_SCHEMA_UNSUPPORTED");
        }

        if (!IsSafeEventId(candidate.EventId))
        {
            return FeedEventValidationResult.Invalid("EVENT_ID_INVALID");
        }

        if (!FeedApiContract.SupportedEventNames.Contains(candidate.EventName))
        {
            return FeedEventValidationResult.Invalid("EVENT_NAME_UNSUPPORTED");
        }

        if (!Guid.TryParse(candidate.FeedSessionId, out var feedSessionId)
            || !string.Equals(feedSessionId.ToString("N"), token.FeedSessionId, StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("FEED_SESSION_INVALID");
        }

        if (!string.Equals(token.PrincipalBinding, principal.CursorBinding, StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("ITEM_TOKEN_PRINCIPAL_MISMATCH");
        }

        if (string.IsNullOrWhiteSpace(candidate.ItemToken)
            || candidate.ItemToken.Length > FeedApiContract.MaxEventItemTokenLength)
        {
            return FeedEventValidationResult.Invalid("ITEM_TOKEN_INVALID");
        }

        if (candidate.Source is not null
            && !string.Equals(candidate.Source, token.EntityType, StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("EVENT_SOURCE_MISMATCH");
        }

        if (candidate.Position is < 0 or > 10_000
            || candidate.Position is int position && position != token.Position)
        {
            return FeedEventValidationResult.Invalid("EVENT_POSITION_INVALID");
        }

        if (TraceOnlyEvents.Contains(candidate.EventName)
            && !string.Equals(token.EntityType, "TRACE", StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("EVENT_ENTITY_TYPE_INVALID");
        }

        if (PlaceOnlyEvents.Contains(candidate.EventName)
            && !string.Equals(token.EntityType, "PLACE", StringComparison.Ordinal))
        {
            return FeedEventValidationResult.Invalid("EVENT_ENTITY_TYPE_INVALID");
        }

        var occurredAt = candidate.OccurredAt.ToUniversalTime();
        if (occurredAt < now.AddDays(-7) || occurredAt > now.AddMinutes(5))
        {
            return FeedEventValidationResult.Invalid("EVENT_TIMESTAMP_SKEW");
        }

        var metadata = candidate.Metadata ?? new FeedEventMetadataContract();
        if (!ValidateMetadata(metadata, candidate.EventName, token.EntityType))
        {
            return FeedEventValidationResult.Invalid("EVENT_METADATA_INVALID");
        }

        var metadataJson = JsonSerializer.Serialize(metadata, MetadataOptions);
        if (Encoding.UTF8.GetByteCount(metadataJson) > FeedApiContract.MaxEventMetadataBytes)
        {
            return FeedEventValidationResult.Invalid("EVENT_METADATA_TOO_LARGE");
        }

        var eventHash = HashEvent(
            principal.CursorBinding,
            candidate,
            token,
            occurredAt,
            metadataJson);
        return new FeedEventValidationResult(new ValidatedFeedEvent(
            candidate.EventId.Trim(),
            candidate.EventName,
            token.FeedSessionId,
            candidate.ItemToken,
            token.EntityType,
            token.EntityId,
            candidate.Position ?? token.Position,
            token.EntityType,
            occurredAt,
            token.ConfigVersion!,
            token.RankingVersion!,
            token.ExperimentVariant,
            metadataJson,
            eventHash));
    }

    public static bool ShouldSample(string principalBinding, string eventId, int samplePercent)
    {
        if (samplePercent <= 0) return false;
        if (samplePercent >= 100) return true;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{principalBinding}:{eventId}"));
        var bucket = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(0, sizeof(uint))) % 100;
        return bucket < samplePercent;
    }

    private static bool ValidateMetadata(
        FeedEventMetadataContract metadata,
        string eventName,
        string itemType)
    {
        if (metadata.ClientPlatform is not null
            && metadata.ClientPlatform is not ("web" or "pwa" or "capacitor")) return false;
        if (metadata.Surface is not null && metadata.Surface != "explore") return false;
        if (metadata.Tab is not null && !FeedApiContract.SupportedTabs.Contains(metadata.Tab)) return false;
        if (metadata.VisibleRatio is double ratio && (!double.IsFinite(ratio) || ratio is < 0 or > 1)) return false;
        if (metadata.VisibleDurationMs is int duration && duration is < 0 or > 86_400_000) return false;

        if (metadata.ReasonCode is not null)
        {
            var allowed = itemType == "TRACE"
                ? new[] { "FOLLOWING_TRACER", "TASTE_MATCH", "POPULAR", "NEW_TRACE" }
                : new[] { "NEARBY_PLACE", "POPULAR_PLACE" };
            if (!allowed.Contains(metadata.ReasonCode, StringComparer.Ordinal)) return false;
        }

        if (eventName == "impression"
            && (metadata.VisibleRatio is not >= 0.5
                || metadata.VisibleDurationMs is not >= 1_000)) return false;
        if (eventName == "engaged_view"
            && (metadata.VisibleRatio is not >= 0.5
                || metadata.VisibleDurationMs is not >= 3_000)) return false;

        return true;
    }

    private static bool IsSafeEventId(string? value)
    {
        return value is { Length: > 0 and <= FeedApiContract.MaxEventIdLength }
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':');
    }

    private static string HashEvent(
        string principalBinding,
        FeedEventContract candidate,
        FeedItemTokenPayload token,
        DateTimeOffset occurredAt,
        string metadataJson)
    {
        var canonical = string.Join(
            '|',
            principalBinding,
            candidate.EventId.Trim(),
            candidate.SchemaVersion,
            candidate.EventName,
            token.FeedSessionId,
            candidate.ItemToken,
            token.EntityType,
            token.EntityId,
            candidate.Position?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            occurredAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            metadataJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
