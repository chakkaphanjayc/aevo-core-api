using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Feed;
using Npgsql;
using NpgsqlTypes;

namespace Aevo.CoreApi.Data;

public sealed record FeedConfigValidationData(
    bool Valid,
    string ValidatorVersion,
    IReadOnlyList<FeedConfigValidationIssue> Errors,
    IReadOnlyList<FeedConfigValidationIssue> Warnings,
    DateTimeOffset? ValidatedAt);

public sealed record FeedConfigRevisionData(
    Guid RevisionId,
    long Version,
    string SchemaVersion,
    string Status,
    JsonElement Config,
    string ContentHash,
    Guid? SourceRevisionId,
    Guid? AuthorId,
    string Reason,
    DateTimeOffset CreatedAt,
    FeedConfigValidationData Validation);

public sealed record FeedConfigPublicationData(
    string ScopeKey,
    Guid ActiveRevisionId,
    long ActiveVersion,
    long PointerVersion,
    Guid? PreviousRevisionId,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    string PropagationStatus,
    long? LastPropagatedVersion,
    DateTimeOffset? LastPropagatedAt,
    string? LastErrorCode);

public sealed record FeedConfigPropagationData(
    string RuntimeName,
    Guid? ObservedRevisionId,
    long? ObservedVersion,
    string Status,
    DateTimeOffset? LastCheckedAt,
    int? LatencyMs,
    string? LastErrorCode);

