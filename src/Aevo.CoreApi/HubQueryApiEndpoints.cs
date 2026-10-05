using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.QueryPlatform;
using Aevo.CoreApi.Security;
using Npgsql;

namespace Aevo.CoreApi;

public static partial class HubApiEndpoints
{
    private const int MaximumQueryRequestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions QueryRequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16
    };

    public static void MapHubQueryApi(this WebApplication app)
    {
        app.MapGet("/api/v1/query/models", async (
            HttpContext context,
            AppSessionReader sessions,
            CoreDataStore database,
            QueryPlatformService queryService) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (!HasQueryModelPermission(auth.Principal!))
            {
                return QueryFailure(context, StatusCodes.Status403Forbidden, "PERMISSION_REQUIRED", "The current Hub role cannot read this query model.");
            }

            try
            {
                var entitlement = await database.GetQueryPlatformEntitlementAsync(auth.Principal!.OrganizationId, context.RequestAborted);
                if (!entitlement.Enabled)
                {
                    return QueryFailure(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The organization is not entitled to use the query platform.");
                }

                return Results.Ok(new QueryModelsResponse(QueryPlatformContract.QueryVersion, queryService.ListModels()));
            }
            catch (CoreDatabaseException error)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
            catch (NpgsqlException)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "The Core query service is unavailable.");
            }
        });

        app.MapGet("/api/v1/query/models/{technicalName}", async (
            HttpContext context,
            string technicalName,
            AppSessionReader sessions,
            CoreDataStore database,
            QueryPlatformService queryService) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (!HasQueryModelPermission(auth.Principal!))
            {
                return QueryFailure(context, StatusCodes.Status403Forbidden, "PERMISSION_REQUIRED", "The current Hub role cannot read this query model.");
            }

            try
            {
                var entitlement = await database.GetQueryPlatformEntitlementAsync(auth.Principal!.OrganizationId, context.RequestAborted);
                if (!entitlement.Enabled)
                {
                    return QueryFailure(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The organization is not entitled to use the query platform.");
                }

                var model = queryService.FindModel(technicalName);
                return model is null
                    ? QueryFailure(context, StatusCodes.Status404NotFound, "MODEL_NOT_REGISTERED", "The requested model is not registered for query access.", "model")
                    : Results.Ok(new QueryModelResponse(QueryPlatformContract.QueryVersion, model));
            }
            catch (CoreDatabaseException error)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
            catch (NpgsqlException)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "The Core query service is unavailable.");
            }
        });

        app.MapPost("/api/v1/query/execute", async (
            HttpContext context,
            AppSessionReader sessions,
            CoreDataStore database,
            QueryPlatformService queryService) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (!HasQueryModelPermission(auth.Principal!))
            {
                return QueryFailure(context, StatusCodes.Status403Forbidden, "PERMISSION_REQUIRED", "The current Hub role cannot read this query model.");
            }

            try
            {
                var entitlement = await database.GetQueryPlatformEntitlementAsync(auth.Principal!.OrganizationId, context.RequestAborted);
                if (!entitlement.Enabled)
                {
                    return QueryFailure(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The organization is not entitled to use the query platform.");
                }

                var request = await ReadQueryRequestAsync(context);
                var plan = queryService.Compile(request?.Query);
                var effectiveLimit = Math.Min(plan.Limit, entitlement.MaxRows ?? plan.Limit);
                if (effectiveLimit < 1)
                {
                    return QueryFailure(context, StatusCodes.Status429TooManyRequests, "ENTITLEMENT_LIMIT_EXCEEDED", "The organization's query row quota does not allow this request.", "query.pagination.limit");
                }

                var response = await database.ExecuteQueryPlanAsync(
                    auth.Principal!,
                    auth.Session!.StoreId,
                    plan,
                    effectiveLimit,
                    RequestId(context),
                    context.RequestAborted);
                return Results.Ok(response);
            }
            catch (QueryPlatformContractException error)
            {
                return QueryFailure(context, error.StatusCode, error.Code, error.Message, error.Path);
            }
            catch (QueryRequestTooLargeException)
            {
                return QueryFailure(context, StatusCodes.Status413PayloadTooLarge, "QUERY_BODY_TOO_LARGE", "The query request exceeds the 64 KB request limit.");
            }
            catch (JsonException)
            {
                return QueryFailure(context, StatusCodes.Status400BadRequest, "REQUEST_BODY_INVALID", "The query request body is invalid.");
            }
            catch (CoreDatabaseException error)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
            catch (NpgsqlException)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "The Core query service is unavailable.");
            }
        });

        app.MapPost("/api/v1/query/exports", async (
            HttpContext context,
            AppSessionReader sessions,
            CoreDataStore database,
            QueryPlatformService queryService) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (!HasQueryModelPermission(auth.Principal!))
            {
                return QueryFailure(context, StatusCodes.Status403Forbidden, "PERMISSION_REQUIRED", "The current Hub role cannot export this query model.");
            }

            try
            {
                var entitlement = await database.GetQueryPlatformEntitlementAsync(auth.Principal!.OrganizationId, context.RequestAborted);
                if (!entitlement.Enabled)
                {
                    return QueryFailure(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The organization is not entitled to use the query platform.");
                }

                var idempotencyKey = context.Request.Headers["idempotency-key"].ToString().Trim();
                if (idempotencyKey.Length is < 8 or > 200)
                {
                    return QueryFailure(context, StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "A valid Idempotency-Key header between 8 and 200 characters is required.");
                }

                var request = await ReadQueryJsonAsync<QueryExportRequest>(context);
                if (request is null)
                {
                    throw new QueryPlatformContractException(
                        "REQUEST_BODY_INVALID",
                        "The export request body is required.",
                        "body",
                        StatusCodes.Status400BadRequest);
                }

                var plan = queryService.Compile(request.Query);
                var format = request.Format?.Trim().ToUpperInvariant();
                if (format is not ("CSV" or "JSON" or "XLSX"))
                {
                    throw new QueryPlatformContractException(
                        "EXPORT_FORMAT_UNSUPPORTED",
                        "Export format must be CSV, JSON, or XLSX.",
                        "format");
                }

                if (request.SelectedFields is not null
                    && (request.SelectedFields.Count == 0
                        || request.SelectedFields.Any(field => !plan.Fields.Any(selected => string.Equals(selected.Path, field, StringComparison.Ordinal)))))
                {
                    throw new QueryPlatformContractException(
                        "FIELD_NOT_ALLOWED",
                        "Selected export fields must be present in the validated query.",
                        "selectedFields");
                }

                return QueryFailure(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "EXPORT_WORKER_NOT_CONFIGURED",
                    "Core accepted the contract boundary only after validation, but no trusted export worker or private object storage is configured.");
            }
            catch (QueryPlatformContractException error)
            {
                return QueryFailure(context, error.StatusCode, error.Code, error.Message, error.Path);
            }
            catch (QueryRequestTooLargeException)
            {
                return QueryFailure(context, StatusCodes.Status413PayloadTooLarge, "QUERY_BODY_TOO_LARGE", "The export request exceeds the 64 KB request limit.");
            }
            catch (JsonException)
            {
                return QueryFailure(context, StatusCodes.Status400BadRequest, "REQUEST_BODY_INVALID", "The export request body is invalid.");
            }
            catch (CoreDatabaseException error)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
            catch (NpgsqlException)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "The Core query service is unavailable.");
            }
        });

        app.MapGet("/api/v1/query/exports/{jobId}", async (
            HttpContext context,
            string jobId,
            AppSessionReader sessions,
            CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, false, "store.read");
            if (auth.Failure is not null) return auth.Failure;
            if (!HasQueryModelPermission(auth.Principal!))
            {
                return QueryFailure(context, StatusCodes.Status403Forbidden, "PERMISSION_REQUIRED", "The current Hub role cannot read export status.");
            }

            if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128)
            {
                return QueryFailure(context, StatusCodes.Status400BadRequest, "EXPORT_JOB_ID_INVALID", "The export job identifier is invalid.", "jobId");
            }

            return QueryFailure(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "EXPORT_WORKER_NOT_CONFIGURED",
                "Core has no trusted export worker or private object storage configured for export status.");
        });

        app.MapPost("/api/v1/query/imports", async (
            HttpContext context,
            AppSessionReader sessions,
            CoreDataStore database) =>
        {
            var auth = await AuthenticateAsync(context, sessions, database, true, true, "organization.manage");
            if (auth.Failure is not null) return auth.Failure;

            try
            {
                var entitlement = await database.GetQueryPlatformEntitlementAsync(auth.Principal!.OrganizationId, context.RequestAborted);
                if (!entitlement.Enabled)
                {
                    return QueryFailure(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The organization is not entitled to use the query platform.");
                }

                _ = await ReadQueryJsonAsync<QueryImportRequest>(context);
                return QueryFailure(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "IMPORT_WORKER_NOT_CONFIGURED",
                    "Core has no private upload storage, schema validator, or trusted import worker configured.");
            }
            catch (QueryRequestTooLargeException)
            {
                return QueryFailure(context, StatusCodes.Status413PayloadTooLarge, "QUERY_BODY_TOO_LARGE", "The import request exceeds the 64 KB request limit.");
            }
            catch (JsonException)
            {
                return QueryFailure(context, StatusCodes.Status400BadRequest, "REQUEST_BODY_INVALID", "The import request body is invalid.");
            }
            catch (CoreDatabaseException error)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
            }
            catch (NpgsqlException)
            {
                return QueryFailure(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "The Core query service is unavailable.");
            }
        });

    }

    private static bool HasQueryModelPermission(HubPrincipalRecord principal) =>
        principal.Permissions.Contains("store.read", StringComparer.Ordinal);

    private static Task<QueryExecutionRequest?> ReadQueryRequestAsync(HttpContext context) =>
        ReadQueryJsonAsync<QueryExecutionRequest>(context);

    private static async Task<T?> ReadQueryJsonAsync<T>(HttpContext context)
        where T : class
    {
        var contentType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new QueryPlatformContractException(
                "CONTENT_TYPE_UNSUPPORTED",
                "The query request must use application/json.",
                "contentType",
                StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength is > MaximumQueryRequestBytes)
        {
            throw new QueryRequestTooLargeException();
        }

        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted);
                if (read == 0) break;
                if (body.Length + read > MaximumQueryRequestBytes)
                {
                    throw new QueryRequestTooLargeException();
                }

                await body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return JsonSerializer.Deserialize<T>(body.ToArray(), QueryRequestJsonOptions);
    }

    private static IResult QueryFailure(HttpContext context, int status, string code, string message, string? path = null)
    {
        var details = path is null ? null : new { path };
        return Results.Json(new
        {
            error = new
            {
                code,
                message,
                requestId = RequestId(context),
                details
            }
        }, statusCode: status);
    }

    private sealed class QueryRequestTooLargeException : Exception { }
}