public sealed partial class CoreDataStore
{
    private static readonly JsonSerializerOptions FeedConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FeedConfigRevisionData?> GetActiveFeedConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.revision_id, r.version, r.schema_version, 'ACTIVE', r.config,
                   r.content_hash, r.source_revision_id, r.author_id, r.reason, r.created_at,
                   coalesce(v.valid, false), coalesce(v.validator_version, ''),
                   coalesce(v.errors, '[]'::jsonb), coalesce(v.warnings, '[]'::jsonb), v.validated_at
            from aevo_feed_config_publications p
            join aevo_feed_config_revisions r on r.revision_id = p.active_revision_id
            left join lateral (
              select valid, validator_version, errors, warnings, validated_at
              from aevo_feed_config_validation_results
              where revision_id = r.revision_id
              order by validated_at desc
              limit 1
            ) v on true
            where p.scope_key = @scope_key
            """, connection);
        command.Parameters.AddWithValue("scope_key", FeedConfigContract.ScopeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<FeedConfigRevisionData?> GetLastValidFeedConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select r.revision_id, r.version, r.schema_version,
                   case when p.active_revision_id = r.revision_id then 'ACTIVE' else 'VALID' end,
                   r.config, r.content_hash, r.source_revision_id, r.author_id, r.reason, r.created_at,
                   true, v.validator_version, v.errors, v.warnings, v.validated_at
            from aevo_feed_config_revisions r
            join aevo_feed_config_validation_results v on v.revision_id = r.revision_id and v.valid = true
            left join aevo_feed_config_publications p on p.scope_key = @scope_key
            where v.validator_version = @validator_version
              and (
                r.status = 'ACTIVE'
                or exists (
                  select 1
                  from aevo_feed_config_audit_events a
                  where a.revision_id = r.revision_id
                    and a.action in ('PUBLISHED', 'ROLLED_BACK')
                )
              )
            order by v.validated_at desc, r.version desc
            limit 1
            """, connection);
        command.Parameters.AddWithValue("scope_key", FeedConfigContract.ScopeKey);
        command.Parameters.AddWithValue("validator_version", FeedConfigContract.ValidatorVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<FeedConfigRevisionData?> GetFeedConfigRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadRevisionByIdAsync(connection, null, revisionId, cancellationToken);
    }

    public async Task<FeedConfigRevisionData?> GetFeedConfigRevisionByVersionAsync(long version, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = RevisionCommand(connection, null, "where r.version = @version");
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    public async Task<IReadOnlyList<FeedConfigRevisionData>> ListFeedConfigRevisionsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = RevisionCommand(
            connection,
            null,
            "order by r.version desc limit @limit");
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var revisions = new List<FeedConfigRevisionData>();
        while (await reader.ReadAsync(cancellationToken)) revisions.Add(ReadRevision(reader));
        return revisions;
    }

    public async Task<FeedConfigRevisionData> CreateFeedConfigDraftAsync(
        Guid actorId,
        string reason,
        string requestId,
        string idempotencyKey,
        JsonElement config,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdempotencyLockAsync(connection, transaction, idempotencyKey, cancellationToken);

        var existing = await ReadIdempotentActionAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Action != "DRAFT_CREATED") throw new FeedConfigIdempotencyConflictException();
            if (existing.Value.RevisionId is null) throw new CoreDatabaseException("Feed config idempotency record is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            var existingRevision = await GetFeedConfigRevisionAsync(existing.Value.RevisionId.Value, cancellationToken);
            if (existingRevision is null) throw new CoreDatabaseException("Feed config idempotency record points to a missing revision.");
            if (existingRevision.AuthorId != actorId
                || !string.Equals(existingRevision.Reason, reason, StringComparison.Ordinal)
                || !string.Equals(existingRevision.ContentHash, contentHash, StringComparison.Ordinal))
            {
                throw new FeedConfigIdempotencyConflictException();
            }
            return existingRevision;
        }

        var revisionId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand(
            """
            insert into aevo_feed_config_revisions
              (revision_id, schema_version, status, config, content_hash, author_id, author_app_code, reason)
            values (@revision_id, @schema_version, 'DRAFT', @config, @content_hash, @author_id, 'ADMIN', @reason)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision_id", revisionId);
            command.Parameters.AddWithValue("schema_version", FeedConfigContract.SchemaVersion);
            command.Parameters.Add(new NpgsqlParameter("config", NpgsqlDbType.Jsonb) { Value = config.GetRawText() });
            command.Parameters.AddWithValue("content_hash", contentHash);
            command.Parameters.AddWithValue("author_id", actorId);
            command.Parameters.AddWithValue("reason", reason);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var created = await ReadRevisionByIdAsync(connection, transaction, revisionId, cancellationToken)
            ?? throw new CoreDatabaseException("Feed config draft could not be read after insert.");
        var after = JsonSerializer.SerializeToElement(
            new { revisionId, version = created.Version, contentHash },
            FeedConfigJsonOptions);
        await InsertFeedConfigAuditAsync(connection, transaction, actorId, "DRAFT_CREATED", revisionId, null, created.Version, reason, requestId, idempotencyKey, after, cancellationToken);
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "FEED_CONFIG_DRAFT_CREATED", "feed_config_revision", revisionId.ToString(), reason, null, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<FeedConfigValidationData> RecordFeedConfigValidationAsync(
        Guid actorId,
        Guid revisionId,
        string reason,
        string requestId,
        string idempotencyKey,
        FeedConfigValidationResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdempotencyLockAsync(connection, transaction, idempotencyKey, cancellationToken);
        var revision = await ReadRevisionByIdAsync(connection, transaction, revisionId, cancellationToken)
            ?? throw new FeedConfigNotFoundException();
        var existing = await ReadIdempotentActionAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Action != "VALIDATED" || existing.Value.RevisionId != revisionId) throw new FeedConfigIdempotencyConflictException();
            await transaction.CommitAsync(cancellationToken);
            return revision.Validation;
        }

        var errors = JsonSerializer.SerializeToElement(result.Errors, FeedConfigJsonOptions);
        var warnings = JsonSerializer.SerializeToElement(result.Warnings, FeedConfigJsonOptions);
        await using (var command = new NpgsqlCommand(
            """
            insert into aevo_feed_config_validation_results
              (revision_id, validator_version, valid, errors, warnings, validated_by, validated_at)
            values (@revision_id, @validator_version, @valid, @errors, @warnings, @validated_by, now())
            on conflict (revision_id, validator_version) do update set
              valid = excluded.valid,
              errors = excluded.errors,
              warnings = excluded.warnings,
              validated_by = excluded.validated_by,
              validated_at = excluded.validated_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision_id", revisionId);
            command.Parameters.AddWithValue("validator_version", result.ValidatorVersion);
            command.Parameters.AddWithValue("valid", result.Valid);
            command.Parameters.Add(new NpgsqlParameter("errors", NpgsqlDbType.Jsonb) { Value = errors.GetRawText() });
            command.Parameters.Add(new NpgsqlParameter("warnings", NpgsqlDbType.Jsonb) { Value = warnings.GetRawText() });
            command.Parameters.AddWithValue("validated_by", actorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var after = JsonSerializer.SerializeToElement(
            new { revisionId, version = revision.Version, valid = result.Valid, validatorVersion = result.ValidatorVersion },
            FeedConfigJsonOptions);
        await InsertFeedConfigAuditAsync(connection, transaction, actorId, "VALIDATED", revisionId, null, revision.Version, reason, requestId, idempotencyKey, after, cancellationToken);
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "FEED_CONFIG_VALIDATED", "feed_config_revision", revisionId.ToString(), reason, null, after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FeedConfigValidationData(result.Valid, result.ValidatorVersion, result.Errors, result.Warnings, DateTimeOffset.UtcNow);
    }

    public async Task<FeedConfigPublicationData> PublishFeedConfigAsync(
        Guid actorId,
        Guid revisionId,
        long? expectedActiveVersion,
        string reason,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdempotencyLockAsync(connection, transaction, idempotencyKey, cancellationToken);
        var existing = await ReadIdempotentActionAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Action != "PUBLISHED" || existing.Value.RevisionId != revisionId) throw new FeedConfigIdempotencyConflictException();
            var existingPublication = await ReadPublicationAsync(connection, transaction, cancellationToken);
            if (existingPublication is null) throw new CoreDatabaseException("Feed config publication idempotency record is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            return existingPublication;
        }

        var publication = await ReadPublicationAsync(connection, transaction, cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        if (expectedActiveVersion.HasValue && expectedActiveVersion.Value != publication.ActiveVersion)
        {
            throw new FeedConfigVersionConflictException(publication.ActiveVersion);
        }

        var revision = await ReadRevisionByIdAsync(connection, transaction, revisionId, cancellationToken)
            ?? throw new FeedConfigNotFoundException();
        if (!revision.Validation.Valid || revision.Validation.ValidatorVersion != FeedConfigContract.ValidatorVersion)
        {
            throw new FeedConfigNotValidatedException();
        }

        await using (var command = new NpgsqlCommand(
            """
            update aevo_feed_config_publications
            set previous_revision_id = active_revision_id,
                active_revision_id = @active_revision_id,
                active_version = @active_version,
                pointer_version = pointer_version + 1,
                updated_by = @updated_by,
                updated_at = now(),
                propagation_status = 'PENDING',
                last_error_code = null
            where scope_key = @scope_key
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("active_revision_id", revision.RevisionId);
            command.Parameters.AddWithValue("active_version", revision.Version);
            command.Parameters.AddWithValue("updated_by", actorId);
            command.Parameters.AddWithValue("scope_key", FeedConfigContract.ScopeKey);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        }

        var after = JsonSerializer.SerializeToElement(
            new { scope = FeedConfigContract.ScopeKey, revisionId, version = revision.Version, action = "PUBLISHED" },
            FeedConfigJsonOptions);
        await InsertFeedConfigOutboxAsync(connection, transaction, "FEED_CONFIG_PUBLISHED", revision, after, cancellationToken);
        await InsertFeedConfigAuditAsync(connection, transaction, actorId, "PUBLISHED", revisionId, publication.ActiveVersion, revision.Version, reason, requestId, idempotencyKey, after, cancellationToken);
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "FEED_CONFIG_PUBLISHED", "feed_config_publication", FeedConfigContract.ScopeKey, reason, JsonSerializer.SerializeToElement(new { version = publication.ActiveVersion }), after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await using var readConnection = await OpenConnectionAsync(cancellationToken);
        return await ReadPublicationAsync(readConnection, null, cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer could not be read after publish.");
    }

    public async Task<FeedConfigPublicationData> RollbackFeedConfigAsync(
        Guid actorId,
        long targetVersion,
        long? expectedActiveVersion,
        string reason,
        string requestId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdempotencyLockAsync(connection, transaction, idempotencyKey, cancellationToken);
        var existing = await ReadIdempotentActionAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Action != "ROLLED_BACK" || existing.Value.Version != targetVersion) throw new FeedConfigIdempotencyConflictException();
            var existingPublication = await ReadPublicationAsync(connection, transaction, cancellationToken);
            if (existingPublication is null) throw new CoreDatabaseException("Feed config rollback idempotency record is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            return existingPublication;
        }

        var publication = await ReadPublicationAsync(connection, transaction, cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        if (expectedActiveVersion.HasValue && expectedActiveVersion.Value != publication.ActiveVersion)
        {
            throw new FeedConfigVersionConflictException(publication.ActiveVersion);
        }

        var target = await ReadRevisionByVersionAsync(connection, transaction, targetVersion, cancellationToken)
            ?? throw new FeedConfigNotFoundException();
        if (!target.Validation.Valid || target.Validation.ValidatorVersion != FeedConfigContract.ValidatorVersion)
        {
            throw new FeedConfigNotValidatedException();
        }
        if (!await IsPublishedRevisionAsync(connection, transaction, target.RevisionId, cancellationToken))
        {
            throw new FeedConfigNotFoundException();
        }
        if (target.Version == publication.ActiveVersion) throw new FeedConfigVersionConflictException(publication.ActiveVersion);

        await using (var command = new NpgsqlCommand(
            """
            update aevo_feed_config_publications
            set previous_revision_id = active_revision_id,
                active_revision_id = @active_revision_id,
                active_version = @active_version,
                pointer_version = pointer_version + 1,
                updated_by = @updated_by,
                updated_at = now(),
                propagation_status = 'PENDING',
                last_error_code = null
            where scope_key = @scope_key
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("active_revision_id", target.RevisionId);
            command.Parameters.AddWithValue("active_version", target.Version);
            command.Parameters.AddWithValue("updated_by", actorId);
            command.Parameters.AddWithValue("scope_key", FeedConfigContract.ScopeKey);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new CoreDatabaseException("Feed config publication pointer is not configured.");
        }

        var after = JsonSerializer.SerializeToElement(
            new { scope = FeedConfigContract.ScopeKey, revisionId = target.RevisionId, version = target.Version, action = "ROLLED_BACK" },
            FeedConfigJsonOptions);
        await InsertFeedConfigOutboxAsync(connection, transaction, "FEED_CONFIG_ROLLED_BACK", target, after, cancellationToken);
        await InsertFeedConfigAuditAsync(connection, transaction, actorId, "ROLLED_BACK", target.RevisionId, publication.ActiveVersion, target.Version, reason, requestId, idempotencyKey, after, cancellationToken);
        await InsertAuditAsync(connection, transaction, actorId, "ADMIN", "FEED_CONFIG_ROLLED_BACK", "feed_config_publication", FeedConfigContract.ScopeKey, reason, JsonSerializer.SerializeToElement(new { version = publication.ActiveVersion }), after, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await using var readConnection = await OpenConnectionAsync(cancellationToken);
        return await ReadPublicationAsync(readConnection, null, cancellationToken)
            ?? throw new CoreDatabaseException("Feed config publication pointer could not be read after rollback.");
    }

    public async Task<FeedConfigPublicationData?> GetFeedConfigPublicationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadPublicationAsync(connection, null, cancellationToken);
    }

    public async Task<IReadOnlyList<FeedConfigPropagationData>> ListFeedConfigPropagationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select runtime_name, observed_revision_id, observed_version, status, last_checked_at, latency_ms, last_error_code
            from aevo_feed_config_propagations
            order by runtime_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FeedConfigPropagationData>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FeedConfigPropagationData(
                reader.GetString(0),
                NullableGuid(reader, 1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                NullableDateTimeOffset(reader, 4),
                NullableInt32(reader, 5),
                NullableString(reader, 6)));
        }
        return result;
    }

    /// <summary>
    /// Compatibility read only. This is intentionally not a write path for the
    /// legacy Go flag store; Core Feed config remains the authority.
    /// </summary>
    public async Task<IReadOnlyList<FeedConfigLegacyFlagObservation>> ReadLegacyFeedFlagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select flag_key, enabled, rollout_percent
            from aevo_go_feature_flags
            where flag_key in ('mixed_feed', 'taste_ranking_v1')
            order by flag_key
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FeedConfigLegacyFlagObservation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FeedConfigLegacyFlagObservation(reader.GetString(0), reader.GetBoolean(1), reader.GetInt32(2)));
        }
        return result;
    }

    private static NpgsqlCommand RevisionCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, string suffix)
    {
        var command = transaction is null
            ? new NpgsqlCommand(
                $"""
                select r.revision_id, r.version, r.schema_version,
                       case when p.active_revision_id = r.revision_id then 'ACTIVE'
                            when coalesce(v.valid, false) then 'VALID' else r.status end,
                       r.config, r.content_hash, r.source_revision_id, r.author_id, r.reason, r.created_at,
                       coalesce(v.valid, false), coalesce(v.validator_version, ''),
                       coalesce(v.errors, '[]'::jsonb), coalesce(v.warnings, '[]'::jsonb), v.validated_at
                from aevo_feed_config_revisions r
                left join aevo_feed_config_publications p on p.scope_key = '{FeedConfigContract.ScopeKey}'
                left join lateral (
                  select valid, validator_version, errors, warnings, validated_at
                  from aevo_feed_config_validation_results
                  where revision_id = r.revision_id
                  order by validated_at desc
                  limit 1
                ) v on true
                {suffix}
                """, connection)
            : new NpgsqlCommand(
                $"""
                select r.revision_id, r.version, r.schema_version,
                       case when p.active_revision_id = r.revision_id then 'ACTIVE'
                            when coalesce(v.valid, false) then 'VALID' else r.status end,
                       r.config, r.content_hash, r.source_revision_id, r.author_id, r.reason, r.created_at,
                       coalesce(v.valid, false), coalesce(v.validator_version, ''),
                       coalesce(v.errors, '[]'::jsonb), coalesce(v.warnings, '[]'::jsonb), v.validated_at
                from aevo_feed_config_revisions r
                left join aevo_feed_config_publications p on p.scope_key = '{FeedConfigContract.ScopeKey}'
                left join lateral (
                  select valid, validator_version, errors, warnings, validated_at
                  from aevo_feed_config_validation_results
                  where revision_id = r.revision_id
                  order by validated_at desc
                  limit 1
                ) v on true
                {suffix}
                """, connection, transaction);
        return command;
    }

    private static async Task<FeedConfigRevisionData?> ReadRevisionByIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid revisionId, CancellationToken cancellationToken)
    {
        await using var command = RevisionCommand(connection, transaction, "where r.revision_id = @revision_id");
        command.Parameters.AddWithValue("revision_id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    private static async Task<FeedConfigRevisionData?> ReadRevisionByVersionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long version, CancellationToken cancellationToken)
    {
        await using var command = RevisionCommand(connection, transaction, "where r.version = @version");
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRevision(reader) : null;
    }

    private static async Task<bool> IsPublishedRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid revisionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from aevo_feed_config_revisions r where r.revision_id = @revision_id and (r.status = 'ACTIVE' or exists (select 1 from aevo_feed_config_audit_events a where a.revision_id = r.revision_id and a.action in ('PUBLISHED', 'ROLLED_BACK'))))",
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static FeedConfigRevisionData ReadRevision(NpgsqlDataReader reader)
    {
        var config = JsonValue(reader, 4) ?? throw new CoreDatabaseException("Feed config revision contains invalid JSON.");
        var errors = JsonValue(reader, 12) ?? JsonDocument.Parse("[]").RootElement.Clone();
        var warnings = JsonValue(reader, 13) ?? JsonDocument.Parse("[]").RootElement.Clone();
        return new FeedConfigRevisionData(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            config,
            reader.GetString(5),
            NullableGuid(reader, 6),
            NullableGuid(reader, 7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            new FeedConfigValidationData(
                reader.GetBoolean(10),
                reader.GetString(11),
                ReadIssues(errors),
                ReadIssues(warnings),
                NullableDateTimeOffset(reader, 14)));
    }

    private static async Task<FeedConfigPublicationData?> ReadPublicationAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = transaction is null
            ? new NpgsqlCommand(
                "select scope_key, active_revision_id, active_version, pointer_version, previous_revision_id, updated_by, updated_at, propagation_status, last_propagated_version, last_propagated_at, last_error_code from aevo_feed_config_publications where scope_key = @scope_key",
                connection)
            : new NpgsqlCommand(
                "select scope_key, active_revision_id, active_version, pointer_version, previous_revision_id, updated_by, updated_at, propagation_status, last_propagated_version, last_propagated_at, last_error_code from aevo_feed_config_publications where scope_key = @scope_key for update",
                connection,
                transaction);
        command.Parameters.AddWithValue("scope_key", FeedConfigContract.ScopeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new FeedConfigPublicationData(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            NullableGuid(reader, 4),
            NullableGuid(reader, 5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            NullableDateTimeOffset(reader, 9),
            NullableString(reader, 10));
    }

    private static async Task<(string Action, Guid? RevisionId, long? Version)?> ReadIdempotentActionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select action, revision_id, after_version from aevo_feed_config_audit_events where app_code = 'ADMIN' and idempotency_key = @idempotency_key",
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), NullableGuid(reader, 1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async Task AcquireIdempotencyLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@idempotency_key, 0))", connection, transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFeedConfigAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        string action,
        Guid? revisionId,
        long? beforeVersion,
        long? afterVersion,
        string reason,
        string requestId,
        string idempotencyKey,
        JsonElement diffMetadata,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into aevo_feed_config_audit_events
              (actor_id, app_code, action, revision_id, before_version, after_version, reason, request_id, idempotency_key, diff_metadata)
            values (@actor_id, 'ADMIN', @action, @revision_id, @before_version, @after_version, @reason, @request_id, @idempotency_key, @diff_metadata)
            """, connection, transaction);
        command.Parameters.AddWithValue("actor_id", actorId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.Add(new NpgsqlParameter("revision_id", NpgsqlDbType.Uuid) { Value = (object?)revisionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before_version", NpgsqlDbType.Bigint) { Value = (object?)beforeVersion ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("after_version", NpgsqlDbType.Bigint) { Value = (object?)afterVersion ?? DBNull.Value });
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("request_id", requestId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.Add(new NpgsqlParameter("diff_metadata", NpgsqlDbType.Jsonb) { Value = diffMetadata.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFeedConfigOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventType,
        FeedConfigRevisionData revision,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "insert into aevo_outbox_events (event_type, aggregate_type, aggregate_id, payload) values (@event_type, 'feed_config', @aggregate_id, @payload)",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("aggregate_id", revision.RevisionId.ToString());
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload.GetRawText() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<FeedConfigValidationIssue> ReadIssues(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<FeedConfigValidationIssue>();
        var result = new List<FeedConfigValidationIssue>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var code = Property(item, "code") ?? Property(item, "Code") ?? "CONFIG_VALIDATION_ERROR";
            var path = Property(item, "path") ?? Property(item, "Path") ?? "$";
            var message = Property(item, "message") ?? Property(item, "Message") ?? "Feed config validation failed.";
            result.Add(new FeedConfigValidationIssue(code, path, message));
        }
        return result;
    }

    private static string? Property(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
