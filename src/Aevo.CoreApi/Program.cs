using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aevo.CoreApi;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.Feed;
using Aevo.CoreApi.Runtime;
using Aevo.CoreApi.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache(options => options.SizeLimit = 256);
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<AppSessionReader>();
builder.Services.AddSingleton<IAevoMemoryCache, AevoMemoryCache>();
builder.Services.AddSingleton<CoreDataStore>();
builder.Services.AddHostedService<CoreDatabaseWarmupService>();
builder.Services.AddSingleton<FeedConfigService>();
builder.Services.AddSingleton<FeedCursorSigner>();
builder.Services.AddSingleton<FeedSessionContextFactory>();
builder.Services.AddSingleton<AuditCursorSigner>();
builder.Services.AddSingleton<FeedRateLimiter>();
builder.Services.AddSingleton<FeedFacadeService>();
builder.Services.AddSingleton<FeedEventService>();
builder.Services.AddSingleton<FeedNegativeFeedbackService>();
builder.Services.AddSingleton<FeedSavedPlaceService>();
builder.Services.AddSingleton<FeedSavedPlaceReconciliationService>();
builder.Services.AddSingleton<FeedCanonicalDataStore>();
builder.Services.AddSingleton<PlaceCursorSigner>();
builder.Services.AddSingleton<PlacePublicReadService>();
builder.Services.AddSingleton<TraceFeedCandidateGenerator>();
builder.Services.AddSingleton<PlaceFeedCandidateGenerator>();
builder.Services.AddSingleton<IFeedCandidateGenerator>(services => services.GetRequiredService<TraceFeedCandidateGenerator>());
builder.Services.AddSingleton<IFeedCandidateGenerator>(services => services.GetRequiredService<PlaceFeedCandidateGenerator>());
builder.Services.AddSingleton<IFeedDataPort, CanonicalFeedDataPort>();

var entitlementConfiguration = EntitlementEvaluator.ValidateConfiguration(
    builder.Configuration["AEVO_ENVIRONMENT"],
    builder.Configuration["AEVO_ALLOW_UNLIMITED_TESTING"]);
if (!entitlementConfiguration.Valid)
{
    throw new InvalidOperationException("Development/unlimited testing cannot be enabled in a production-like Core API environment.");
}

builder.Services.AddHttpClient();

var strictFeedEventJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict
};

var app = builder.Build();

app.UseExceptionHandler();
#pragma warning disable CA1848
app.Use(async (context, next) =>
{
    var requestId = context.Request.Headers.TryGetValue("x-request-id", out var incomingRequestId)
        && !string.IsNullOrWhiteSpace(incomingRequestId)
        ? incomingRequestId.ToString()[..Math.Min(incomingRequestId.ToString().Length, 128)]
        : Guid.NewGuid().ToString("N");

    context.Items["aevo.request_id"] = requestId;
    context.Response.Headers["x-request-id"] = requestId;
    context.Response.Headers["x-aevo-contract-version"] = HubApiContract.Version;
    ApplySecurityHeaders(context);
    var performance = RequestPerformance.Attach(context, requestId);
    context.Response.OnStarting(() =>
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
        var responseHeadersEnabled = string.Equals(configuration["AEVO_PERF_RESPONSE_HEADERS"], "true", StringComparison.OrdinalIgnoreCase)
            || environment is "development" or "test" or "local";
        if (responseHeadersEnabled)
        {
            var snapshot = performance.Snapshot();
            context.Response.Headers["x-aevo-perf-cache"] = snapshot.CacheStatus;
            context.Response.Headers["x-aevo-perf-db-wall-ms"] = snapshot.DatabaseWallMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-aevo-perf-db-aggregate-ms"] = snapshot.DatabaseAggregateMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-aevo-perf-db-open-ms"] = snapshot.DatabaseConnectionOpenMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-aevo-perf-total-ms"] = snapshot.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-aevo-perf-db-query-count"] = snapshot.DatabaseQueryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.Headers["x-aevo-perf-db-ops"] = snapshot.DatabaseOperationBreakdown;
        }
        return Task.CompletedTask;
    });

    try
    {
        if (RequiresGatewaySignature(context))
        {
            var gatewayFailure = await ValidateGatewaySignatureAsync(context);
            if (gatewayFailure is not null)
            {
                await gatewayFailure.ExecuteAsync(context);
                return;
            }
        }

        await next();
    }
    finally
    {
        var snapshot = performance.Snapshot();
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var warnThreshold = PerformanceThreshold(configuration["AEVO_PERF_WARN_MS"], 250);
        var criticalThreshold = Math.Max(warnThreshold, PerformanceThreshold(configuration["AEVO_PERF_CRITICAL_MS"], 500));
        var logAll = string.Equals(configuration["AEVO_PERF_LOG_ALL"], "true", StringComparison.OrdinalIgnoreCase);
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Aevo.CoreApi.Performance");
        if (snapshot.TotalMilliseconds >= criticalThreshold)
        {
            CoreApiLog.PerformanceCritical(
                logger,
                requestId, context.Request.Method, context.Request.Path, context.Response.StatusCode,
                snapshot.TotalMilliseconds, snapshot.AuthenticationMilliseconds, snapshot.AuthorizationMilliseconds,
                snapshot.DatabaseWallMilliseconds, snapshot.DatabaseAggregateMilliseconds, snapshot.ExternalApiMilliseconds, snapshot.SerializationMilliseconds,
                snapshot.CacheLookupMilliseconds, snapshot.DatabaseQueryCount, snapshot.CacheStatus, snapshot.DatabaseOperationBreakdown);
        }
        else if (snapshot.TotalMilliseconds >= warnThreshold || logAll)
        {
            CoreApiLog.PerformanceWarning(
                logger,
                requestId, context.Request.Method, context.Request.Path, context.Response.StatusCode,
                snapshot.TotalMilliseconds, snapshot.AuthenticationMilliseconds, snapshot.AuthorizationMilliseconds,
                snapshot.DatabaseWallMilliseconds, snapshot.DatabaseAggregateMilliseconds, snapshot.ExternalApiMilliseconds, snapshot.SerializationMilliseconds,
                snapshot.CacheLookupMilliseconds, snapshot.DatabaseQueryCount, snapshot.CacheStatus, snapshot.DatabaseOperationBreakdown, snapshot.TotalMilliseconds >= warnThreshold ? "warn" : "normal");
        }
        RequestPerformance.Detach(performance);
    }
});
#pragma warning restore CA1848

app.MapGet("/health/live", (HttpContext context) =>
    Results.Ok(Health(context, "healthy")));

app.MapGet("/health", (HttpContext context) =>
    Results.Ok(Health(context, "healthy")));

app.MapGet("/ready", async (HttpContext context, ReadinessState readiness, CoreDataStore database) =>
{
    var result = readiness.Evaluate();
    if (!result.Ready)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "Core API dependencies are not configured.", result.MissingConfiguration);
    }

    if (!database.IsConfigured)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_NOT_CONFIGURED", "The Core API database connection is not configured.");
    }

    try
    {
        return await database.CanConnectAsync(context.RequestAborted)
            ? Results.Ok(Health(context, "ready"))
            : Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", "Core API could not reach Cloud SQL.");
    }
    catch (CoreDatabaseException dbError)
    {
        return DatabaseError(context, dbError);
    }
});

app.MapGet("/v1/me", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authentication = await RequireSessionAsync(context, sessions, database, null);
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    return Results.Ok(new MeResponse(
        new AuthenticatedUser(session.UserId.ToString(), session.Email, session.DisplayName),
        new ApplicationSessionSummary(
            session.AppCode,
            session.Id.ToString(),
            session.ExpiresAt,
            session.OrganizationId?.ToString(),
            session.StoreId?.ToString())));
});

app.MapGet("/v1/access", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var application = context.Request.Query["application"].ToString().Trim().ToUpperInvariant();
    CoreSession session;
    if (string.IsNullOrWhiteSpace(application))
    {
        var genericAuthentication = await RequireSessionAsync(context, sessions, database, null);
        if (genericAuthentication.Failure is not null) return genericAuthentication.Failure;
        session = genericAuthentication.Session!;
        application = session.AppCode;
    }
    else
    {
        if (!ApplicationCodes.All.Contains(application))
        {
            return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Unknown application code.");
        }
        var applicationAuthentication = await RequireSessionAsync(context, sessions, database, application);
        if (applicationAuthentication.Failure is not null) return applicationAuthentication.Failure;
        session = applicationAuthentication.Session!;
    }
    try
    {
        using var authorizationTiming = RequestPerformance.Measure(context, "authorization");
        var hubPrincipal = application == "HUB"
            ? await database.ResolveHubPrincipalAsync(session, context.RequestAborted)
            : null;
        var permissions = application == "ADMIN"
            ? PlatformPermissions(session.PlatformRole)
            : hubPrincipal?.Permissions ?? await database.ResolvePermissionsAsync(session.UserId, application, session.OrganizationId, session.StoreId, context.RequestAborted);
        var allowed = application == "ADMIN"
            ? session.PlatformRole is not null && permissions.Count > 0
            : application == "HUB" ? hubPrincipal is not null : permissions.Count > 0;
        return Results.Ok(new
        {
            allowed,
            application,
            appCode = application,
            reason = allowed ? "ALLOWED" : "PERMISSION_REQUIRED",
            userId = session.UserId,
            organizationId = hubPrincipal?.OrganizationId ?? session.OrganizationId,
            storeId = session.StoreId,
            permissions,
            platformRole = application == "ADMIN" ? session.PlatformRole : null,
            platformPermissions = application == "ADMIN" ? permissions : Array.Empty<string>(),
            checkedAt = DateTimeOffset.UtcNow
        });
    }
    catch (CoreDatabaseException dbError)
    {
        return DatabaseError(context, dbError);
    }
});

app.MapGet("/api/v1/public/feed", async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedFacadeService feedFacade) =>
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);

    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_REQUEST", "Feed actor, tenant, source, ranking, and config are resolved by Core.");
    }

    var normalized = FeedRequestNormalizer.Normalize(context.Request.Query);
    if (!normalized.IsValid)
    {
        return Error(context, StatusCodes.Status400BadRequest, normalized.ErrorCode!, normalized.ErrorMessage!);
    }

    var principalResolution = await ResolveFeedPrincipalAsync(context, sessions, database);
    if (principalResolution.Failure is not null) return principalResolution.Failure;
    var principal = principalResolution.Principal!;

    var result = await feedFacade.GetPageAsync(
        principal,
        normalized.Request!,
        RequestId(context),
        DateTimeOffset.UtcNow,
        context.RequestAborted);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, result.RateLimit);
    if (result.ErrorStatus is not null)
    {
        return Error(context, result.ErrorStatus.Value, result.ErrorCode!, result.ErrorMessage!, result.ErrorDetails);
    }

    return Results.Ok(result.Response!);
});

app.MapGet(FeedApiContract.FeedbackHistoryRoute, async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedNegativeFeedbackService feedbackService) =>
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEEDBACK_HISTORY", "Feed feedback history actor, tenant, and session are resolved by Core.");
    }

    var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    var principal = FeedPrincipalFactory.FromGoSession(session);
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The Feed feedback history rate limit has been reached.");
    }

    var limitText = context.Request.Query["limit"].ToString().Trim();
    var limit = FeedApiContract.DefaultFeedbackHistoryLimit;
    if (!string.IsNullOrWhiteSpace(limitText)
        && (!int.TryParse(limitText, out limit)
            || limit is < 1 or > FeedApiContract.MaxFeedbackHistoryLimit))
    {
        return Error(context, StatusCodes.Status400BadRequest, "FEEDBACK_HISTORY_LIMIT_INVALID", "The Feed feedback history limit is invalid.");
    }

    try
    {
        var result = await feedbackService.ListHistoryAsync(
            principal,
            limit,
            RequestId(context),
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (FeedNegativeFeedbackRequestException error)
    {
        return Error(context, error.StatusCode, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost(FeedApiContract.EventRoute, async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedEventService feedEvents) =>
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_EVENT", "Feed event actor, tenant, source, and session are resolved by Core.");
    }

    if (context.Request.ContentLength is > FeedApiContract.MaxEventBodyBytes)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "EVENT_BATCH_TOO_LARGE", "The Feed event batch is too large.");
    }

    var principalResolution = await ResolveFeedPrincipalAsync(context, sessions, database);
    if (principalResolution.Failure is not null) return principalResolution.Failure;
    var principal = principalResolution.Principal!;
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The Feed event rate limit has been reached.");
    }

    var (bodyBytes, bodyTooLarge) = await ReadRequestBodyAtMostAsync(
        context.Request,
        FeedApiContract.MaxEventBodyBytes,
        context.RequestAborted);
    if (bodyTooLarge)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "EVENT_BATCH_TOO_LARGE", "The Feed event batch is too large.");
    }

    FeedEventBatchContract? batch;
    try
    {
        batch = JsonSerializer.Deserialize<FeedEventBatchContract>(bodyBytes, strictFeedEventJsonOptions);
    }
    catch (JsonException)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_EVENT", "The Feed event batch contains an unknown or invalid field.");
    }

    if (batch?.Events is null)
    {
        return Error(context, StatusCodes.Status400BadRequest, "EVENT_BATCH_INVALID", "The Feed event batch must contain an events array.");
    }

    try
    {
        var result = await feedEvents.AcceptBatchAsync(
            principal,
            batch,
            RequestId(context),
            DateTimeOffset.UtcNow,
            context.RequestAborted);
        return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
    }
    catch (FeedEventRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost(FeedApiContract.FeedbackRoute, async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedNegativeFeedbackService feedbackService) =>
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_FEEDBACK", "Feed feedback actor, tenant, source, and session are resolved by Core.");
    }

    if (context.Request.ContentLength is > FeedApiContract.MaxFeedbackBodyBytes)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "FEEDBACK_BODY_TOO_LARGE", "The Feed feedback body is too large.");
    }

    var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    var csrfFailure = RequireCsrf(context, session);
    if (csrfFailure is not null) return csrfFailure;

    var idempotencyKey = context.Request.Headers["idempotency-key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Error(context, StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "A Feed feedback idempotency key is required.");
    }

    var principal = FeedPrincipalFactory.FromGoSession(session);
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The Feed feedback rate limit has been reached.");
    }

    var (bodyBytes, bodyTooLarge) = await ReadRequestBodyAtMostAsync(
        context.Request,
        FeedApiContract.MaxFeedbackBodyBytes,
        context.RequestAborted);
    if (bodyTooLarge)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "FEEDBACK_BODY_TOO_LARGE", "The Feed feedback body is too large.");
    }

    FeedNegativeFeedbackRequestContract? body;
    try
    {
        body = JsonSerializer.Deserialize<FeedNegativeFeedbackRequestContract>(bodyBytes, strictFeedEventJsonOptions);
    }
    catch (JsonException)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_FEEDBACK", "The Feed feedback body contains an unknown or invalid field.");
    }

    if (body is null)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_FEEDBACK", "The Feed feedback body is required.");
    }

    try
    {
        var result = await feedbackService.ApplyAsync(
            principal,
            body,
            idempotencyKey,
            RequestId(context),
            DateTimeOffset.UtcNow,
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (FeedNegativeFeedbackIdempotencyConflictException)
    {
        return Error(context, StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT", "The Feed feedback idempotency key was already used for a different request.");
    }
    catch (FeedNegativeFeedbackRequestException error)
    {
        return Error(context, error.StatusCode, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet(PlaceApiContract.SavedPlacesRoute, async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces) =>
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_SAVE_LIST", "Saved Place actor, tenant, and session are resolved by Core.");
    }

    var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
    if (authentication.Failure is not null) return authentication.Failure;
    var principal = FeedPrincipalFactory.FromGoSession(authentication.Session!);
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The saved Place rate limit has been reached.");
    }

    try
    {
        return Results.Ok(await savedPlaces.ListAsync(principal, RequestId(context), context.RequestAborted));
    }
    catch (FeedSavedPlaceRequestException error)
    {
        return Error(context, error.StatusCode, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/public/places/{placeId:guid}/save", (
    HttpContext context,
    Guid placeId,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces) =>
    HandleFeedSavedPlaceMutationAsync(context, placeId, true, sessions, database, rateLimiter, savedPlaces));

app.MapDelete("/api/v1/public/places/{placeId:guid}/save", (
    HttpContext context,
    Guid placeId,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces) =>
    HandleFeedSavedPlaceMutationAsync(context, placeId, false, sessions, database, rateLimiter, savedPlaces));

app.MapGet("/api/v1/public/places/map", async (
    HttpContext context,
    PlacePublicReadService places,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces) =>
{
    if (!PlaceRequestParser.TryMap(context.Request.Query, out var request, out var error))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", error);
    }
    try
    {
        var savedFilter = await ResolvePlaceSavedFilterAsync(
            context,
            request.SavedOnly,
            sessions,
            database,
            rateLimiter,
            savedPlaces);
        if (savedFilter.Failure is not null) return savedFilter.Failure;

        var response = await places.GetMapOverlayAsync(
            request,
            RequestId(context),
            context.RequestAborted,
            savedFilter.PlaceIds);
        if (!request.SavedOnly)
        {
            context.Response.Headers.CacheControl = "public, max-age=15, stale-while-revalidate=30";
        }
        return Results.Ok(response);
    }
    catch (FeedSavedPlaceRequestException savedError)
    {
        return Error(context, savedError.StatusCode, savedError.Code, savedError.Message);
    }
    catch (CoreDatabaseException dbError)
    {
        return DatabaseError(context, dbError);
    }
    catch (PlaceProjectionUnavailableException)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "PLACE_PROJECTION_UNAVAILABLE", "The public Place projection is not available.");
    }
    catch (InvalidPlaceCursorException cursorError)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_CURSOR", cursorError.Message);
    }
});

app.MapGet("/api/v1/public/places/search", async (
    HttpContext context,
    PlacePublicReadService places,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces) =>
{
    if (!PlaceRequestParser.TrySearch(context.Request.Query, out var request, out var error))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", error);
    }
    try
    {
        var savedFilter = await ResolvePlaceSavedFilterAsync(
            context,
            request.SavedOnly,
            sessions,
            database,
            rateLimiter,
            savedPlaces);
        if (savedFilter.Failure is not null) return savedFilter.Failure;

        var response = await places.SearchAsync(
            request,
            RequestId(context),
            context.RequestAborted,
            savedFilter.PlaceIds,
            savedFilter.PrincipalBinding);
        if (!request.SavedOnly)
        {
            context.Response.Headers.CacheControl = "public, max-age=10, stale-while-revalidate=20";
        }
        return Results.Ok(response);
    }
    catch (FeedSavedPlaceRequestException savedError)
    {
        return Error(context, savedError.StatusCode, savedError.Code, savedError.Message);
    }
    catch (CoreDatabaseException dbError)
    {
        return DatabaseError(context, dbError);
    }
    catch (PlaceProjectionUnavailableException)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "PLACE_PROJECTION_UNAVAILABLE", "The public Place projection is not available.");
    }
    catch (InvalidPlaceCursorException cursorError)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_CURSOR", cursorError.Message);
    }
});

app.MapGet("/api/v1/public/places/nearby", async (HttpContext context, PlacePublicReadService places) =>
{
    if (!PlaceRequestParser.TryNearby(context.Request.Query, out var request, out var error))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", error);
    }
    try
    {
        var response = await places.NearbyAsync(request, RequestId(context), context.RequestAborted);
        context.Response.Headers.CacheControl = "public, max-age=10, stale-while-revalidate=20";
        return Results.Ok(response);
    }
    catch (PlaceProjectionUnavailableException)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "PLACE_PROJECTION_UNAVAILABLE", "The public Place projection is not available.");
    }
    catch (InvalidPlaceCursorException cursorError)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_CURSOR", cursorError.Message);
    }
});

app.MapGet("/api/v1/public/places/{placeId}", async (HttpContext context, string placeId, PlacePublicReadService places) =>
{
    if (!PlaceRequestParser.TryPlaceId(placeId, out var parsedPlaceId, out var error))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", error);
    }
    try
    {
        var response = await places.GetDetailAsync(parsedPlaceId, RequestId(context), context.RequestAborted);
        context.Response.Headers.CacheControl = "public, max-age=30, stale-while-revalidate=60";
        return Results.Ok(response);
    }
    catch (KeyNotFoundException)
    {
        return Error(context, StatusCodes.Status404NotFound, "PLACE_NOT_FOUND", "The Place was not found.");
    }
    catch (PlaceProjectionUnavailableException)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "PLACE_PROJECTION_UNAVAILABLE", "The public Place projection is not available.");
    }
    catch (PlaceRedirectResolutionException redirectError)
    {
        return Error(context, StatusCodes.Status409Conflict, "PLACE_REDIRECT_INVALID", redirectError.Message);
    }
});

app.MapGet("/api/v1/admin/map/places", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var rawLimit = context.Request.Query["limit"].ToString().Trim();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var requestedLimit) || requestedLimit is < 1 or > 50))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", "limit must be between 1 and 50.");
    }
    var rawPage = context.Request.Query["page"].ToString().Trim();
    if (!string.IsNullOrWhiteSpace(rawPage)
        && (!int.TryParse(rawPage, out var requestedPage) || requestedPage is < 1 or > 100_000))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", "page must be between 1 and 100000.");
    }
    var limit = string.IsNullOrWhiteSpace(rawLimit) ? 50 : int.Parse(rawLimit);
    var page = string.IsNullOrWhiteSpace(rawPage) ? 1 : int.Parse(rawPage);

    var query = context.Request.Query["q"].ToString().Trim();
    if (query.Length > 200)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", "q cannot exceed 200 characters.");
    }

    var status = context.Request.Query["status"].ToString().Trim().ToLowerInvariant();
    if (status.Length > 0 && status is not ("candidate" or "visible" or "limited" or "under_review" or "closed" or "removed" or "merged"))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", "status is not a valid Place lifecycle state.");
    }

    var sort = context.Request.Query["sort"].ToString();
    var direction = context.Request.Query["direction"].ToString();

    try
    {
        var directory = await database.ListAdminPlaceRegistryPageAsync(
            limit,
            (page - 1) * limit,
            string.IsNullOrWhiteSpace(query) ? null : query,
            string.IsNullOrWhiteSpace(status) ? null : status,
            sort,
            direction,
            context.RequestAborted);
        return Results.Ok(new
        {
            success = true,
            places = directory.Items,
            page,
            limit,
            hasMore = directory.HasMore,
            readOnly = true,
            projectionBoundary = "aevo_place_public_projections"
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/map/places/{placeId:guid}", async (
    HttpContext context,
    Guid placeId,
    AppSessionReader sessions,
    CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;

    try
    {
        var result = await database.ReadAdminPlaceAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            placeId,
            context.RequestAborted);
        return result is null
            ? Error(context, StatusCodes.Status404NotFound, "PLACE_NOT_FOUND", "The Place does not exist.")
            : Results.Ok(result);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/map/places", async (
    HttpContext context,
    [FromBody] PlaceAdminPlaceMutationRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    if (body.ExpectedRevision is not null)
    {
        return Error(context, StatusCodes.Status400BadRequest, "PLACE_CREATE_REVISION_INVALID", "Create must not include an expected revision; use PUT to edit an existing Place.");
    }
    try
    {
        var result = await database.UpsertAdminPlaceAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            RequestId(context),
            body,
            context.RequestAborted);
        return Results.Json(result, statusCode: StatusCodes.Status201Created);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceMutationConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "PLACE_REVISION_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPut("/api/v1/admin/map/places/{placeId:guid}", async (
    HttpContext context,
    Guid placeId,
    [FromBody] PlaceAdminPlaceMutationRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    if (!Guid.TryParse(body.Summary.Id, out var bodyPlaceId) || bodyPlaceId != placeId)
    {
        return Error(context, StatusCodes.Status400BadRequest, "PLACE_ID_MISMATCH", "The path and summary Place IDs must match.");
    }
    if (body.ExpectedRevision is null)
    {
        return Error(context, StatusCodes.Status400BadRequest, "PLACE_REVISION_REQUIRED", "Edit requires the current Place revision.");
    }

    try
    {
        var result = await database.UpsertAdminPlaceAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            RequestId(context),
            body,
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceMutationConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "PLACE_REVISION_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapDelete("/api/v1/admin/map/places/{placeId:guid}", async (
    HttpContext context,
    Guid placeId,
    [FromBody] PlaceAdminDeleteRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;

    try
    {
        var result = await database.SoftDeleteAdminPlaceAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            RequestId(context),
            placeId,
            body,
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceMutationNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "PLACE_NOT_FOUND", error.Message);
    }
    catch (PlaceMutationConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "PLACE_REVISION_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapDelete("/api/v1/admin/map/legacy-mappings", async (
    HttpContext context,
    [FromBody] PlaceAdminLegacyMappingDeleteRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database,
    IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var purgeFailure = RequireLegacyHardDelete(context, configuration);
    if (purgeFailure is not null) return purgeFailure;

    try
    {
        var result = await database.DeleteAdminLegacyMappingAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            RequestId(context),
            body,
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceMutationNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "LEGACY_MAPPING_NOT_FOUND", error.Message);
    }
    catch (PlaceMutationConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "LEGACY_MAPPING_LINKED", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapDelete("/api/v1/admin/map/source-links/{sourceLinkId:guid}", async (
    HttpContext context,
    Guid sourceLinkId,
    [FromBody] PlaceAdminSourceLinkDeleteRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database,
    IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var purgeFailure = RequireLegacyHardDelete(context, configuration);
    if (purgeFailure is not null) return purgeFailure;

    try
    {
        var result = await database.DeleteAdminSourceLinkAsync(
            authorization.Session!.UserId,
            authorization.Session.PlatformRole,
            RequestId(context),
            sourceLinkId,
            body,
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceMutationNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "LEGACY_SOURCE_LINK_NOT_FOUND", error.Message);
    }
    catch (PlaceMutationConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "LEGACY_SOURCE_LINKED", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/place/projections/replay", async (
    HttpContext context,
    [FromBody] PlaceProjectionReplayRequestContract body,
    CoreDataStore database,
    IConfiguration configuration) =>
{
    if (!PlaceProjectionReplayWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Place projection replay authentication failed.");
    }

    var replayGuard = RequirePlaceProjectionReplay(context, configuration);
    if (replayGuard is not null) return replayGuard;
    if (context.Request.ContentLength is > 1_048_576)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "REQUEST_BODY_TOO_LARGE", "A Place projection replay batch is limited to 1 MiB.");
    }

    try
    {
        var result = await database.ApplyPlaceProjectionReplayAsync(
            RequestId(context),
            body,
            context.RequestAborted);
        return result.Success
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (PlaceMutationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (PlaceProjectionReplayAuthorizationException error)
    {
        return Error(context, StatusCodes.Status403Forbidden, "PLACE_REPLAY_ACTOR_INVALID", error.Message);
    }
    catch (PlaceProjectionReplayConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "PLACE_PROJECTION_REPLAY_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/map/workflows", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "map.places.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var rawLimit = context.Request.Query["limit"].ToString().Trim();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var requestedLimit) || requestedLimit is < 1 or > 50))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_REQUEST", "limit must be between 1 and 50.");
    }
    var rawPage = context.Request.Query["page"].ToString().Trim();
    if (!string.IsNullOrWhiteSpace(rawPage)
        && (!int.TryParse(rawPage, out var requestedPage) || requestedPage is < 1 or > 100_000))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_WORKFLOW_REQUEST", "page must be between 1 and 100000.");
    }
    var limit = string.IsNullOrWhiteSpace(rawLimit) ? 50 : int.Parse(rawLimit);
    var page = string.IsNullOrWhiteSpace(rawPage) ? 1 : int.Parse(rawPage);

    var kind = context.Request.Query["kind"].ToString().Trim().ToLowerInvariant();
    if (kind.Length > 0 && kind is not ("claim" or "submission" or "relationship"))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_WORKFLOW_REQUEST", "kind is not a supported Place workflow aggregate.");
    }

    var status = context.Request.Query["status"].ToString().Trim().ToLowerInvariant();
    if (status.Length > 0
        && status is not ("pending" or "proposed" or "active" or "revoked" or "approved" or "rejected" or "needs_info" or "withdrawn" or "applied" or "expired"))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_WORKFLOW_REQUEST", "status is not a supported Place workflow state.");
    }

    var query = context.Request.Query["q"].ToString().Trim();
    if (query.Length > 200)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_WORKFLOW_REQUEST", "q cannot exceed 200 characters.");
    }
    var sort = context.Request.Query["sort"].ToString();
    var direction = context.Request.Query["direction"].ToString();

    try
    {
        var directory = await database.ListAdminPlaceWorkflowsPageAsync(
            limit,
            (page - 1) * limit,
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            string.IsNullOrWhiteSpace(status) ? null : status,
            string.IsNullOrWhiteSpace(query) ? null : query,
            sort,
            direction,
            context.RequestAborted);
        return Results.Ok(new
        {
            success = true,
            workflows = directory.Items,
            page,
            limit,
            hasMore = directory.HasMore,
            readOnly = true,
            reviewActionsEnabled = false,
            requestId = RequestId(context)
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/v1/admin/connections", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.health");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var records = await database.ListConnectionsAsync(configuration["AEVO_ENVIRONMENT"] ?? "local", context.RequestAborted);
        var items = records.Select(record => new ConnectionSummary(
            record.AppCode,
            record.Label,
            record.Status,
            record.BaseUrl,
            record.CheckedAt,
            record.LatencyMs,
            record.LastErrorCode,
            record.Metadata)).ToArray();
        return Results.Ok(new PageResponse<ConnectionSummary>(items, null));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/v1/admin/audit-logs", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "audit.read");
    if (authorization.Failure is not null) return authorization.Failure;
    var application = context.Request.Query["application"].ToString().Trim().ToUpperInvariant();
    var applicationFilter = string.IsNullOrWhiteSpace(application) ? null : application;
    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 50) : 50;
    try
    {
        var records = await database.ListAuditLogsAsync(limit, applicationFilter, context.RequestAborted);
        var items = records.Select(ToAuditContract).ToArray();
        return Results.Ok(new PageResponse<AuditLogRecord>(items, null));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

// Shared authentication routes are the versioned app-session boundary used by
// the Hub and Admin shells. They return real Core data and fail closed with an
// explicit 401/503 when the session projection is unavailable.
app.MapGet("/api/auth/me", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var application = RequestedApplication(context, null, "ADMIN");
    var includeHubStores = application == "HUB"
        && string.Equals(context.Request.Query["includeStores"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
    CoreSession session;
    HubPrincipalRecord? hubPrincipal = null;
    IReadOnlyList<object>? hubStores = null;

    if (includeHubStores)
    {
        var bootstrap = await RequireHubBootstrapSessionAsync(context, sessions, database);
        if (bootstrap.Failure is not null) return bootstrap.Failure;
        session = bootstrap.Session!;
        hubPrincipal = bootstrap.Principal;
        hubStores = bootstrap.Stores
            .Select(store => (object)new
            {
                store.Id,
                store.OrganizationId,
                store.Code,
                store.Name,
                store.Timezone,
                store.Currency,
                store.StoreMode,
                store.Address,
                store.Phone,
                store.TaxId,
                store.Status
            })
            .ToArray();
    }
    else
    {
        var authentication = await RequireSessionAsync(context, sessions, database, application);
        if (authentication.Failure is not null) return authentication.Failure;
        session = authentication.Session!;
        using (RequestPerformance.Measure(context, "authorization"))
        {
            if (application == "HUB")
            {
                hubPrincipal = await database.ResolveHubPrincipalAsync(session, context.RequestAborted);
            }
        }
    }
    object? access = application == "HUB"
        ? new
        {
            allowed = hubPrincipal is not null,
            application,
            appCode = application,
            reason = hubPrincipal is not null ? "ALLOWED" : "MEMBERSHIP_REQUIRED",
            userId = session.UserId,
            organizationId = hubPrincipal?.OrganizationId ?? session.OrganizationId,
            storeId = session.StoreId,
            permissions = hubPrincipal?.Permissions ?? Array.Empty<string>(),
            platformRole = (string?)null,
            platformPermissions = Array.Empty<string>(),
            checkedAt = DateTimeOffset.UtcNow
        }
        : application == "ADMIN"
            ? new
            {
                allowed = session.PlatformRole is not null && PlatformPermissions(session.PlatformRole).Count > 0,
                application,
                appCode = application,
                reason = session.PlatformRole is not null && PlatformPermissions(session.PlatformRole).Count > 0 ? "ALLOWED" : "PLATFORM_PERMISSION_REQUIRED",
                userId = session.UserId,
                organizationId = session.OrganizationId,
                storeId = session.StoreId,
                permissions = Array.Empty<string>(),
                platformRole = session.PlatformRole,
                platformPermissions = PlatformPermissions(session.PlatformRole),
                checkedAt = DateTimeOffset.UtcNow
            }
            : null;
    return Results.Ok(new
    {
        user = new { id = session.UserId.ToString(), email = session.Email, displayName = session.DisplayName },
        session = new { rememberMe = session.RememberMe },
        principal = hubPrincipal,
        access,
        stores = includeHubStores ? hubStores : null,
        effectiveUser = (object?)null,
        impersonation = (object?)null
    });
});

app.MapGet("/api/v1/sync/manifest", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var application = RequestedApplication(context, null, "ADMIN");
    var authentication = await RequireSessionAsync(context, sessions, database, application);
    if (authentication.Failure is not null) return authentication.Failure;

    try
    {
        var manifest = await database.GetSyncManifestAsync(authentication.Session!, application, context.RequestAborted);
        context.Response.Headers.ETag = manifest.ETag;
        context.Response.Headers.CacheControl = "private, no-cache";
        var requestedTag = context.Request.Headers.IfNoneMatch.ToString().Trim();
        if (string.Equals(requestedTag, manifest.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Ok(new
        {
            etag = manifest.ETag,
            generatedAt = manifest.GeneratedAt,
            application = manifest.Application,
            userId = manifest.UserId,
            organizationId = manifest.OrganizationId,
            storeId = manifest.StoreId,
            resources = manifest.Resources,
            deltas = manifest.Deltas
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/auth/handoff", async (HttpContext context, JsonElement body, AppSessionReader sessions, CoreDataStore database) =>
{
    var authentication = await RequireSessionAsync(context, sessions, database, "HUB");
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    var csrfFailure = RequireCsrf(context, session);
    if (csrfFailure is not null) return csrfFailure;

    var application = JsonString(body, "application")?.Trim().ToUpperInvariant();
    var returnPath = SafeReturnPath(JsonString(body, "returnTo"));
    var state = JsonString(body, "state")?.Trim();
    var codeChallenge = JsonString(body, "codeChallenge")?.Trim();
    var requestedStoreText = JsonString(body, "storeId")?.Trim();
    var requestedRememberMe = JsonBoolean(body, "rememberMe");
    if (body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("rememberMe", out var rememberMeValue)
        && rememberMeValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_REMEMBER_ME", "rememberMe must be a boolean.");
    }
    if (requestedRememberMe == true && !session.RememberMe)
    {
        return Error(context, StatusCodes.Status400BadRequest, "REMEMBER_ME_REQUIRES_PERSISTENT_SOURCE", "A target application cannot outlive a non-persistent Hub session.");
    }
    // Preserve the source Hub session policy for callers that have not yet
    // adopted the field, while allowing the login UI to explicitly choose the
    // persistence policy for the target application session.
    var rememberMe = requestedRememberMe ?? session.RememberMe;
    Guid? requestedStoreId = null;
    if (!string.IsNullOrWhiteSpace(requestedStoreText))
    {
        if (!Guid.TryParse(requestedStoreText, out var parsedStoreId))
        {
            return Error(context, StatusCodes.Status400BadRequest, "INVALID_STORE_CONTEXT", "The handoff store context must be a UUID.");
        }
        requestedStoreId = parsedStoreId;
    }
    if (application is null || !ApplicationCodes.All.Contains(application) || application is "HUB" or "ADMIN")
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Only registered first-party applications can receive a handoff.");
    }
    if (returnPath is null) return Error(context, StatusCodes.Status400BadRequest, "INVALID_RETURN_PATH", "The handoff return path must be a same-origin relative path.");
    if (state is null || state.Length is < 16 or > 256) return Error(context, StatusCodes.Status400BadRequest, "INVALID_STATE", "The authentication state is required and invalid.");
    if (codeChallenge is null || !System.Text.RegularExpressions.Regex.IsMatch(codeChallenge, "^[A-Za-z0-9._~-]{43,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_CODE_CHALLENGE", "A PKCE code challenge is required and invalid.");
    }

    HubPrincipalRecord? principal;
    using (RequestPerformance.Measure(context, "authorization"))
    {
        principal = await database.ResolveHubPrincipalAsync(session, context.RequestAborted);
    }
    if (principal is null) return Error(context, StatusCodes.Status403Forbidden, "MEMBERSHIP_REQUIRED", "An active Hub organization membership is required for handoff.");
    try
    {
        using var authorizationTiming = RequestPerformance.Measure(context, "authorization");
        string? audience = null;
        string? contractVersion = null;
        if (application is "PLAY" or "POS")
        {
            var target = await database.GetApplicationLaunchTargetAsync(
                application,
                context.RequestServices.GetRequiredService<IConfiguration>()["AEVO_ENVIRONMENT"] ?? "development",
                context.RequestAborted);
            if (target is null || target.RegistryStatus != "ACTIVE" || target.LifecycleStatus is "DEPRECATED" or "RETIRED")
            {
                return Error(context, StatusCodes.Status503ServiceUnavailable, "APPLICATION_NOT_READY", "The target application is not ready for identity handoff.");
            }

            var accessSnapshot = await database.ResolveAccessSnapshotAsync(
                session,
                application,
                principal.OrganizationId,
                requestedStoreId,
                false,
                context.RequestAborted);
            var authorization = accessSnapshot.Authorization;
            if (authorization is null)
            {
                return Error(context, StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.");
            }

            var entitlement = accessSnapshot.Entitlement;
            if (entitlement is null || !entitlement.Allowed)
            {
                return Error(context, StatusCodes.Status403Forbidden, entitlement?.Reason ?? EntitlementReasonCodes.EntitlementRequired, "The target application entitlement does not allow this handoff.");
            }

            audience = target.Audience;
            contractVersion = target.ContractVersion;
        }

        var code = await database.CreateAuthorizationCodeAsync(
            session.UserId,
            application,
            principal.OrganizationId,
            requestedStoreId ?? session.StoreId,
            returnPath,
            state,
            codeChallenge,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            context.RequestAborted,
            rememberMe);
        return Results.Ok(new { application, audience, contractVersion, storeId = requestedStoreId ?? session.StoreId, code, expiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/auth/login", async (
    HttpContext context,
    PasswordLoginRequest body,
    AppSessionReader sessions,
    CoreDataStore database,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) =>
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    var application = RequestedApplication(context, body.Application, "ADMIN");
    if (!ApplicationCodes.All.Contains(application)) return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Unknown application code.");
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");
    var accountsOrigin = configuration["AEVO_ACCOUNTS_API_ORIGIN"]?.Trim();
    var serviceSecret = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    if (string.IsNullOrWhiteSpace(accountsOrigin) || string.IsNullOrWhiteSpace(serviceSecret))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "AUTH_PROVIDER_NOT_CONFIGURED", "The Accounts/Identity Platform boundary is not configured.");
    }

    if (string.IsNullOrWhiteSpace(body.Email) || body.Email.Length > 320 || string.IsNullOrEmpty(body.Password) || body.Password.Length > 1024)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_CREDENTIALS", "Email and password are required.");
    }

    try
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri($"{accountsOrigin.TrimEnd('/')}/"), "v1/auth/password"));
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-request-id", RequestId(context));
        request.Headers.TryAddWithoutValidation("x-aevo-accounts-secret", serviceSecret);
        request.Content = JsonContent.Create(new { email = body.Email, password = body.Password, application, rememberMe = body.RememberMe });
        using var externalApiTiming = RequestPerformance.Measure(context, "external_api");
        using var response = await httpClientFactory.CreateClient().SendAsync(request, context.RequestAborted);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
        if (!response.IsSuccessStatusCode || payload.ValueKind != JsonValueKind.Object)
        {
            return Error(context, (int)response.StatusCode == StatusCodes.Status401Unauthorized ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable, "AUTHENTICATION_FAILED", "The Accounts authentication boundary could not complete sign-in.");
        }

        var user = payload.TryGetProperty("user", out var userValue) ? userValue : default;
        var session = payload.TryGetProperty("session", out var sessionValue) ? sessionValue : default;
        var payloadApplication = payload.TryGetProperty("application", out var applicationValue) && applicationValue.ValueKind == JsonValueKind.String
            ? applicationValue.GetString()?.Trim().ToUpperInvariant()
            : null;
        var userId = Guid.Empty;
        if (user.ValueKind != JsonValueKind.Object || session.ValueKind != JsonValueKind.Object
            || payloadApplication != application
            || !user.TryGetProperty("id", out var userIdValue) || !Guid.TryParse(userIdValue.GetString(), out userId)
            || !user.TryGetProperty("email", out var emailValue) || emailValue.ValueKind != JsonValueKind.String
            || !session.TryGetProperty("sessionToken", out var sessionTokenValue) || sessionTokenValue.ValueKind != JsonValueKind.String
            || !session.TryGetProperty("csrfToken", out var csrfTokenValue) || csrfTokenValue.ValueKind != JsonValueKind.String
            || !session.TryGetProperty("expiresAt", out var expiresAtValue) || expiresAtValue.ValueKind != JsonValueKind.String)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_INVALID", "Accounts returned an invalid app session.");
        }

        var displayName = user.TryGetProperty("displayName", out var displayNameValue) && displayNameValue.ValueKind == JsonValueKind.String
            ? displayNameValue.GetString()
            : null;
        var issuedRememberMe = session.TryGetProperty("rememberMe", out var rememberMeValue)
            && rememberMeValue.ValueKind == JsonValueKind.True
            && rememberMeValue.GetBoolean();
        if (issuedRememberMe != body.RememberMe)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_INVALID", "Accounts returned a session with an invalid persistence policy.");
        }
        var secure = IsSecureCookie(configuration);
        var expiresAt = expiresAtValue.GetString()!;
        var cookieExpiresAt = expiresAt;
        if (issuedRememberMe)
        {
            if (!session.TryGetProperty("absoluteExpiresAt", out var absoluteExpiresAtValue) || absoluteExpiresAtValue.ValueKind != JsonValueKind.String)
            {
                return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_INVALID", "Accounts returned a remembered session without an absolute expiry.");
            }
            cookieExpiresAt = absoluteExpiresAtValue.GetString()!;
        }
        context.Response.Headers.Append("set-cookie", SessionCookie(sessions.CookieName(application), sessionTokenValue.GetString()!, cookieExpiresAt, secure, issuedRememberMe));
        context.Response.Headers.Append("set-cookie", CsrfCookie(sessions.CsrfCookieName(application), csrfTokenValue.GetString()!, cookieExpiresAt, secure, issuedRememberMe));
        return Results.Ok(new
        {
            success = true,
            user = new { id = userId.ToString(), email = emailValue.GetString(), displayName },
            session = new { expiresAt, rememberMe = issuedRememberMe },
            appCode = application
        });
    }
    catch (HttpRequestException)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "ACCOUNTS_UNAVAILABLE", "The Accounts authentication boundary is unavailable.");
    }
});

app.MapPost("/api/v1/hub/onboarding/register", async (
    HttpContext context,
    JsonElement body,
    AppSessionReader sessions,
    CoreDataStore database,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) =>
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    var accountsOrigin = configuration["AEVO_ACCOUNTS_API_ORIGIN"]?.Trim();
    var serviceSecret = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    if (string.IsNullOrWhiteSpace(accountsOrigin) || string.IsNullOrWhiteSpace(serviceSecret)) return Error(context, 503, "AUTH_PROVIDER_NOT_CONFIGURED", "The Accounts/Identity Platform boundary is not configured.");
    var requestBody = JsonNode.Parse(body.GetRawText())?.AsObject() ?? new JsonObject();
    requestBody["application"] = "HUB";
    if (body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("rememberMe", out var requestedRememberMe)
        && requestedRememberMe.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_REMEMBER_ME", "rememberMe must be a boolean.");
    }
    var rememberMe = JsonBoolean(body, "rememberMe") ?? false;
    requestBody["rememberMe"] = rememberMe;
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri($"{accountsOrigin.TrimEnd('/')}/"), "v1/auth/register"));
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-request-id", RequestId(context));
        request.Headers.TryAddWithoutValidation("x-aevo-accounts-secret", serviceSecret);
        request.Content = JsonContent.Create(requestBody);
        using var externalApiTiming = RequestPerformance.Measure(context, "external_api");
        using var response = await httpClientFactory.CreateClient().SendAsync(request, context.RequestAborted);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
        if (!response.IsSuccessStatusCode || payload.ValueKind != JsonValueKind.Object) return Error(context, (int)response.StatusCode, "REGISTRATION_FAILED", "The Accounts authentication boundary could not complete registration.");
        var user = payload.TryGetProperty("user", out var userValue) ? userValue : default;
        var session = payload.TryGetProperty("session", out var sessionValue) ? sessionValue : default;
        if (user.ValueKind != JsonValueKind.Object || session.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var userIdValue) || !Guid.TryParse(userIdValue.GetString(), out _)
            || !session.TryGetProperty("sessionToken", out var sessionToken) || sessionToken.ValueKind != JsonValueKind.String
            || !session.TryGetProperty("csrfToken", out var csrfToken) || csrfToken.ValueKind != JsonValueKind.String
            || !session.TryGetProperty("expiresAt", out var expiresAt) || expiresAt.ValueKind != JsonValueKind.String)
        {
            return Error(context, 503, "SESSION_ISSUER_INVALID", "Accounts returned an invalid app session.");
        }
        var issuedRememberMe = session.TryGetProperty("rememberMe", out var rememberMeValue)
            && rememberMeValue.ValueKind == JsonValueKind.True
            && rememberMeValue.GetBoolean();
        if (issuedRememberMe != rememberMe)
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_INVALID", "Accounts returned a session with an invalid persistence policy.");
        }
        var secure = IsSecureCookie(configuration);
        var cookieExpiresAt = expiresAt.GetString()!;
        if (issuedRememberMe)
        {
            if (!session.TryGetProperty("absoluteExpiresAt", out var absoluteExpiresAtValue) || absoluteExpiresAtValue.ValueKind != JsonValueKind.String)
            {
                return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_INVALID", "Accounts returned a remembered session without an absolute expiry.");
            }
            cookieExpiresAt = absoluteExpiresAtValue.GetString()!;
        }
        context.Response.Headers.Append("set-cookie", SessionCookie(sessions.CookieName("HUB"), sessionToken.GetString()!, cookieExpiresAt, secure, issuedRememberMe));
        context.Response.Headers.Append("set-cookie", CsrfCookie(sessions.CsrfCookieName("HUB"), csrfToken.GetString()!, cookieExpiresAt, secure, issuedRememberMe));
        return Results.Ok(new { success = true, user, session = new { expiresAt = expiresAt.GetString(), rememberMe = issuedRememberMe }, appCode = "HUB" });
    }
    catch (HttpRequestException)
    {
        return Error(context, 503, "ACCOUNTS_UNAVAILABLE", "The Accounts authentication boundary is unavailable.");
    }
});

app.MapPost("/api/auth/password/reset-request", async (HttpContext context, JsonElement body, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    // Supabase password recovery supports PKCE.  Core creates and retains the
    // verifier in an HttpOnly cookie so the browser never has to handle a
    // provider credential or a verifier-bearing callback payload.
    var verifier = OpaqueRequestToken();
    var requestBody = JsonNode.Parse(body.GetRawText())?.AsObject() ?? new JsonObject();
    requestBody["codeChallenge"] = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
    context.Response.Headers.Append("set-cookie", RecoveryCookie(RecoveryVerifierCookieName(), verifier, 600, IsSecureCookie(configuration), httpOnly: true));
    return await ForwardAccountsAuthAsync(context, requestBody, "v1/auth/password/reset-request", httpClientFactory, configuration);
});

app.MapGet("/api/auth/password/recovery/callback", async (HttpContext context, CoreDataStore database, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var code = context.Request.Query["code"].ToString().Trim();
    var verifier = context.Request.Cookies[RecoveryVerifierCookieName()]?.Trim();
    var failureRedirect = RecoveryRedirect(configuration, "password_recovery_invalid");
    if (code.Length is < 16 or > 4096 || string.IsNullOrWhiteSpace(verifier)) return Results.Redirect(failureRedirect);

    var forwarded = await ForwardAccountsAuthPayloadAsync(
        context,
        new { code, codeVerifier = verifier },
        "v1/auth/password/recovery/callback",
        httpClientFactory,
        configuration);
    if (forwarded.StatusCode is < 200 or >= 300 || forwarded.Payload.ValueKind != JsonValueKind.Object)
    {
        return Results.Redirect(failureRedirect);
    }

    var user = forwarded.Payload.TryGetProperty("user", out var userValue) && userValue.ValueKind == JsonValueKind.Object
        ? userValue
        : default;
    var userIdText = user.ValueKind == JsonValueKind.Object ? JsonString(user, "id") : null;
    var email = user.ValueKind == JsonValueKind.Object ? JsonString(user, "email") : null;
    var providerAccessToken = JsonString(forwarded.Payload, "accessToken");
    if (!Guid.TryParse(userIdText, out var userId) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(providerAccessToken))
    {
        return Results.Redirect(failureRedirect);
    }

    try
    {
        var grant = await database.CreatePasswordRecoveryGrantAsync(
            userId,
            email,
            providerAccessToken,
            DateTimeOffset.UtcNow.AddMinutes(10),
            RequestId(context),
            context.RequestAborted);
        var secure = IsSecureCookie(configuration);
        context.Response.Headers.Append("set-cookie", RecoveryCookie(RecoveryGrantCookieName(), grant, 600, secure, httpOnly: true));
        context.Response.Headers.Append("set-cookie", RecoveryCookie(RecoveryCsrfCookieName(), OpaqueRequestToken(), 600, secure, httpOnly: false));
        context.Response.Headers.Append("set-cookie", ClearCookie(RecoveryVerifierCookieName(), secure, httpOnly: true));
        return Results.Redirect(RecoveryRedirect(configuration, "ready"));
    }
    catch (CoreDatabaseException)
    {
        return Results.Redirect(failureRedirect);
    }
});

app.MapPost("/api/auth/password/update", async (HttpContext context, JsonElement body, AppSessionReader sessions, CoreDataStore database, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    var token = sessions.ReadSessionCookie(context, "HUB");
    CoreSession? session = null;
    if (token is not null && database.IsConfigured) session = await database.ResolveSessionAsync(token, "HUB", context.RequestAborted);
    if (session is not null)
    {
        var csrfFailure = RequireCsrf(context, session);
        if (csrfFailure is not null) return csrfFailure;
    }
    else
    {
        var grantToken = context.Request.Cookies[RecoveryGrantCookieName()]?.Trim();
        var recoveryCsrf = context.Request.Cookies[RecoveryCsrfCookieName()]?.Trim();
        var providedCsrf = context.Request.Headers["x-csrf-token"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(grantToken)) return Error(context, 401, "AUTHENTICATION_REQUIRED", "A valid app session or password recovery link is required.");
        if (string.IsNullOrWhiteSpace(recoveryCsrf) || string.IsNullOrWhiteSpace(providedCsrf) || !SecureEquals(recoveryCsrf, providedCsrf))
        {
            return Error(context, StatusCodes.Status403Forbidden, "CSRF_INVALID", "A valid recovery CSRF token is required.");
        }
        if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API recovery store is not configured.");

        PasswordRecoveryGrant? recoveryGrant;
        try
        {
            recoveryGrant = await database.ConsumePasswordRecoveryGrantAsync(grantToken, context.RequestAborted);
        }
        catch (CoreDatabaseException error)
        {
            return DatabaseError(context, error);
        }
        if (recoveryGrant is null) return Error(context, StatusCodes.Status401Unauthorized, "PASSWORD_RECOVERY_INVALID", "The password recovery link is expired or has already been used.");

        var recoveryRequestBody = JsonNode.Parse(body.GetRawText())?.AsObject() ?? new JsonObject();
        recoveryRequestBody["userId"] = recoveryGrant.UserId.ToString();
        recoveryRequestBody["email"] = recoveryGrant.Email;
        recoveryRequestBody["recoveryAccessToken"] = recoveryGrant.ProviderAccessToken;
        var forwarded = await ForwardAccountsAuthPayloadAsync(context, recoveryRequestBody, "v1/auth/password/update", httpClientFactory, configuration);
        var secure = IsSecureCookie(configuration);
        context.Response.Headers.Append("set-cookie", ClearCookie(RecoveryGrantCookieName(), secure, httpOnly: true));
        context.Response.Headers.Append("set-cookie", ClearCookie(RecoveryCsrfCookieName(), secure, httpOnly: false));
        if (forwarded.StatusCode is >= 200 and < 300)
        {
            await database.RevokeSessionsForUserAsync(recoveryGrant.UserId, context.RequestAborted);
        }
        if (forwarded.StatusCode == 503) return Error(context, StatusCodes.Status503ServiceUnavailable, "ACCOUNTS_UNAVAILABLE", "The Accounts authentication boundary is unavailable.");
        if (forwarded.Payload.ValueKind == JsonValueKind.Object) return Results.Json(forwarded.Payload, statusCode: forwarded.StatusCode);
        return Error(context, StatusCodes.Status502BadGateway, "AUTH_PROVIDER_INVALID_RESPONSE", "The Accounts authentication boundary returned an invalid response.");
    }

    var requestBody = JsonNode.Parse(body.GetRawText())?.AsObject() ?? new JsonObject();
    if (session is not null)
    {
        requestBody["userId"] = session.UserId.ToString();
        requestBody["email"] = session.Email;
    }

    var authenticatedUpdate = await ForwardAccountsAuthPayloadAsync(
        context,
        requestBody,
        "v1/auth/password/update",
        httpClientFactory,
        configuration);
    if (authenticatedUpdate.StatusCode == StatusCodes.Status503ServiceUnavailable)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "ACCOUNTS_UNAVAILABLE", "The Accounts authentication boundary is unavailable.");
    }
    if (authenticatedUpdate.StatusCode is >= 200 and < 300 && session is not null)
    {
        // A password change invalidates every existing app session, including
        // remembered devices. The browser is redirected to sign in again by
        // the Hub security route after this response succeeds.
        await database.RevokeSessionsForUserAsync(session.UserId, context.RequestAborted);
    }
    if (authenticatedUpdate.Payload.ValueKind == JsonValueKind.Object)
    {
        return Results.Json(authenticatedUpdate.Payload, statusCode: authenticatedUpdate.StatusCode);
    }
    return Error(context, StatusCodes.Status502BadGateway, "AUTH_PROVIDER_INVALID_RESPONSE", "The Accounts authentication boundary returned an invalid response.");
});

app.MapPost("/api/auth/refresh", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
{
    var application = RequestedApplication(context, null, "ADMIN");
    var token = sessions.ReadSessionCookie(context, application);
    if (token is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "An app-scoped session is required.");
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        var current = await database.ResolveSessionAsync(token, application, context.RequestAborted);
        if (current is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid or expired.");
        var csrfFailure = RequireCsrf(context, current);
        if (csrfFailure is not null) return csrfFailure;
        var issued = await database.RefreshSessionAsync(
            token,
            application,
            SessionIdleLifetime(configuration, current.RememberMe),
            SessionLifetime(configuration, current.RememberMe),
            context.RequestAborted);
        if (issued is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is no longer active.");
        var secure = IsSecureCookie(configuration);
        var cookieExpiresAt = (issued.RememberMe ? issued.AbsoluteExpiresAt : null) ?? issued.ExpiresAt;
        context.Response.Headers.Append("set-cookie", SessionCookie(sessions.CookieName(application), issued.SessionToken, cookieExpiresAt.ToString("O"), secure, issued.RememberMe));
        context.Response.Headers.Append("set-cookie", CsrfCookie(sessions.CsrfCookieName(application), issued.CsrfToken, cookieExpiresAt.ToString("O"), secure, issued.RememberMe));
        return Results.Ok(new { success = true, session = new { expiresAt = issued.ExpiresAt, rememberMe = issued.RememberMe } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/auth/logout", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    var application = RequestedApplication(context, null, "ADMIN");
    var token = sessions.ReadSessionCookie(context, application);
    if (token is not null && database.IsConfigured)
    {
        try
        {
            var current = await database.ResolveSessionAsync(token, application, context.RequestAborted);
            if (current is not null)
            {
                var csrfFailure = RequireCsrf(context, current);
                if (csrfFailure is not null) return csrfFailure;
            }
            await database.RevokeSessionAsync(token, application, context.RequestAborted);
        }
        catch (CoreDatabaseException error) { return DatabaseError(context, error); }
    }
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var secure = IsSecureCookie(configuration);
    context.Response.Headers.Append("set-cookie", ClearCookie(sessions.CookieName(application), secure, httpOnly: true));
    context.Response.Headers.Append("set-cookie", ClearCookie(sessions.CsrfCookieName(application), secure, httpOnly: false));
    return Results.NoContent();
});

app.MapGet("/api/v1/access", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var application = context.Request.Query["application"].ToString().Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)) return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Unknown application code.");

    var organizationQuery = context.Request.Query["organizationId"].ToString().Trim();
    var storeQuery = context.Request.Query["storeId"].ToString().Trim();
    if (application == "HUB" && string.IsNullOrWhiteSpace(organizationQuery) && string.IsNullOrWhiteSpace(storeQuery))
    {
        var hubAuthentication = await RequireHubBootstrapSessionAsync(context, sessions, database);
        if (hubAuthentication.Failure is not null) return hubAuthentication.Failure;
        using var authorizationTiming = RequestPerformance.Measure(context, "authorization");
        var hubPrincipal = hubAuthentication.Principal;
        var hubSession = hubAuthentication.Session!;
        return Results.Ok(new
        {
            allowed = hubPrincipal is not null,
            application,
            appCode = application,
            reason = hubPrincipal is not null ? "ALLOWED" : "MEMBERSHIP_REQUIRED",
            userId = hubSession.UserId,
            organizationId = hubPrincipal?.OrganizationId ?? hubSession.OrganizationId,
            storeId = hubSession.StoreId,
            permissions = hubPrincipal?.Permissions ?? Array.Empty<string>(),
            platformRole = (string?)null,
            platformPermissions = Array.Empty<string>(),
            checkedAt = DateTimeOffset.UtcNow
        });
    }

    var authentication = await RequireSessionAsync(context, sessions, database, application);
    var hubProxy = false;
    if (authentication.Failure is not null
        && application is not ("HUB" or "ADMIN")
        && string.Equals(context.Request.Headers["x-aevo-app"].ToString().Trim(), "HUB", StringComparison.Ordinal))
    {
        // Hub may inspect another app's launch decision without receiving an
        // app session. The target app still requires its own app-scoped
        // session when it is actually opened.
        authentication = await RequireSessionAsync(context, sessions, database, "HUB");
        hubProxy = authentication.Failure is null;
    }
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    Guid? requestedOrganizationId = null;
    Guid? requestedStoreId = null;
    if (!string.IsNullOrWhiteSpace(organizationQuery) && !Guid.TryParse(organizationQuery, out var parsedOrganizationId))
    {
        return Error(context, StatusCodes.Status400BadRequest, "TENANT_CONTEXT_INVALID", "The organizationId query parameter must be a UUID.");
    }
    if (!string.IsNullOrWhiteSpace(storeQuery) && !Guid.TryParse(storeQuery, out var parsedStoreId))
    {
        return Error(context, StatusCodes.Status400BadRequest, "TENANT_CONTEXT_INVALID", "The storeId query parameter must be a UUID.");
    }
    if (!string.IsNullOrWhiteSpace(organizationQuery)) requestedOrganizationId = Guid.Parse(organizationQuery);
    if (!string.IsNullOrWhiteSpace(storeQuery)) requestedStoreId = Guid.Parse(storeQuery);
    var scopeValidation = TenantContextValidator.ValidateScope(session.OrganizationId, session.StoreId, requestedOrganizationId, requestedStoreId);
    if (!scopeValidation.IsValid) return Error(context, scopeValidation.StatusCode, scopeValidation.Code!, scopeValidation.Message!);
    var effectiveOrganizationId = requestedOrganizationId ?? session.OrganizationId;
    var effectiveStoreId = requestedStoreId ?? session.StoreId;
    try
    {
        using var authorizationTiming = RequestPerformance.Measure(context, "authorization");
        if (application == "HUB")
        {
            var hubSnapshot = await database.ResolveAccessSnapshotAsync(session, application, effectiveOrganizationId, effectiveStoreId, true, context.RequestAborted);
            var hubAuthorization = hubSnapshot.Authorization;
            return Results.Ok(new
            {
                allowed = hubAuthorization is not null,
                application,
                appCode = application,
                reason = hubAuthorization is not null ? "ALLOWED" : "MEMBERSHIP_REQUIRED",
                userId = session.UserId,
                organizationId = hubAuthorization?.OrganizationId ?? session.OrganizationId,
                storeId = effectiveStoreId,
                permissions = hubAuthorization?.Permissions ?? Array.Empty<string>(),
                platformRole = (string?)null,
                platformPermissions = Array.Empty<string>(),
                checkedAt = DateTimeOffset.UtcNow
            });
        }
        var accessSnapshot = application == "ADMIN"
            ? null
            : await database.ResolveAccessSnapshotAsync(
                session,
                application,
                effectiveOrganizationId,
                effectiveStoreId,
                !hubProxy,
                context.RequestAborted);
        var authorization = accessSnapshot?.Authorization;
        if (authorization is null && application is not "ADMIN" && !hubProxy)
        {
            return Error(context, StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.");
        }
        var permissions = application == "ADMIN"
            ? PlatformPermissions(session.PlatformRole)
            : authorization?.Permissions ?? Array.Empty<string>();
        var entitlement = accessSnapshot?.Entitlement;
        var assignmentAllowed = application == "ADMIN" || authorization is not null;
        var permissionAllowed = application == "ADMIN" ? session.PlatformRole is not null && permissions.Count > 0 : permissions.Count > 0;
        var allowed = assignmentAllowed && permissionAllowed && (entitlement?.Allowed ?? true);
        var reason = !assignmentAllowed
            ? "APP_ASSIGNMENT_REQUIRED"
            : !permissionAllowed
                ? application == "ADMIN" ? "PLATFORM_PERMISSION_REQUIRED" : "PERMISSION_REQUIRED"
                : entitlement?.Reason ?? "ALLOWED";
        return Results.Ok(new
        {
            allowed,
            application,
            appCode = application,
            reason,
            userId = session.UserId,
            organizationId = authorization?.OrganizationId ?? session.OrganizationId,
            storeId = effectiveStoreId,
            permissions,
            platformRole = application == "ADMIN" ? session.PlatformRole : null,
            platformPermissions = application == "ADMIN" ? permissions : Array.Empty<string>(),
            projectionVersion = entitlement?.ProjectionVersion,
            checkedAt = DateTimeOffset.UtcNow
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/overview", async (
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.health");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var overview = await database.GetPlatformOverviewAsync(
            configuration["AEVO_ENVIRONMENT"] ?? "development",
            string.Equals(configuration["AEVO_ALLOW_UNLIMITED_TESTING"], "true", StringComparison.OrdinalIgnoreCase),
            context.RequestAborted);
        return Results.Ok(new
        {
            success = true,
            totalOrganizations = overview.TotalOrganizations,
            totalStores = overview.TotalStores,
            totalUsers = overview.TotalUsers,
            totalDevices = overview.TotalDevices,
            totalSubscriptions = overview.TotalSubscriptions,
            operatingMode = new { mode = overview.OperatingMode, unlimited = overview.Unlimited }
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/applications", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.health");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var applications = (await database.ListApplicationsAsync(context.RequestAborted)).Select(record => new
        {
            code = record.Code,
            name = record.Name,
            kind = record.Kind,
            status = record.Status,
            manifestVersion = record.ManifestVersion,
            ownerRepository = record.OwnerRepository,
            contractVersion = record.ContractVersion,
            audience = record.Audience,
            installScope = record.InstallScope,
            storeScoped = record.StoreScoped,
            launchPath = record.LaunchPath,
            lifecycleStatus = record.LifecycleStatus,
            capabilities = record.Capabilities,
            configSchemaRefs = record.ConfigSchemaRefs,
            createdAt = record.CreatedAt,
            updatedAt = record.UpdatedAt
        }).ToArray();
        return Results.Ok(new { success = true, applications });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/organizations", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 50) : 50;
    var page = int.TryParse(context.Request.Query["page"], out var requestedPage) ? Math.Clamp(requestedPage, 1, 100_000) : 1;
    var query = context.Request.Query["q"].ToString();
    var status = context.Request.Query["status"].ToString();
    var sort = context.Request.Query["sort"].ToString();
    var direction = context.Request.Query["direction"].ToString();
    try
    {
        var directory = await database.ListAdminOrganizationsPageAsync(
            limit,
            (page - 1) * limit,
            query,
            status,
            sort,
            direction,
            context.RequestAborted);
        var organizations = directory.Items.Select(record => new
        {
            id = record.Id.ToString(),
            name = record.Name,
            slug = record.Slug,
            status = record.Status,
            maxUsers = record.MaxUsers,
            maxStores = record.MaxStores,
            storesCount = record.StoresCount,
            membersCount = record.MembersCount,
            createdAt = record.CreatedAt
        }).ToArray();
        return Results.Ok(new { success = true, organizations, page, limit, hasMore = directory.HasMore });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/subscriptions", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "subscription.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 50) : 50;
    var page = int.TryParse(context.Request.Query["page"], out var requestedPage) ? Math.Clamp(requestedPage, 1, 100_000) : 1;
    var query = context.Request.Query["q"].ToString();
    var status = context.Request.Query["status"].ToString();
    var sort = context.Request.Query["sort"].ToString();
    var direction = context.Request.Query["direction"].ToString();
    try
    {
        var directory = await database.ListAdminSubscriptionsPageAsync(
            limit,
            (page - 1) * limit,
            query,
            status,
            sort,
            direction,
            context.RequestAborted);
        var subscriptions = directory.Items.Select(record => new
        {
            id = record.Id.ToString(),
            organizationId = record.OrganizationId.ToString(),
            organizationName = record.OrganizationName,
            planId = record.PlanId,
            provider = record.Provider,
            status = record.Status,
            trialEnd = record.TrialEnd,
            currentPeriodStart = record.CurrentPeriodStart,
            currentPeriodEnd = record.CurrentPeriodEnd,
            cancelAtPeriodEnd = record.CancelAtPeriodEnd,
            canceledAt = record.CanceledAt,
            providerSubscriptionId = record.ProviderSubscriptionId,
            projectionVersion = record.ProjectionVersion,
            updatedAt = record.UpdatedAt
        }).ToArray();
        return Results.Ok(new { success = true, subscriptions, page, limit, hasMore = directory.HasMore });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/users", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 50) : 50;
    var page = int.TryParse(context.Request.Query["page"], out var requestedPage) ? Math.Clamp(requestedPage, 1, 100_000) : 1;
    var query = context.Request.Query["q"].ToString();
    var status = context.Request.Query["status"].ToString();
    var sort = context.Request.Query["sort"].ToString();
    var direction = context.Request.Query["direction"].ToString();
    try
    {
        var directory = await database.ListAdminUsersPageAsync(
            limit,
            (page - 1) * limit,
            query,
            status,
            sort,
            direction,
            context.RequestAborted);
        var users = directory.Items.Select(record => new
        {
            id = record.Id.ToString(),
            email = record.Email,
            displayName = record.DisplayName,
            status = record.Status,
            memberships = record.Memberships.Select(membership => new
            {
                organizationName = membership.OrganizationName,
                role = membership.Role
            }).ToArray()
        }).ToArray();
        return Results.Ok(new { success = true, users, page, limit, hasMore = directory.HasMore });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/query/execute", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, JsonElement body) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.read");
    if (authorization.Failure is not null) return authorization.Failure;
    if (!TryReadAdminDirectoryQuery(body, out var query, out var limit, out var offset, out var queryError))
    {
        return Error(context, StatusCodes.Status400BadRequest, "QUERY_SPEC_INVALID", queryError ?? "The query specification is invalid.");
    }

    try
    {
        var results = await database.SearchAdminDirectoryAsync(
            query!,
            limit,
            HasPlatformPermission(authorization.Session!.PlatformRole, "subscription.read"),
            HasPlatformPermission(authorization.Session.PlatformRole, "system.health"),
            HasPlatformPermission(authorization.Session.PlatformRole, "system.health"),
            context.RequestServices.GetRequiredService<IConfiguration>()["AEVO_ENVIRONMENT"] ?? "local",
            context.RequestAborted);
        return Results.Ok(new
        {
            success = true,
            version = 1,
            model = "admin.directory",
            query,
            limit,
            offset,
            items = results.Select(result => new
            {
                type = result.Type,
                id = result.Id,
                title = result.Title,
                subtitle = result.Subtitle,
                status = result.Status,
                route = result.Route
            }).ToArray()
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/organizations/{organizationId:guid}", async (HttpContext context, Guid organizationId, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var detail = await database.GetAdminOrganizationAsync(organizationId, context.RequestAborted);
        return detail is null
            ? Error(context, StatusCodes.Status404NotFound, "ORGANIZATION_NOT_FOUND", "Organization was not found.")
            : Results.Ok(new
            {
                success = true,
                organization = new
                {
                    id = detail.Organization.Id.ToString(),
                    name = detail.Organization.Name,
                    slug = detail.Organization.Slug,
                    status = detail.Organization.Status,
                    maxUsers = detail.Organization.MaxUsers,
                    maxStores = detail.Organization.MaxStores,
                    storesCount = detail.Organization.StoresCount,
                    membersCount = detail.Organization.MembersCount,
                    createdAt = detail.Organization.CreatedAt,
                    stores = detail.Stores.Select(store => new { id = store.Id.ToString(), code = store.Code, name = store.Name, timezone = store.Timezone, currency = store.Currency, status = store.Status, createdAt = store.CreatedAt }).ToArray(),
                    members = detail.Members.Select(member => new { membershipId = member.MembershipId.ToString(), userId = member.UserId.ToString(), email = member.Email, displayName = member.DisplayName, role = member.Role, status = member.Status, createdAt = member.CreatedAt }).ToArray()
                }
            });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/subscriptions/{subscriptionId:guid}", async (HttpContext context, Guid subscriptionId, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "subscription.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var subscription = await database.GetAdminSubscriptionAsync(subscriptionId, context.RequestAborted);
        return subscription is null
            ? Error(context, StatusCodes.Status404NotFound, "SUBSCRIPTION_NOT_FOUND", "Subscription was not found.")
            : Results.Ok(new { success = true, subscription = new { id = subscription.Id.ToString(), organizationId = subscription.OrganizationId.ToString(), subscription.OrganizationName, subscription.PlanId, subscription.Provider, subscription.Status, subscription.TrialEnd, subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd, subscription.CancelAtPeriodEnd, subscription.CanceledAt, subscription.ProviderSubscriptionId, subscription.ProjectionVersion, subscription.UpdatedAt } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/users/{userId:guid}", async (HttpContext context, Guid userId, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var user = await database.GetAdminUserAsync(userId, context.RequestAborted);
        return user is null
            ? Error(context, StatusCodes.Status404NotFound, "USER_NOT_FOUND", "User was not found.")
            : Results.Ok(new { success = true, user = new { id = user.Id.ToString(), user.Email, user.DisplayName, user.Status, memberships = user.Memberships.Select(membership => new { membership.OrganizationName, membership.Role }).ToArray() } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/organizations", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, JsonElement body) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var reason = JsonString(body, "reason")?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    try
    {
        var organization = await database.CreateAdminOrganizationAsync(authorization.Session!.UserId, RequestId(context), body, reason, context.RequestAborted);
        return organization is null
            ? Error(context, StatusCodes.Status503ServiceUnavailable, "ORGANIZATION_CREATE_FAILED", "Organization creation returned no organization.")
            : Results.Created($"/api/v1/admin/organizations/{organization.Organization.Id}", new { success = true, organization = new { id = organization.Organization.Id.ToString(), organization.Organization.Name, organization.Organization.Slug, organization.Organization.Status } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPatch("/api/v1/admin/organizations/{organizationId:guid}", async (HttpContext context, Guid organizationId, AppSessionReader sessions, CoreDataStore database, JsonElement body) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "organization.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var reason = JsonString(body, "reason")?.Trim();
    var status = JsonString(body, "status")?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    if (status is not null and not "ACTIVE" and not "SUSPENDED") return Error(context, StatusCodes.Status400BadRequest, "ORGANIZATION_STATUS_INVALID", "Organization status must be ACTIVE or SUSPENDED.");
    try
    {
        var organization = await database.UpdateAdminOrganizationAsync(authorization.Session!.UserId, RequestId(context), organizationId, body, reason, context.RequestAborted);
        return organization is null
            ? Error(context, StatusCodes.Status404NotFound, "ORGANIZATION_NOT_FOUND", "Organization was not found.")
            : Results.Ok(new { success = true, organization = new { id = organization.Organization.Id.ToString(), organization.Organization.Name, organization.Organization.Slug, organization.Organization.Status } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPatch("/api/v1/admin/users/{userId:guid}", async (HttpContext context, Guid userId, AppSessionReader sessions, CoreDataStore database, JsonElement body) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "user.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var reason = JsonString(body, "reason")?.Trim();
    var status = JsonString(body, "status")?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    if (status is not "ACTIVE" and not "DISABLED") return Error(context, StatusCodes.Status400BadRequest, "USER_STATUS_INVALID", "User status must be ACTIVE or DISABLED.");
    try
    {
        var user = await database.UpdateAdminUserStatusAsync(authorization.Session!.UserId, RequestId(context), userId, status, reason, context.RequestAborted);
        return user is null
            ? Error(context, StatusCodes.Status404NotFound, "USER_NOT_FOUND", "User was not found.")
            : Results.Ok(new { success = true, user = new { id = user.Id.ToString(), user.Email, user.DisplayName, user.Status } });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/connections", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.health");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var records = await database.ListConnectionsAsync(configuration["AEVO_ENVIRONMENT"] ?? "local", context.RequestAborted);
        var connections = records.Select(record => new
        {
            appCode = record.AppCode,
            label = record.Label,
            status = record.Status,
            baseUrl = record.BaseUrl,
            checkedAt = record.CheckedAt,
            latencyMs = record.LatencyMs,
            lastErrorCode = record.LastErrorCode,
            metadata = record.Metadata
        }).ToArray();
        return Results.Ok(new { success = true, items = connections, connections });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/migrations/control-plane", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.jobs");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var entries = (await database.ListMigrationAuthorityAsync(context.RequestAborted)).Select(record => new
        {
            schema = record.SchemaName,
            table = record.TableName,
            semanticDomain = record.SemanticDomain,
            historicalMigrationRepos = record.HistoricalMigrationRepos,
            currentOwner = record.CurrentOwner,
            targetOwner = record.TargetOwner,
            runtimeWriters = record.RuntimeWriters,
            readers = record.Readers,
            currentWriteMode = record.CurrentWriteMode,
            migrationPhase = record.MigrationPhase,
            transitionState = record.TransitionState,
            lastReconciledAt = record.LastReconciledAt,
            updatedAt = record.UpdatedAt,
            deleteAfter = record.DeleteAfter
        }).ToArray();
        return Results.Ok(new { success = true, entries });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/audit-logs", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, AuditCursorSigner cursorSigner) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "audit.read");
    if (authorization.Failure is not null) return authorization.Failure;
    if (!cursorSigner.IsConfigured)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "AUDIT_CURSOR_NOT_CONFIGURED", "The audit cursor signing boundary is not configured.");
    }
    var application = context.Request.Query["app"].ToString().Trim().ToUpperInvariant();
    var applicationFilter = string.IsNullOrWhiteSpace(application) || application == "ALL" || application == "PLATFORM" ? null : application;
    var limit = int.TryParse(context.Request.Query["limit"], out var requestedLimit) ? Math.Clamp(requestedLimit, 1, 50) : 50;
    var query = context.Request.Query["q"].ToString().Trim();
    if (query.Length > 200)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_AUDIT_REQUEST", "q cannot exceed 200 characters.");
    }
    AuditCursorPosition? after = null;
    var cursorValue = context.Request.Query["cursor"].ToString().Trim();
    if (!string.IsNullOrWhiteSpace(cursorValue)
        && !cursorSigner.TryVerify(cursorValue, applicationFilter, DateTimeOffset.UtcNow, out after))
    {
        return Error(context, StatusCodes.Status400BadRequest, "AUDIT_CURSOR_INVALID", "The audit log cursor is invalid, expired, or bound to another filter.");
    }
    try
    {
    var page = await database.ListAuditLogsPageAsync(limit, applicationFilter, after, string.IsNullOrWhiteSpace(query) ? null : query, context.RequestAborted);
        var logs = page.Items.Select(record => new
        {
            id = record.Id.ToString(),
            organizationId = (string?)null,
            adminUserId = record.ActorId.ToString(),
            platformRole = record.PlatformRole,
            action = record.Action,
            targetType = record.TargetType,
            targetId = record.TargetId,
            reason = record.Reason,
            beforeState = record.BeforeState,
            afterState = record.AfterState,
            metadata = new { applicationCode = record.AppCode, requestId = record.RequestId },
            applicationCode = record.AppCode,
            createdAt = record.CreatedAt
        }).ToArray();
        var nextCursor = page.HasMore && page.Items.Count > 0
            ? cursorSigner.Create(new AuditCursorPosition(page.Items[^1].CreatedAt, page.Items[^1].Id, applicationFilter))
            : null;
        return Results.Ok(new { success = true, logs, nextCursor });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/overview", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.analytics.read");
    if (authorization.Failure is not null) return authorization.Failure;
    var days = int.TryParse(context.Request.Query["days"], out var requestedDays) ? requestedDays : 30;
    try
    {
        var overview = await database.GetGoOverviewAsync(days, context.RequestAborted);
        return Results.Ok(new { success = true, overview });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/settings", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var settings = await database.GetGoSettingsAsync(context.RequestAborted);
        if (settings is null) return Error(context, StatusCodes.Status503ServiceUnavailable, "GO_SETTINGS_NOT_CONFIGURED", "Aevo Go settings are not configured.");
        var flags = settings.FeatureFlags.ToDictionary(
            flag => flag.FlagKey,
            flag => new { enabled = flag.Enabled, rolloutPercent = flag.RolloutPercent, config = flag.Config });
        return Results.Ok(new
        {
            success = true,
            settings = settings.Settings,
            settingsUpdatedAt = settings.UpdatedAt,
            settingsUpdatedBy = settings.UpdatedBy?.ToString(),
            featureFlags = flags
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPatch("/api/v1/admin/go/settings", async (HttpContext context, GoSettingsUpdateRequest body, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    if (body.Reason.Trim().Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    try
    {
        await database.UpdateGoSettingsAsync(authorization.Session!.UserId, RequestId(context), body.Reason.Trim(), body.Settings, context.RequestAborted);
        return Results.Ok(new { success = true });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPatch("/api/v1/admin/go/feature-flags/{flagKey}", async (HttpContext context, string flagKey, GoFeatureFlagUpdateRequest body, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    if (FeedConfigCompatibility.IsLegacyFeedFlagKey(flagKey))
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_USE_LIFECYCLE", "Feed flags are read-only compatibility inputs; use the Core Feed config lifecycle.");
    }
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    if (body.Reason.Trim().Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    try
    {
        await database.UpdateGoFeatureFlagAsync(authorization.Session!.UserId, RequestId(context), body.Reason.Trim(), flagKey, body.Enabled, body.RolloutPercent, context.RequestAborted);
        return Results.Ok(new { success = true });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/config", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        return Results.Ok(await feedConfig.GetAdminSnapshotAsync(RequestId(context), context.RequestAborted));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/config/revisions", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.read");
    if (authorization.Failure is not null) return authorization.Failure;
    var rawLimit = context.Request.Query["limit"].ToString();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var parsedLimit) || parsedLimit is < 1 or > 50))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_FEED_CONFIG_REQUEST", "limit must be between 1 and 50.");
    }
    var requestedLimit = string.IsNullOrWhiteSpace(rawLimit) ? 50 : int.Parse(rawLimit);
    try
    {
        return Results.Ok(new
        {
            items = await feedConfig.ListRevisionsAsync(requestedLimit, context.RequestAborted),
            requestId = RequestId(context)
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost(FeedSavedPlaceReconciliationContract.Route, async (
    HttpContext context,
    [FromBody] FeedSavedPlaceReconciliationRequestContract body,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedSavedPlaceReconciliationService reconciliation,
    IConfiguration configuration) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.jobs");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;

    FeedSavedPlaceReconciliationRequestContract normalized;
    try
    {
        normalized = reconciliation.Normalize(body);
    }
    catch (FeedSavedPlaceReconciliationException error)
    {
        return Error(context, error.StatusCode, error.Code, error.Message);
    }

    if (FeedSavedPlaceReconciliationService.IsMutation(normalized.Mode))
    {
        var mutationGate = RequireLegacyFavoriteReconciliationMutation(
            context,
            configuration,
            FeedSavedPlaceReconciliationService.IsDestructive(normalized.Mode));
        if (mutationGate is not null) return mutationGate;
    }

    try
    {
        return Results.Ok(await reconciliation.RunAsync(
            authorization.Session!,
            normalized,
            RequestId(context),
            context.RequestAborted));
    }
    catch (FeedSavedPlaceReconciliationIdempotencyConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "LEGACY_RECONCILIATION_IDEMPOTENCY_CONFLICT", error.Message);
    }
    catch (LegacyCustomerFavoritesSourceUnavailableException error)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "LEGACY_FAVORITES_SOURCE_UNAVAILABLE", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/go/feed/config/drafts", async (HttpContext context, FeedConfigDraftRequest body, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    try
    {
        var result = await feedConfig.CreateDraftAsync(
            authorization.Session!.UserId,
            body.Reason,
            RequestId(context),
            IdempotencyKey(context, body.IdempotencyKey),
            body.Config,
            context.RequestAborted);
        return Results.Json(result, statusCode: StatusCodes.Status201Created);
    }
    catch (FeedConfigRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (FeedConfigIdempotencyConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_IDEMPOTENCY_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/go/feed/config/drafts/{revisionId:guid}/validate", async (HttpContext context, Guid revisionId, FeedConfigActionRequest body, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    try
    {
        var result = await feedConfig.ValidateDraftAsync(
            authorization.Session!.UserId,
            revisionId,
            body.Reason,
            RequestId(context),
            IdempotencyKey(context, body.IdempotencyKey),
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (FeedConfigRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (FeedConfigNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "FEED_CONFIG_NOT_FOUND", error.Message);
    }
    catch (FeedConfigIdempotencyConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_IDEMPOTENCY_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/go/feed/config/drafts/{revisionId:guid}/publish", async (HttpContext context, Guid revisionId, FeedConfigPublishRequest body, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    try
    {
        return Results.Ok(await feedConfig.PublishAsync(
            authorization.Session!.UserId,
            revisionId,
            body.ExpectedActiveVersion,
            body.Reason,
            RequestId(context),
            IdempotencyKey(context, body.IdempotencyKey),
            context.RequestAborted));
    }
    catch (FeedConfigRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (FeedConfigNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "FEED_CONFIG_NOT_FOUND", error.Message);
    }
    catch (FeedConfigNotValidatedException error)
    {
        return Error(context, StatusCodes.Status422UnprocessableEntity, "FEED_CONFIG_NOT_VALIDATED", error.Message);
    }
    catch (FeedConfigVersionConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_VERSION_CONFLICT", error.Message, new { actualActiveVersion = error.ActualVersion });
    }
    catch (FeedConfigIdempotencyConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_IDEMPOTENCY_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/go/feed/config/revisions/{version:long}/rollback", async (HttpContext context, long version, FeedConfigRollbackRequest body, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.manage");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    try
    {
        return Results.Ok(await feedConfig.RollbackAsync(
            authorization.Session!.UserId,
            version,
            body.ExpectedActiveVersion,
            body.Reason,
            RequestId(context),
            IdempotencyKey(context, body.IdempotencyKey),
            context.RequestAborted));
    }
    catch (FeedConfigRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (FeedConfigNotFoundException error)
    {
        return Error(context, StatusCodes.Status404NotFound, "FEED_CONFIG_NOT_FOUND", error.Message);
    }
    catch (FeedConfigNotValidatedException error)
    {
        return Error(context, StatusCodes.Status422UnprocessableEntity, "FEED_CONFIG_NOT_VALIDATED", error.Message);
    }
    catch (FeedConfigVersionConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_VERSION_CONFLICT", error.Message, new { actualActiveVersion = error.ActualVersion });
    }
    catch (FeedConfigIdempotencyConflictException error)
    {
        return Error(context, StatusCodes.Status409Conflict, "FEED_CONFIG_IDEMPOTENCY_CONFLICT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/moderation", async (HttpContext context, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "content.moderate");
    if (authorization.Failure is not null) return authorization.Failure;

    var rawStatus = context.Request.Query["status"].ToString();
    var status = string.IsNullOrWhiteSpace(rawStatus) ? "OPEN" : rawStatus.Trim().ToUpperInvariant();
    var rawLimit = context.Request.Query["limit"].ToString();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var parsedLimit) || parsedLimit is < 1 or > 50))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_MODERATION_REQUEST", "limit must be between 1 and 50.");
    }
    var limit = string.IsNullOrWhiteSpace(rawLimit) ? 50 : int.Parse(rawLimit);
    DateTimeOffset? beforeCreatedAt = null;
    Guid? beforeReportId = null;
    var cursor = context.Request.Query["cursor"].ToString();
    if (!string.IsNullOrWhiteSpace(cursor))
    {
        if (!FeedModerationCursor.TryDecode(cursor, status, out var parsedCreatedAt, out var parsedReportId))
        {
            return Error(context, StatusCodes.Status400BadRequest, "MODERATION_CURSOR_INVALID", "Feed moderation cursor is invalid.");
        }
        beforeCreatedAt = parsedCreatedAt;
        beforeReportId = parsedReportId;
    }

    try
    {
        return Results.Ok(await database.ListFeedModerationQueueAsync(
            status,
            limit,
            beforeCreatedAt,
            beforeReportId,
            RequestId(context),
            context.RequestAborted));
    }
    catch (FeedModerationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/api/v1/admin/go/feed/moderation/{reportId:guid}/actions", async (
    HttpContext context,
    Guid reportId,
    FeedModerationActionRequest body,
    AppSessionReader sessions,
    CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "content.moderate");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;

    try
    {
        return Results.Ok(await database.ApplyFeedModerationActionAsync(
            authorization.Session!.UserId,
            authorization.Session.Id,
            reportId,
            body,
            IdempotencyKey(context, body.IdempotencyKey),
            RequestId(context),
            context.RequestAborted));
    }
    catch (FeedModerationRequestException error) when (error.Code is "REPORT_NOT_FOUND")
    {
        return Error(context, StatusCodes.Status404NotFound, error.Code, error.Message);
    }
    catch (FeedModerationRequestException error) when (error.Code is "MODERATION_VERSION_CONFLICT" or "IDEMPOTENCY_CONFLICT" or "IDEMPOTENCY_IN_PROGRESS")
    {
        return Error(context, StatusCodes.Status409Conflict, error.Code, error.Message);
    }
    catch (FeedModerationRequestException error) when (error.Code is "MODERATION_ACTION_UNSUPPORTED")
    {
        return Error(context, StatusCodes.Status422UnprocessableEntity, error.Code, error.Message);
    }
    catch (FeedModerationRequestException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/guardrails", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        return Results.Ok(await feedConfig.GetGuardrailsAsync(RequestId(context), context.RequestAborted));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/propagation", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedConfigService feedConfig) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.settings.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        return Results.Ok(await feedConfig.GetPropagationAsync(RequestId(context), context.RequestAborted));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/events/health", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedEventService feedEvents) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.analytics.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        return Results.Ok(await feedEvents.GetHealthAsync(RequestId(context), context.RequestAborted));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/discovery-evaluation", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedEventService feedEvents) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.analytics.read");
    if (authorization.Failure is not null) return authorization.Failure;

    var rawDays = context.Request.Query["days"].ToString();
    var days = string.IsNullOrWhiteSpace(rawDays) ? 30 : int.TryParse(rawDays, out var parsedDays) ? parsedDays : 0;
    if (days is < 1 or > 90)
    {
        return Error(context, StatusCodes.Status400BadRequest, "EVALUATION_WINDOW_INVALID", "The evaluation window must be between 1 and 90 days.");
    }

    try
    {
        return Results.Ok(await feedEvents.GetDiscoveryEvaluationAsync(days, RequestId(context), context.RequestAborted));
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapGet("/api/v1/admin/go/feed/projections/health", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, FeedCanonicalDataStore feedProjection) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.analytics.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var health = await feedProjection.GetFeedProjectionHealthAsync(context.RequestAborted);
        return Results.Ok(health is null
            ? new
            {
                projectionName = FeedServingProjection.Name,
                status = "not_built",
                readMode = FeedServingProjection.ToParameter(feedProjection.ProjectionReadMode),
                fresh = false,
                activeRunId = (string?)null,
                previousRunId = (string?)null,
                projectionVersion = (string?)null,
                sourceCutoffAt = (DateTimeOffset?)null,
                completedAt = (DateTimeOffset?)null,
                rowsPublished = 0,
                maxAgeSeconds = FeedServingProjection.DefaultMaxAgeSeconds,
                lastErrorCode = (string?)null
            }
            : new
            {
                projectionName = health.ProjectionName,
                status = health.Status ?? "not_built",
                readMode = FeedServingProjection.ToParameter(health.ReadMode),
                health.Fresh,
                health.ActiveRunId,
                health.PreviousRunId,
                health.ProjectionVersion,
                health.SourceCutoffAt,
                health.CompletedAt,
                health.RowsPublished,
                health.MaxAgeSeconds,
                health.LastErrorCode
            });
    }
    catch (FeedSourceDataException error)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, error.Code, "Feed projection health is unavailable.");
    }
    catch (Exception)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "FEED_PROJECTION_HEALTH_UNAVAILABLE", "Feed projection health is unavailable.");
    }
});

app.MapGet("/api/v1/admin/go/feed/sources/health", async (HttpContext context, AppSessionReader sessions, CoreDataStore database, IFeedDataPort feedData) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "go.analytics.read");
    if (authorization.Failure is not null) return authorization.Failure;
    try
    {
        var health = await feedData.CheckAsync(context.RequestAborted);
        return Results.Ok(new
        {
            source = health.Source,
            health.Available,
            checkedAt = health.CheckedAt ?? DateTimeOffset.UtcNow,
            health.FailureCode,
            sources = health.SourceHealth,
            requestId = RequestId(context)
        });
    }
    catch (Exception)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "FEED_SOURCE_HEALTH_UNAVAILABLE", "Feed source health is unavailable.");
    }
});

app.MapPatch("/api/v1/admin/applications/{code}", async (HttpContext context, string code, ApplicationStatusUpdateRequest body, AppSessionReader sessions, CoreDataStore database) =>
{
    var authorization = await RequireAdminAsync(context, sessions, database, "system.jobs");
    if (authorization.Failure is not null) return authorization.Failure;
    var csrfFailure = RequireCsrf(context, authorization.Session!);
    if (csrfFailure is not null) return csrfFailure;
    var application = code.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)) return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Unknown application code.");
    if (body.Reason.Trim().Length < 3) return Error(context, StatusCodes.Status400BadRequest, "REASON_REQUIRED", "An administrative reason is required.");
    try
    {
        await database.UpdateApplicationStatusAsync(authorization.Session!.UserId, RequestId(context), body.Reason.Trim(), application, body.Status.Trim().ToUpperInvariant(), context.RequestAborted);
        return Results.Ok(new { success = true });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/sessions", async (HttpContext context, InternalSessionIssueRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    var expectedSecret = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    var providedSecret = context.Request.Headers["x-aevo-accounts-secret"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(expectedSecret))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_ISSUER_NOT_CONFIGURED", "The Accounts session issuer secret is not configured.");
    }
    if (!SecureEquals(expectedSecret, providedSecret))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Accounts session issuer authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)) return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "Unknown application code.");
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        var absoluteLifetime = body.LifetimeSeconds ?? SessionLifetime(configuration, body.RememberMe);
        var idleLifetime = Math.Min(SessionIdleLifetime(configuration, body.RememberMe), absoluteLifetime);
        var issued = await database.CreateSessionAsync(
            body.UserId,
            body.Email,
            body.DisplayName,
            application,
            body.OrganizationId,
            body.StoreId,
            idleLifetime,
            absoluteLifetime,
            context.RequestAborted,
            body.RememberMe);
        return Results.Ok(new
        {
            sessionId = issued.SessionId,
            userId = issued.UserId,
            application = issued.AppCode,
            email = issued.Email,
            displayName = issued.DisplayName,
            sessionToken = issued.SessionToken,
            csrfToken = issued.CsrfToken,
            expiresAt = issued.ExpiresAt,
            absoluteExpiresAt = issued.AbsoluteExpiresAt,
            rememberMe = issued.RememberMe
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/sessions/revoke", async (HttpContext context, InternalSessionRevokeRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    var expectedSecret = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    var providedSecret = context.Request.Headers["x-aevo-accounts-secret"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(expectedSecret))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "SESSION_REVOKER_NOT_CONFIGURED", "The Accounts session revoker secret is not configured.");
    }
    if (!SecureEquals(expectedSecret, providedSecret))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Accounts session revoker authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)
        || string.IsNullOrWhiteSpace(body.SessionToken)
        || body.SessionToken.Length is < 40 or > 4096
        || !System.Text.RegularExpressions.Regex.IsMatch(body.SessionToken, "^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_SESSION", "A valid app-scoped session token and application are required.");
    }
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        await database.RevokeSessionAsync(body.SessionToken, application, context.RequestAborted);
        return Results.Ok(new { success = true, application });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/sessions/refresh", async (HttpContext context, InternalSessionRefreshRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-core-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Core API service authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)
        || string.IsNullOrWhiteSpace(body.SessionToken)
        || body.SessionToken.Length is < 40 or > 4096
        || !System.Text.RegularExpressions.Regex.IsMatch(body.SessionToken, "^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
        || string.IsNullOrWhiteSpace(body.CsrfToken))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_SESSION", "A valid app-scoped session and CSRF token are required.");
    }
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        var current = await database.ResolveSessionAsync(body.SessionToken, application, context.RequestAborted);
        if (current is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid or expired.");
        if (!CoreDataStore.VerifyCsrf(current, body.CsrfToken)) return Error(context, StatusCodes.Status403Forbidden, "CSRF_INVALID", "A valid CSRF token is required for this operation.");
        var issued = await database.RefreshSessionAsync(
            body.SessionToken,
            application,
            SessionIdleLifetime(configuration, current.RememberMe),
            SessionLifetime(configuration, current.RememberMe),
            context.RequestAborted);
        if (issued is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is no longer active.");
        return Results.Ok(new
        {
            sessionId = issued.SessionId,
            userId = issued.UserId,
            application = issued.AppCode,
            sessionToken = issued.SessionToken,
            csrfToken = issued.CsrfToken,
            expiresAt = issued.ExpiresAt,
            absoluteExpiresAt = issued.AbsoluteExpiresAt,
            rememberMe = issued.RememberMe
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/sessions/resolve", async (HttpContext context, InternalSessionResolveRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-core-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Core API service authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)
        || string.IsNullOrWhiteSpace(body.SessionToken)
        || body.SessionToken.Length is < 40 or > 4096
        || !System.Text.RegularExpressions.Regex.IsMatch(body.SessionToken, "^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_SESSION", "A valid app-scoped session token and application are required.");
    }
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        using var authorizationTiming = RequestPerformance.Measure(context, "authorization");
        var session = await database.ResolveSessionAsync(body.SessionToken, application, context.RequestAborted);
        if (session is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid or expired.");
        var tenantContext = TenantContextValidator.ValidateScope(session.OrganizationId, session.StoreId, body.OrganizationId, body.StoreId);
        if (!tenantContext.IsValid) return Error(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!);
        if (!string.IsNullOrWhiteSpace(body.CsrfToken) && !CoreDataStore.VerifyCsrf(session, body.CsrfToken))
        {
            return Error(context, StatusCodes.Status403Forbidden, "CSRF_INVALID", "A valid CSRF token is required for this operation.");
        }
        var touchInterval = SessionTouchInterval(configuration);
        var idleExpiresAt = session.LastSeenAt is null || DateTimeOffset.UtcNow >= session.LastSeenAt.Value.AddSeconds(touchInterval)
            ? await database.TouchSessionIfDueAsync(
                session.Id,
                SessionIdleLifetime(configuration, session.RememberMe),
                touchInterval,
                context.RequestAborted)
            : session.ExpiresAt;
        if (idleExpiresAt is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is no longer active.");
        if (session.LastSeenAt is null || DateTimeOffset.UtcNow >= session.LastSeenAt.Value.AddSeconds(touchInterval))
        {
            database.InvalidateSessionSnapshot(application, body.SessionToken);
        }
        var requestedOrganizationId = body.OrganizationId ?? session.OrganizationId;
        var requestedStoreId = body.StoreId ?? session.StoreId;
        var accessSnapshot = await database.ResolveAccessSnapshotAsync(
            session with { ExpiresAt = idleExpiresAt.Value },
            application,
            requestedOrganizationId,
            requestedStoreId,
            true,
            context.RequestAborted);
        var authorization = accessSnapshot.Authorization;
        if (authorization is null) return Error(context, StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.");
        var entitlement = accessSnapshot.Entitlement;
        var accessAllowed = entitlement is null || entitlement.Allowed;
        if (!accessAllowed)
        {
            return Error(context, StatusCodes.Status403Forbidden, entitlement!.Reason, "The application entitlement or store access is not active.");
        }
        var principal = new
        {
            userId = session.UserId,
            email = session.Email,
            displayName = session.DisplayName,
            organizationId = authorization.OrganizationId,
            membershipId = authorization.MembershipId,
            role = authorization.Role,
            permissions = authorization.Permissions
        };
        return Results.Ok(new
        {
            authenticated = true,
            user = new { id = session.UserId, email = session.Email, displayName = session.DisplayName },
            session = new { id = session.Id, application, expiresAt = idleExpiresAt.Value, absoluteExpiresAt = session.AbsoluteExpiresAt, organizationId = session.OrganizationId, storeId = requestedStoreId },
            principal,
            access = new
            {
                allowed = accessAllowed,
                application,
                reason = entitlement?.Reason ?? EntitlementReasonCodes.Allowed,
                permissions = authorization.Permissions,
                organizationId = authorization.OrganizationId,
                storeId = requestedStoreId,
                projectionVersion = entitlement?.ProjectionVersion,
                checkedAt = DateTimeOffset.UtcNow
            }
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/sync/manifest", async (HttpContext context, InternalSyncManifestRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-core-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Core API service authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application)
        || string.IsNullOrWhiteSpace(body.SessionToken)
        || body.SessionToken.Length is < 40 or > 4096
        || !System.Text.RegularExpressions.Regex.IsMatch(body.SessionToken, "^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_SESSION", "A valid app-scoped session token and application are required.");
    }
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        var session = await database.ResolveSessionAsync(body.SessionToken, application, context.RequestAborted);
        if (session is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid or expired.");
        var snapshot = await database.ResolveAccessSnapshotAsync(
            session,
            application,
            session.OrganizationId,
            session.StoreId,
            true,
            context.RequestAborted);
        if (snapshot.Authorization is null)
        {
            return Error(context, StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.");
        }
        if (snapshot.Entitlement is { Allowed: false } entitlement)
        {
            return Error(context, StatusCodes.Status403Forbidden, entitlement.Reason, "The application entitlement or store access is not active.");
        }

        var manifest = await database.GetSyncManifestAsync(session, application, context.RequestAborted);
        context.Response.Headers.ETag = manifest.ETag;
        context.Response.Headers.CacheControl = "private, no-cache";
        if (string.Equals(context.Request.Headers.IfNoneMatch.ToString().Trim(), manifest.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Ok(new
        {
            etag = manifest.ETag,
            generatedAt = manifest.GeneratedAt,
            application = manifest.Application,
            userId = manifest.UserId,
            organizationId = manifest.OrganizationId,
            storeId = manifest.StoreId,
            resources = manifest.Resources,
            deltas = manifest.Deltas
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/hub/subscriptions", async (HttpContext context, InternalHubSubscriptionsRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-core-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Core API service authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (application is not ("PLAY" or "POS" or "KIOSK" or "QUEUE")
        || string.IsNullOrWhiteSpace(body.SessionToken)
        || body.SessionToken.Length is < 40 or > 4096
        || !System.Text.RegularExpressions.Regex.IsMatch(body.SessionToken, "^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_SESSION", "A valid app-scoped session and application are required.");
    }
    if (!database.IsConfigured) return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured.");

    try
    {
        var session = await database.ResolveSessionAsync(body.SessionToken, application, context.RequestAborted);
        if (session is null) return Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid or expired.");
        var tenantContext = TenantContextValidator.ValidateScope(session.OrganizationId, session.StoreId, body.OrganizationId, body.StoreId);
        if (!tenantContext.IsValid) return Error(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!);
        var authorization = await database.ResolveApplicationAuthorizationAsync(session, application, body.OrganizationId, body.StoreId, context.RequestAborted);
        if (authorization is null) return Error(context, StatusCodes.Status403Forbidden, "APP_ASSIGNMENT_REQUIRED", "The current identity is not assigned to this application scope.");
        return Results.Ok(new
        {
            subscriptions = await database.GetHubSubscriptionsAsync(authorization.OrganizationId, body.StoreId ?? session.StoreId, context.RequestAborted),
            organizationId = authorization.OrganizationId,
            storeId = body.StoreId ?? session.StoreId
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/billing/webhooks", async (HttpContext context, InternalBillingWebhookRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-core-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Core API service authentication failed.");
    }
    try
    {
        var result = await database.ApplyBillingWebhookAsync(
            body.Provider,
            body.EventId,
            body.EventType,
            body.Payload,
            RequestId(context),
            context.RequestAborted);
        return Results.Ok(new
        {
            received = true,
            duplicate = result.Duplicate,
            processed = result.Status == "APPLIED",
            status = result.Status,
            organizationId = result.OrganizationId,
            errorCode = result.ErrorCode,
            projectionVersion = "billing-v1"
        });
    }
    catch (ArgumentException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_BILLING_EVENT", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/auth/handoffs/exchange", async (HttpContext context, InternalHandoffExchangeRequest body, CoreDataStore database, IConfiguration configuration) =>
{
    if (!InternalServiceAuthorized(context, configuration, "x-aevo-accounts-secret"))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The Accounts service authentication failed.");
    }

    var application = body.Application.Trim().ToUpperInvariant();
    if (!ApplicationCodes.All.Contains(application) || application is "HUB" or "ADMIN")
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_APPLICATION", "The handoff application is not supported.");
    }
    if (string.IsNullOrWhiteSpace(body.Code) || body.Code.Length is < 40 or > 4096)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_AUTHORIZATION_CODE", "The authorization code is invalid.");
    }
    try
    {
        var consumed = await database.ConsumeAuthorizationCodeAsync(body.Code, application, body.State, body.CodeVerifier, context.RequestAborted);
        if (consumed is null) return Error(context, StatusCodes.Status400BadRequest, "INVALID_AUTHORIZATION_CODE", "The authorization code is invalid, expired, or already used.");
        if (application is "PLAY" or "POS")
        {
            var handoffAuthorization = await database.ResolveHandoffAuthorizationAsync(consumed, context.RequestAborted);
            if (!handoffAuthorization.Allowed)
            {
                return Error(context, StatusCodes.Status403Forbidden, handoffAuthorization.Reason, "The application assignment or entitlement is no longer valid.");
            }
        }
        var identity = await database.GetIdentityUserAsync(consumed.UserId, context.RequestAborted);
        if (identity is null) return Error(context, StatusCodes.Status401Unauthorized, "IDENTITY_INACTIVE", "The source identity is no longer active.");
        var issued = await database.CreateSessionAsync(
            identity.Id,
            identity.Email,
            identity.DisplayName,
            application,
            consumed.OrganizationId,
            consumed.StoreId,
            SessionIdleLifetime(configuration, consumed.RememberMe),
            SessionLifetime(configuration, consumed.RememberMe),
            context.RequestAborted,
            consumed.RememberMe);
        return Results.Ok(new
        {
            userId = identity.Id,
            application,
            email = identity.Email,
            displayName = identity.DisplayName,
            session = new { sessionId = issued.SessionId, sessionToken = issued.SessionToken, csrfToken = issued.CsrfToken, expiresAt = issued.ExpiresAt, absoluteExpiresAt = issued.AbsoluteExpiresAt, rememberMe = issued.RememberMe },
            returnPath = consumed.ReturnPath,
            state = consumed.State
        });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/application-connections/probe", async (HttpContext context, ConnectionProbeUpdate probe, CoreDataStore database, IConfiguration configuration) =>
{
    var expectedToken = configuration["AEVO_CONNECTION_PROBE_TOKEN"]?.Trim();
    var providedToken = context.Request.Headers["x-aevo-worker-token"].ToString();
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "CONNECTION_PROBE_NOT_CONFIGURED", "The connection probe token is not configured.");
    }
    if (!SecureEquals(expectedToken, providedToken))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Connection probe authentication failed.");
    }
    var appCode = probe.AppCode.Trim().ToUpperInvariant();
    var environment = probe.Environment.Trim().ToLowerInvariant();
    var status = probe.Status.Trim().ToLowerInvariant();
    if (!ApplicationCodes.All.Contains(appCode) || environment.Length is < 1 or > 32 || status is not ("connected" or "degraded" or "not_connected" or "not_configured"))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_CONNECTION_PROBE", "The connection probe payload is invalid.");
    }
    try
    {
        await database.RecordConnectionProbeAsync(probe with { AppCode = appCode, Environment = environment, Status = status }, context.RequestAborted);
        return Results.Ok(new { success = true, appCode, environment, status });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/feed/events/consume", async (HttpContext context, InternalEventDeliveryRequest body, FeedEventService feedEvents, IConfiguration configuration) =>
{
    if (!FeedEventWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Feed event worker authentication failed.");
    }

    if (!string.Equals(body.EventType.Trim(), "FEED_EVENT_ACCEPTED", StringComparison.OrdinalIgnoreCase)
        || body.SchemaVersion.Trim() != "v1"
        || string.IsNullOrWhiteSpace(body.EventId)
        || body.EventId.Length > 200)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_EVENT_ENVELOPE", "The Feed event envelope is invalid.");
    }

    var consumer = context.Request.Query["consumer"].ToString();
    if (string.IsNullOrWhiteSpace(consumer)) consumer = "aevo-background-worker";
    var rawLimit = context.Request.Query["limit"].ToString();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var requestedLimit) || requestedLimit is < 1 or > 100))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_EVENT_CONSUMER", "limit must be between 1 and 100.");
    }

    try
    {
        return Results.Ok(await feedEvents.ConsumePendingAsync(
            consumer,
            string.IsNullOrWhiteSpace(rawLimit) ? 50 : int.Parse(rawLimit),
            context.RequestAborted,
            body.EventId));
    }
    catch (ArgumentException error)
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_EVENT_CONSUMER", error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/feed/events/replay-dead-letter", async (HttpContext context, FeedEventService feedEvents, IConfiguration configuration) =>
{
    if (!FeedEventWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Feed event worker authentication failed.");
    }

    var rawLimit = context.Request.Query["limit"].ToString();
    if (!string.IsNullOrWhiteSpace(rawLimit)
        && (!int.TryParse(rawLimit, out var requestedLimit) || requestedLimit is < 1 or > 100))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_EVENT_REPLAY", "limit must be between 1 and 100.");
    }

    try
    {
        var replayed = await feedEvents.ReplayDeadLettersAsync(
            string.IsNullOrWhiteSpace(rawLimit) ? 100 : int.Parse(rawLimit),
            context.RequestAborted);
        return Results.Ok(new { replayed, requestId = RequestId(context) });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/feed/events/retention", async (HttpContext context, FeedEventService feedEvents, FeedConfigService feedConfig, IConfiguration configuration) =>
{
    if (!FeedEventWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Feed event worker authentication failed.");
    }

    try
    {
        var runtime = await feedConfig.GetRuntimeSnapshotAsync(context.RequestAborted);
        var deleted = await feedEvents.PurgeExpiredAsync(runtime.Config.Analytics.RetentionDays, context.RequestAborted);
        return Results.Ok(new { deleted, retentionDays = runtime.Config.Analytics.RetentionDays, requestId = RequestId(context) });
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
});

app.MapPost("/internal/feed/projections/rebuild", async (HttpContext context, FeedCanonicalDataStore feedProjection, IConfiguration configuration) =>
{
    if (!FeedEventWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Feed event worker authentication failed.");
    }

    try
    {
        var result = await feedProjection.RebuildFeedServingProjectionAsync(context.RequestAborted);
        return Results.Ok(new
        {
            runId = result.RunId,
            projectionName = result.ProjectionName,
            projectionVersion = result.ProjectionVersion,
            status = result.Status,
            result.SourceCutoffAt,
            result.RowsPublished,
            result.CompletedAt,
            readMode = FeedServingProjection.ToParameter(result.ReadMode),
            requestId = RequestId(context)
        });
    }
    catch (FeedSourceDataException error)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, error.Code, "Feed projection rebuild is unavailable.");
    }
    catch (Exception)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "FEED_PROJECTION_REBUILD_FAILED", "Feed projection rebuild is unavailable.");
    }
});

app.MapPost("/internal/feed/projections/rollback", async (HttpContext context, FeedCanonicalDataStore feedProjection, IConfiguration configuration) =>
{
    if (!FeedEventWorkerAuthorized(context, configuration))
    {
        return Error(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Feed event worker authentication failed.");
    }

    try
    {
        var rolledBack = await feedProjection.RollbackFeedServingProjectionAsync(context.RequestAborted);
        return rolledBack
            ? Results.Ok(new { rolledBack = true, requestId = RequestId(context) })
            : Error(context, StatusCodes.Status409Conflict, "FEED_PROJECTION_ROLLBACK_UNAVAILABLE", "No previous completed feed projection is available for rollback.");
    }
    catch (FeedSourceDataException error)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, error.Code, "Feed projection rollback is unavailable.");
    }
    catch (Exception)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "FEED_PROJECTION_ROLLBACK_FAILED", "Feed projection rollback is unavailable.");
    }
});

app.MapGet("/v1/meta", (HttpContext context, IConfiguration configuration) =>
    Results.Ok(new
    {
        service = "aevo-core-api",
        contractVersion = HubApiContract.Version,
        contractRelease = HubApiContract.Release,
        hubRouteCount = HubApiContract.Routes.Count,
        environment = configuration["AEVO_ENVIRONMENT"] ?? "local",
        requestId = RequestId(context)
    }));

app.MapGet("/v1/hub/contract", (HttpContext context) =>
    Results.Ok(new HubContractMetadata(
        HubApiContract.Version,
        HubApiContract.Release,
        "aevo-core-api",
        HubApiContract.Routes.Count,
        HubApiContract.ImplementedRoutes.Count,
        HubApiContract.ImplementationStatus,
        HubApiContract.Routes)));

app.MapGet("/api/v1/hub/contract", (HttpContext context) =>
    Results.Ok(new HubContractMetadata(
        HubApiContract.Version,
        HubApiContract.Release,
        "aevo-core-api",
        HubApiContract.Routes.Count,
        HubApiContract.ImplementedRoutes.Count,
        HubApiContract.ImplementationStatus,
        HubApiContract.Routes)));

// Browser CSP reports are diagnostic input only. Do not persist or echo the
// report body; accepting it at Core also keeps the local Edge proxy from
// turning the report-only policy into a noisy 404.
app.MapPost("/api/security/csp-report", () => Results.NoContent());

app.MapHubApi();

app.Run();

static HealthResponse Health(HttpContext context, string status)
{
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    return new HealthResponse(
        status,
        "aevo-core-api",
        configuration["AEVO_BUILD_VERSION"] ?? "local",
        configuration["AEVO_ENVIRONMENT"] ?? "local",
        RequestId(context));
}

static IResult Error(HttpContext context, int statusCode, string code, string message, object? details = null)
{
    return Results.Json(
        new ApiErrorResponse(new ApiError(code, message, RequestId(context), details)),
        statusCode: statusCode,
        contentType: "application/json");
}

static async Task<(FeedPrincipal? Principal, IResult? Failure)> ResolveFeedPrincipalAsync(
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database)
{
    var goSessionCookie = sessions.ReadSessionCookie(context, FeedApiContract.ApplicationCode);
    if (goSessionCookie is not null)
    {
        var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
        if (authentication.Failure is not null) return (null, authentication.Failure);
        return (FeedPrincipalFactory.FromGoSession(authentication.Session!), null);
    }

    var anonymous = FeedAnonymousCookie.Resolve(
        context,
        IsSecureCookie(context.RequestServices.GetRequiredService<IConfiguration>()));
    if (anonymous.ShouldSetCookie && anonymous.SetCookieHeader is not null)
    {
        context.Response.Headers.Append("set-cookie", anonymous.SetCookieHeader);
    }
    return (anonymous.Principal, null);
}

static async Task<IResult> HandleFeedSavedPlaceMutationAsync(
    HttpContext context,
    Guid placeId,
    bool saved,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces)
{
    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_SAVE", "Saved Place actor, tenant, and session are resolved by Core.");
    }

    var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
    if (authentication.Failure is not null) return authentication.Failure;
    var session = authentication.Session!;
    var csrfFailure = RequireCsrf(context, session);
    if (csrfFailure is not null) return csrfFailure;

    var idempotencyKey = context.Request.Headers["idempotency-key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(idempotencyKey))
    {
        return Error(context, StatusCodes.Status400BadRequest, "IDEMPOTENCY_KEY_REQUIRED", "A canonical Place save idempotency key is required.");
    }

    var principal = FeedPrincipalFactory.FromGoSession(session);
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The saved Place rate limit has been reached.");
    }

    try
    {
        var result = await savedPlaces.SetAsync(
            principal,
            placeId,
            saved,
            idempotencyKey,
            RequestId(context),
            context.RequestAborted);
        return Results.Ok(result);
    }
    catch (FeedSavedPlaceIdempotencyConflictException)
    {
        return Error(context, StatusCodes.Status409Conflict, "IDEMPOTENCY_CONFLICT", "The canonical Place save idempotency key was already used for a different request.");
    }
    catch (FeedSavedPlaceNotPublicException)
    {
        return Error(context, StatusCodes.Status404NotFound, "PLACE_NOT_PUBLIC", "The canonical Place is not currently available for saving.");
    }
    catch (FeedSavedPlaceRequestException error)
    {
        return Error(context, error.StatusCode, error.Code, error.Message);
    }
    catch (CoreDatabaseException error)
    {
        return DatabaseError(context, error);
    }
}

static async Task<(IReadOnlySet<Guid>? PlaceIds, string? PrincipalBinding, IResult? Failure)> ResolvePlaceSavedFilterAsync(
    HttpContext context,
    bool savedOnly,
    AppSessionReader sessions,
    CoreDataStore database,
    FeedRateLimiter rateLimiter,
    FeedSavedPlaceService savedPlaces)
{
    if (!savedOnly) return (null, null, null);

    FeedFacadeService.ApplyNoStoreHeaders(context.Response);
    if (HasUntrustedFeedOverride(context))
    {
        return (
            null,
            null,
            Error(context, StatusCodes.Status400BadRequest, "INVALID_PLACE_SAVED_FILTER", "Saved Place actor, tenant, and session are resolved by Core."));
    }

    var authentication = await RequireSessionAsync(context, sessions, database, FeedApiContract.ApplicationCode);
    if (authentication.Failure is not null) return (null, null, authentication.Failure);

    var principal = FeedPrincipalFactory.FromGoSession(authentication.Session!);
    var rate = rateLimiter.Check(principal, DateTimeOffset.UtcNow);
    FeedFacadeService.ApplyRateLimitHeaders(context.Response, rate);
    if (!rate.Allowed)
    {
        return (
            null,
            null,
            Error(context, StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "The saved Place rate limit has been reached."));
    }

    var saved = await savedPlaces.ListAsync(principal, RequestId(context), context.RequestAborted);
    return (saved.SavedPlaces.Select(entry => entry.PlaceId).ToHashSet(), principal.CursorBinding, null);
}

static bool HasUntrustedFeedOverride(HttpContext context)
{
    string[] headers =
    [
        "x-user-id",
        "x-actor-id",
        "x-tenant-id",
        "x-organization-id",
        "x-store-id",
        "x-source",
        "x-rank",
        "x-config-version",
        "x-ranking-version",
        "x-sql"
    ];
    return headers.Any(header => context.Request.Headers.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value));
}

static async Task<(byte[] Body, bool TooLarge)> ReadRequestBodyAtMostAsync(
    HttpRequest request,
    int maximumBytes,
    CancellationToken cancellationToken)
{
    await using var body = new MemoryStream();
    var buffer = new byte[16 * 1024];
    while (true)
    {
        var read = await request.Body.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (read == 0) break;
        if (body.Length + read > maximumBytes) return (Array.Empty<byte>(), true);
        body.Write(buffer, 0, read);
    }

    return (body.ToArray(), false);
}

static IResult DatabaseError(HttpContext context, CoreDatabaseException error)
{
    return Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_DATABASE_UNAVAILABLE", error.Message);
}

static bool RequiresGatewaySignature(HttpContext context)
{
    if (!context.Request.Path.StartsWithSegments("/api")) return false;
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var explicitRequirement = string.Equals(configuration["AEVO_REQUIRE_GATEWAY_SIGNATURE"], "true", StringComparison.OrdinalIgnoreCase);
    var productionRequirement = string.Equals(configuration["AEVO_ENVIRONMENT"], "production", StringComparison.OrdinalIgnoreCase);
    return explicitRequirement || productionRequirement;
}

static async Task<IResult?> ValidateGatewaySignatureAsync(HttpContext context)
{
    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var secret = configuration["AEVO_ORIGIN_SIGNING_SECRET"]?.Trim();
    if (string.IsNullOrWhiteSpace(secret))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "GATEWAY_SIGNATURE_NOT_CONFIGURED", "The Core API gateway signature secret is not configured.");
    }

    var gateway = context.Request.Headers["x-aevo-gateway"].ToString().Trim();
    var trustedGateways = configuration["AEVO_TRUSTED_GATEWAYS"]?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
    if (string.IsNullOrWhiteSpace(gateway) || (trustedGateways.Length > 0 && !trustedGateways.Contains(gateway, StringComparer.Ordinal)))
    {
        return Error(context, StatusCodes.Status403Forbidden, "GATEWAY_IDENTITY_INVALID", "The gateway identity is not trusted.");
    }

    var timestampValue = context.Request.Headers["x-aevo-gateway-timestamp"].ToString();
    var signature = context.Request.Headers["x-aevo-gateway-signature"].ToString();
    var application = context.Request.Headers["x-aevo-app"].ToString().Trim().ToUpperInvariant();
    if (!long.TryParse(timestampValue, out var timestamp) || string.IsNullOrWhiteSpace(signature))
    {
        return Error(context, StatusCodes.Status403Forbidden, "GATEWAY_SIGNATURE_INVALID", "The gateway signature is missing or invalid.");
    }
    if (string.IsNullOrWhiteSpace(application))
    {
        return Error(context, StatusCodes.Status403Forbidden, "GATEWAY_APP_CONTEXT_INVALID", "The gateway application context is missing.");
    }
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 300)
    {
        return Error(context, StatusCodes.Status403Forbidden, "GATEWAY_SIGNATURE_EXPIRED", "The gateway signature has expired.");
    }

    if (context.Request.ContentLength is > 1_048_576)
    {
        return Error(context, StatusCodes.Status413PayloadTooLarge, "REQUEST_BODY_TOO_LARGE", "The gateway request body is too large.");
    }

    context.Request.EnableBuffering();
    await using var bodyBuffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(bodyBuffer, context.RequestAborted);
    context.Request.Body.Position = 0;
    var bodyHash = Base64Url(SHA256.HashData(bodyBuffer.ToArray()));
    var path = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
    var payload = string.Join('\n', timestampValue, context.Request.Method.ToUpperInvariant(), path, bodyHash, application);
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var expected = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var signatureBytes = Encoding.UTF8.GetBytes(signature);
    return expectedBytes.Length == signatureBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, signatureBytes)
        ? null
        : Error(context, StatusCodes.Status403Forbidden, "GATEWAY_SIGNATURE_INVALID", "The gateway signature is invalid.");
}

static string Base64Url(byte[] bytes)
{
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static bool SecureEquals(string expected, string provided)
{
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    return expectedBytes.Length == providedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

static bool FeedEventWorkerAuthorized(HttpContext context, IConfiguration configuration)
{
    var expected = configuration["AEVO_FEED_EVENT_WORKER_TOKEN"]?.Trim();
    var provided = context.Request.Headers["x-aevo-worker-token"].ToString().Trim();
    return !string.IsNullOrWhiteSpace(expected) && SecureEquals(expected, provided);
}

static bool PlaceProjectionReplayWorkerAuthorized(HttpContext context, IConfiguration configuration)
{
    var expected = configuration["AEVO_PLACE_PROJECTION_REPLAY_TOKEN"]?.Trim();
    var provided = context.Request.Headers["x-aevo-worker-token"].ToString().Trim();
    return !string.IsNullOrWhiteSpace(expected) && SecureEquals(expected, provided);
}

static bool InternalServiceAuthorized(HttpContext context, IConfiguration configuration, string headerName)
{
    var expected = headerName == "x-aevo-core-secret"
        ? configuration["AEVO_CORE_API_SERVICE_SECRET"]?.Trim()
        : configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    var provided = context.Request.Headers[headerName].ToString().Trim();
    return !string.IsNullOrWhiteSpace(expected) && SecureEquals(expected, provided);
}

static string? JsonString(JsonElement body, string name)
{
    return body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
}

static bool TryReadAdminDirectoryQuery(
    JsonElement body,
    out string? query,
    out int limit,
    out int offset,
    out string? error)
{
    query = null;
    limit = 50;
    offset = 0;
    error = null;
    if (body.ValueKind != JsonValueKind.Object)
    {
        error = "The query body must be an object.";
        return false;
    }
    if (!body.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionValue) || versionValue != 1)
    {
        error = "Only query specification version 1 is supported.";
        return false;
    }
    if (!body.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || !string.Equals(model.GetString(), "admin.directory", StringComparison.Ordinal))
    {
        error = "The requested query model is not available to Admin.";
        return false;
    }
    if (!body.TryGetProperty("where", out var where) || where.ValueKind != JsonValueKind.Object)
    {
        error = "A text search node is required.";
        return false;
    }
    if (!where.TryGetProperty("type", out var nodeType) || nodeType.ValueKind != JsonValueKind.String || !string.Equals(nodeType.GetString(), "text", StringComparison.Ordinal))
    {
        error = "Admin universal search accepts a text node only.";
        return false;
    }
    query = where.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()?.Trim()
        : null;
    if (string.IsNullOrWhiteSpace(query))
    {
        error = "Search text is required.";
        return false;
    }
    if (query.Length > 200)
    {
        error = "Search text must be 200 characters or fewer.";
        return false;
    }
    if (body.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
    {
        if (pagination.TryGetProperty("limit", out var requestedLimit) && requestedLimit.ValueKind == JsonValueKind.Number && requestedLimit.TryGetInt32(out var parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 50);
        }
        if (pagination.TryGetProperty("offset", out var requestedOffset) && requestedOffset.ValueKind == JsonValueKind.Number && requestedOffset.TryGetInt32(out var parsedOffset))
        {
            offset = Math.Max(parsedOffset, 0);
        }
    }
    return true;
}

static bool? JsonBoolean(JsonElement body, string name)
{
    return body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        ? value.GetBoolean()
        : null;
}

static string? SafeReturnPath(string? value)
{
    if (string.IsNullOrWhiteSpace(value)
        || !value.StartsWith('/')
        || value.StartsWith("//")
        || value.Contains('\\')
        || value.Any(character => character < 0x20 || character == 0x7f)) return null;
    try
    {
        var decoded = Uri.UnescapeDataString(value);
        return decoded.StartsWith('/') && !decoded.StartsWith("//") && !decoded.Contains('\\') ? value : null;
    }
    catch (UriFormatException)
    {
        return null;
    }
}

static int SessionLifetime(IConfiguration configuration, bool rememberMe = false)
{
    var setting = rememberMe
        ? "AEVO_REMEMBERED_SESSION_ABSOLUTE_TIMEOUT_SECONDS"
        : "AEVO_SESSION_ABSOLUTE_TIMEOUT_SECONDS";
    var defaultLifetime = rememberMe ? 60 * 60 * 24 * 30 : 60 * 60 * 8;
    return int.TryParse(configuration[setting], out var configured)
        ? Math.Clamp(configured, 60, 60 * 60 * 24 * 30)
        : defaultLifetime;
}

static int SessionIdleLifetime(IConfiguration configuration, bool rememberMe = false)
{
    var absoluteLifetime = SessionLifetime(configuration, rememberMe);
    var setting = rememberMe
        ? "AEVO_REMEMBERED_SESSION_IDLE_TIMEOUT_SECONDS"
        : "AEVO_SESSION_IDLE_TIMEOUT_SECONDS";
    var defaultLifetime = rememberMe ? 60 * 60 * 24 * 7 : 60 * 30;
    var configured = int.TryParse(configuration[setting], out var parsed)
        ? parsed
        : defaultLifetime;
    return Math.Clamp(configured, 60, absoluteLifetime);
}

static int SessionTouchInterval(IConfiguration configuration)
{
    var configured = int.TryParse(configuration["AEVO_SESSION_TOUCH_INTERVAL_SECONDS"], out var parsed)
        ? parsed
        : 30;
    return Math.Clamp(configured, 1, 600);
}

static bool IsSecureCookie(IConfiguration configuration)
{
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    return environment is "production" or "staging";
}

static string SessionCookie(string name, string value, string expiresAt, bool secure, bool persistent)
{
    var attributes = $"{name}={Uri.EscapeDataString(value)}; Path=/; HttpOnly; SameSite=Lax";
    if (!persistent) return $"{attributes}{(secure ? "; Secure" : string.Empty)}";
    var expires = DateTimeOffset.TryParse(expiresAt, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddHours(8);
    var maxAge = Math.Max(1, (int)Math.Floor((expires - DateTimeOffset.UtcNow).TotalSeconds));
    return $"{attributes}; Max-Age={maxAge}; Expires={expires.UtcDateTime:R}{(secure ? "; Secure" : string.Empty)}";
}

static string CsrfCookie(string name, string value, string expiresAt, bool secure, bool persistent)
{
    var attributes = $"{name}={Uri.EscapeDataString(value)}; Path=/; SameSite=Lax";
    if (!persistent) return $"{attributes}{(secure ? "; Secure" : string.Empty)}";
    var expires = DateTimeOffset.TryParse(expiresAt, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddHours(8);
    var maxAge = Math.Max(1, (int)Math.Floor((expires - DateTimeOffset.UtcNow).TotalSeconds));
    return $"{attributes}; Max-Age={maxAge}; Expires={expires.UtcDateTime:R}{(secure ? "; Secure" : string.Empty)}";
}

static string RecoveryVerifierCookieName() => "aevo_password_recovery_verifier";
static string RecoveryGrantCookieName() => "aevo_password_recovery_grant";
static string RecoveryCsrfCookieName() => "aevo_password_recovery_csrf";

static string RecoveryCookie(string name, string value, int maxAge, bool secure, bool httpOnly)
{
    return $"{name}={Uri.EscapeDataString(value)}; Path=/; SameSite=Lax; Max-Age={Math.Clamp(maxAge, 1, 900)}{(httpOnly ? "; HttpOnly" : string.Empty)}{(secure ? "; Secure" : string.Empty)}";
}

static string OpaqueRequestToken()
{
    return Base64Url(RandomNumberGenerator.GetBytes(32));
}

static string RecoveryRedirect(IConfiguration configuration, string state)
{
    var origin = configuration["AEVO_HUB_WEB_ORIGIN"]?.Trim().TrimEnd('/') ?? string.Empty;
    if (string.IsNullOrWhiteSpace(origin)) return "/modern/reset-password?auth_error=password_recovery_invalid";
    var target = new Uri(new Uri($"{origin}/"), "modern/reset-password");
    target = new UriBuilder(target) { Query = state == "ready" ? "recovery=ready" : $"auth_error={Uri.EscapeDataString(state)}" }.Uri;
    return target.ToString();
}

static string ClearCookie(string name, bool secure, bool httpOnly)
{
    return $"{name}=; Path=/; SameSite=Lax; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT{(httpOnly ? "; HttpOnly" : string.Empty)}{(secure ? "; Secure" : string.Empty)}";
}

static string RequestedApplication(HttpContext context, string? bodyApplication, string fallback)
{
    // The Edge-injected app context is authoritative. A browser-controlled
    // JSON field must not be able to turn a Hub login into an Admin session.
    var requested = context.Request.Headers["x-aevo-app"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(requested)) requested = bodyApplication?.Trim();
    return string.IsNullOrWhiteSpace(requested) ? fallback : requested.ToUpperInvariant();
}

static async Task<(CoreSession? Session, HubPrincipalRecord? Principal, IReadOnlyList<HubStoreRecord> Stores, IResult? Failure)> RequireHubBootstrapSessionAsync(
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database)
{
    var token = sessions.ReadSessionCookie(context, "HUB");
    if (token is null) return (null, null, Array.Empty<HubStoreRecord>(), Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "An app-scoped Hub session is required."));
    if (!database.IsConfigured) return (null, null, Array.Empty<HubStoreRecord>(), Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured."));

    try
    {
        using var authentication = RequestPerformance.Measure(context, "authentication");
        var bootstrap = await database.ResolveHubSessionBootstrapAsync(token, context.RequestAborted);
        if (bootstrap is null)
        {
            return (null, null, Array.Empty<HubStoreRecord>(), Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The Hub session is invalid or expired."));
        }

        var session = bootstrap.Session;
        var tenantContext = TenantContextValidator.ValidateHeaders(
            session.OrganizationId,
            session.StoreId,
            context.Request.Headers["x-tenant-id"].ToString(),
            context.Request.Headers["x-organization-id"].ToString(),
            context.Request.Headers["x-store-id"].ToString());
        if (!tenantContext.IsValid)
        {
            return (null, null, Array.Empty<HubStoreRecord>(), Error(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!));
        }

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var touchInterval = SessionTouchInterval(configuration);
        if (session.LastSeenAt is null || DateTimeOffset.UtcNow >= session.LastSeenAt.Value.AddSeconds(touchInterval))
        {
            var idleExpiresAt = await database.TouchSessionIfDueAsync(
                session.Id,
                SessionIdleLifetime(configuration, session.RememberMe),
                touchInterval,
                context.RequestAborted);
            if (idleExpiresAt is null)
            {
                return (null, null, Array.Empty<HubStoreRecord>(), Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is no longer active."));
            }
            database.InvalidateSessionSnapshot("HUB", token);
            session = session with { ExpiresAt = idleExpiresAt.Value, LastSeenAt = DateTimeOffset.UtcNow };
        }

        if (context.Request.Cookies.TryGetValue(sessions.CsrfCookieName(session.AppCode), out var csrfToken) && !string.IsNullOrWhiteSpace(csrfToken))
        {
            var secure = IsSecureCookie(configuration);
            var cookieExpiresAt = ((session.RememberMe ? session.AbsoluteExpiresAt : null) ?? session.ExpiresAt).ToString("O");
            context.Response.Headers.Append("set-cookie", SessionCookie(sessions.CookieName(session.AppCode), token, cookieExpiresAt, secure, session.RememberMe));
            context.Response.Headers.Append("set-cookie", CsrfCookie(sessions.CsrfCookieName(session.AppCode), csrfToken, cookieExpiresAt, secure, session.RememberMe));
        }

        return (session, bootstrap.Principal, bootstrap.Stores, null);
    }
    catch (CoreDatabaseException error)
    {
        return (null, null, Array.Empty<HubStoreRecord>(), DatabaseError(context, error));
    }
}

static async Task<(CoreSession? Session, IResult? Failure)> RequireSessionAsync(
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    string? applicationCode)
{
    var token = sessions.ReadSessionCookie(context, applicationCode);
    if (token is null) return (null, Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "An app-scoped session is required."));
    if (!database.IsConfigured) return (null, Error(context, StatusCodes.Status503ServiceUnavailable, "CORE_API_NOT_CONFIGURED", "The Core API session store is not configured."));
    try
    {
        var cacheKey = $"aevo.auth.session:{applicationCode?.Trim().ToUpperInvariant() ?? "*"}:{CoreDataStore.SessionHash(token)}";
        var session = RequestPerformance.MeasureCacheLookup(() => context.Items[cacheKey] as CoreSession);
        // The request-local value is only a second lookup within one request.
        // Leave MISS/HIT attribution to AevoMemoryCache so a memory-cache HIT
        // is not reported as MISS merely because this per-request slot was
        // empty on the first authorization check.
        if (session is not null) RequestPerformance.MarkCache("HIT");
        if (session is null)
        {
            using var authentication = RequestPerformance.Measure(context, "authentication");
            session = await database.ResolveSessionAsync(token, applicationCode, context.RequestAborted);
            if (session is not null)
            {
                var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                var touchInterval = SessionTouchInterval(configuration);
                if (session.LastSeenAt is null || DateTimeOffset.UtcNow >= session.LastSeenAt.Value.AddSeconds(touchInterval))
                {
                    var idleExpiresAt = await database.TouchSessionIfDueAsync(
                        session.Id,
                        SessionIdleLifetime(configuration, session.RememberMe),
                        touchInterval,
                        context.RequestAborted);
                    if (idleExpiresAt is null)
                    {
                        return (null, Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is no longer active."));
                    }
                    database.InvalidateSessionSnapshot(applicationCode, token);
                    session = session with { ExpiresAt = idleExpiresAt.Value, LastSeenAt = DateTimeOffset.UtcNow };
                }
                context.Items[cacheKey] = session;
            }
        }
        if (session is null)
        {
            return (null, Error(context, StatusCodes.Status401Unauthorized, "AUTHENTICATION_REQUIRED", "The app-scoped session is invalid, expired, revoked, or bound to another application."));
        }

        var tenantContext = TenantContextValidator.ValidateHeaders(
            session.OrganizationId,
            session.StoreId,
            context.Request.Headers["x-tenant-id"].ToString(),
            context.Request.Headers["x-organization-id"].ToString(),
            context.Request.Headers["x-store-id"].ToString());
        if (!tenantContext.IsValid)
        {
            return (null, Error(context, tenantContext.StatusCode, tenantContext.Code!, tenantContext.Message!));
        }

        if (context.Request.Cookies.TryGetValue(sessions.CsrfCookieName(session.AppCode), out var csrfToken) && !string.IsNullOrWhiteSpace(csrfToken))
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var secure = IsSecureCookie(configuration);
            var cookieExpiresAt = ((session.RememberMe ? session.AbsoluteExpiresAt : null) ?? session.ExpiresAt).ToString("O");
            context.Response.Headers.Append("set-cookie", SessionCookie(sessions.CookieName(session.AppCode), token, cookieExpiresAt, secure, session.RememberMe));
            context.Response.Headers.Append("set-cookie", CsrfCookie(sessions.CsrfCookieName(session.AppCode), csrfToken, cookieExpiresAt, secure, session.RememberMe));
        }

        return (session, null);
    }
    catch (CoreDatabaseException error)
    {
        return (null, DatabaseError(context, error));
    }
}

static async Task<(CoreSession? Session, IResult? Failure)> RequireAdminAsync(
    HttpContext context,
    AppSessionReader sessions,
    CoreDataStore database,
    string permission)
{
    var authentication = await RequireSessionAsync(context, sessions, database, "ADMIN");
    if (authentication.Failure is not null) return authentication;
    var session = authentication.Session!;
    if (!HasPlatformPermission(session.PlatformRole, permission))
    {
        return (null, Error(context, StatusCodes.Status403Forbidden, "PLATFORM_PERMISSION_REQUIRED", "A platform permission is required for this operation."));
    }
    return authentication;
}

static void ApplySecurityHeaders(HttpContext context)
{
    context.Response.Headers["x-content-type-options"] = "nosniff";
    context.Response.Headers["x-frame-options"] = "DENY";
    context.Response.Headers["referrer-policy"] = "no-referrer";
    context.Response.Headers["permissions-policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["cross-origin-resource-policy"] = "same-site";

    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    if (environment is "production" or "staging")
    {
        context.Response.Headers["strict-transport-security"] = "max-age=31536000; includeSubDomains";
    }

    var path = context.Request.Path;
    if (path.StartsWithSegments("/api/auth")
        || path.StartsWithSegments("/internal/auth")
        || path == "/v1/me"
        || path == "/v1/access")
    {
        context.Response.Headers["cache-control"] = "no-store";
    }
}

static IResult? ValidateBrowserOrigin(HttpContext context)
{
    var origin = context.Request.Headers.Origin.ToString().Trim().TrimEnd('/');
    if (string.IsNullOrWhiteSpace(origin)) return null;

    var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
    var allowedOrigins = configuration["AEVO_ALLOWED_ORIGINS"]?
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.TrimEnd('/'))
        .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri is not null && (uri.Scheme is "http" or "https"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? [];
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    if (allowedOrigins.Count == 0 && environment is ("production" or "staging"))
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "ORIGIN_POLICY_NOT_CONFIGURED", "The browser origin allowlist is not configured.");
    }
    return allowedOrigins.Count > 0 && !allowedOrigins.Contains(origin)
        ? Error(context, StatusCodes.Status403Forbidden, "ORIGIN_NOT_ALLOWED", "The request origin is not allowed.")
        : null;
}

static IResult? RequireCsrf(HttpContext context, CoreSession session)
{
    var originFailure = ValidateBrowserOrigin(context);
    if (originFailure is not null) return originFailure;
    return CoreDataStore.VerifyCsrf(session, context.Request.Headers["x-csrf-token"].ToString())
        ? null
        : Error(context, StatusCodes.Status403Forbidden, "CSRF_INVALID", "A valid CSRF token is required for this operation.");
}

static IResult? RequireLegacyHardDelete(HttpContext context, IConfiguration configuration)
{
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    var explicitlyEnabled = string.Equals(configuration["AEVO_ALLOW_LEGACY_HARD_DELETE"], "true", StringComparison.OrdinalIgnoreCase);
    return environment is "development" or "test" or "local" || explicitlyEnabled
        ? null
        : Error(context, StatusCodes.Status403Forbidden, "LEGACY_HARD_DELETE_DISABLED", "Legacy compatibility-row hard delete is disabled outside an explicitly approved test or maintenance environment.");
}

static IResult? RequireLegacyFavoriteReconciliationMutation(
    HttpContext context,
    IConfiguration configuration,
    bool destructive)
{
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    var enabled = string.Equals(configuration["AEVO_ALLOW_LEGACY_FAVORITE_RECONCILIATION"], "true", StringComparison.OrdinalIgnoreCase);
    if (environment is not ("development" or "test" or "local") && !enabled)
    {
        return Error(context, StatusCodes.Status403Forbidden, "LEGACY_RECONCILIATION_DISABLED", "Legacy Customer favorite reconciliation is disabled outside an explicitly approved test or maintenance environment.");
    }
    if (destructive)
    {
        var hardDeleteEnabled = string.Equals(configuration["AEVO_ALLOW_LEGACY_FAVORITE_HARD_DELETE"], "true", StringComparison.OrdinalIgnoreCase);
        if (environment is not ("development" or "test" or "local") && !hardDeleteEnabled)
        {
            return Error(context, StatusCodes.Status403Forbidden, "LEGACY_FAVORITE_HARD_DELETE_DISABLED", "Legacy Customer favorite deletion is disabled outside an explicitly approved test or maintenance environment.");
        }
    }
    return null;
}

static IResult? RequirePlaceProjectionReplay(HttpContext context, IConfiguration configuration)
{
    var environment = configuration["AEVO_ENVIRONMENT"]?.Trim().ToLowerInvariant();
    var explicitlyEnabled = string.Equals(configuration["AEVO_ALLOW_PLACE_PROJECTION_REPLAY"], "true", StringComparison.OrdinalIgnoreCase);
    return environment is "development" or "test" or "local" || explicitlyEnabled
        ? null
        : Error(context, StatusCodes.Status403Forbidden, "PLACE_PROJECTION_REPLAY_DISABLED", "Place projection replay is disabled outside an explicitly approved test or maintenance environment.");
}

static bool HasPlatformPermission(string? role, string permission) => PlatformPermissions(role).Contains(permission, StringComparer.Ordinal);

static IReadOnlyList<string> PlatformPermissions(string? role)
{
    return role?.Trim().ToUpperInvariant() switch
    {
        "SUPER_ADMIN" or "PLATFORM_OWNER" or "PLATFORM_ADMIN" =>
            new[] { "organization.read", "organization.manage", "organization.suspend", "organization.archive", "subscription.read", "subscription.manage", "entitlement.override", "user.impersonate", "user.manage", "plan.manage", "system.health", "system.jobs", "system.errors", "webhook.read", "webhook.replay", "audit.read", "content.moderate", "go.analytics.read", "go.settings.read", "go.settings.manage", "map.places.read", "map.places.manage" },
        "SUPPORT" or "PLATFORM_SUPPORT" => new[] { "organization.read", "subscription.read", "user.impersonate", "audit.read", "go.analytics.read", "map.places.read" },
        "OPS" or "PLATFORM_OPS" => new[] { "system.health", "system.jobs", "system.errors", "audit.read", "content.moderate", "go.analytics.read", "go.settings.read", "map.places.read" },
        "BILLING_ADMIN" => new[] { "subscription.read", "subscription.manage", "plan.manage", "audit.read" },
        "DEVELOPER" => new[] { "system.errors", "webhook.read", "audit.read", "go.analytics.read", "go.settings.read" },
        "AUDITOR" => new[] { "audit.read", "map.places.read" },
        _ => Array.Empty<string>()
    };
}

static AuditLogRecord ToAuditContract(AdminAuditRecord record)
{
    return new AuditLogRecord(
        record.Id.ToString(),
        record.ActorId.ToString(),
        record.ActorEmail,
        record.PlatformRole,
        record.Action,
        record.TargetType,
        record.TargetId,
        record.Reason,
        JsonObject(record.BeforeState),
        JsonObject(record.AfterState),
        record.RequestId,
        record.CreatedAt);
}

static IReadOnlyDictionary<string, object?>? JsonObject(JsonElement? value)
{
    if (value is null || value.Value.ValueKind != JsonValueKind.Object) return null;
    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var property in value.Value.EnumerateObject())
    {
        result[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
    }
    return result;
}

static string RequestId(HttpContext context)
{
    return context.Items["aevo.request_id"] as string ?? Guid.NewGuid().ToString("N");
}

static double PerformanceThreshold(string? value, double fallback)
{
    return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
        && parsed <= 60_000
        ? parsed
        : fallback;
}

static string IdempotencyKey(HttpContext context, string? bodyValue)
{
    var headerValue = context.Request.Headers["idempotency-key"].ToString().Trim();
    var value = string.IsNullOrWhiteSpace(headerValue) ? bodyValue?.Trim() : headerValue;
    if (string.IsNullOrWhiteSpace(value)) value = RequestId(context);
    return value[..Math.Min(value.Length, 128)];
}

static async Task<IResult> ForwardAccountsAuthAsync(HttpContext context, object body, string path, IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    var forwarded = await ForwardAccountsAuthPayloadAsync(context, body, path, httpClientFactory, configuration);
    if (forwarded.StatusCode == StatusCodes.Status503ServiceUnavailable)
    {
        return Error(context, StatusCodes.Status503ServiceUnavailable, "ACCOUNTS_UNAVAILABLE", "The Accounts authentication boundary is unavailable.");
    }
    if (forwarded.Payload.ValueKind == JsonValueKind.Object) return Results.Json(forwarded.Payload, statusCode: forwarded.StatusCode);
    return Error(context, forwarded.StatusCode, "AUTH_PROVIDER_INVALID_RESPONSE", "The Accounts authentication boundary returned an invalid response.");
}

static async Task<(int StatusCode, JsonElement Payload)> ForwardAccountsAuthPayloadAsync(
    HttpContext context,
    object body,
    string path,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    var accountsOrigin = configuration["AEVO_ACCOUNTS_API_ORIGIN"]?.Trim();
    var serviceSecret = configuration["AEVO_ACCOUNTS_SERVICE_SECRET"]?.Trim();
    if (string.IsNullOrWhiteSpace(accountsOrigin) || string.IsNullOrWhiteSpace(serviceSecret)) return (StatusCodes.Status503ServiceUnavailable, default);
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri($"{accountsOrigin.TrimEnd('/')}/"), path));
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("x-request-id", RequestId(context));
        request.Headers.TryAddWithoutValidation("x-aevo-accounts-secret", serviceSecret);
        request.Content = JsonContent.Create(body);
        using var externalApiTiming = RequestPerformance.Measure(context, "external_api");
        using var response = await httpClientFactory.CreateClient().SendAsync(request, context.RequestAborted);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted).ConfigureAwait(false);
        return ((int)response.StatusCode, payload);
    }
    catch (HttpRequestException)
    {
        return (StatusCodes.Status503ServiceUnavailable, default);
    }
}

#pragma warning disable CA1848
internal static class CoreApiLog
{
    public static void PerformanceCritical(
        ILogger logger,
        string RequestId,
        string Method,
        string Path,
        int StatusCode,
        double TotalMs,
        double AuthMs,
        double AuthzMs,
        double DbWallMs,
        double DbAggregateMs,
        double ExternalApiMs,
        double SerializationMs,
        double CacheLookupMs,
        int DbQueryCount,
        string CacheStatus,
        string DatabaseOperations)
    {
        logger.LogError(
            "api.performance request_id={RequestId} method={Method} path={Path} status={StatusCode} total_ms={TotalMs} auth_ms={AuthMs} authz_ms={AuthzMs} db_wall_ms={DbWallMs} db_aggregate_ms={DbAggregateMs} external_api_ms={ExternalApiMs} serialization_ms={SerializationMs} cache_lookup_ms={CacheLookupMs} db_query_count={DbQueryCount} cache_status={CacheStatus} db_ops={DatabaseOperations} threshold=critical",
            RequestId, Method, Path, StatusCode, TotalMs, AuthMs, AuthzMs, DbWallMs, DbAggregateMs, ExternalApiMs, SerializationMs, CacheLookupMs, DbQueryCount, CacheStatus, DatabaseOperations);
    }

    public static void PerformanceWarning(
        ILogger logger,
        string RequestId,
        string Method,
        string Path,
        int StatusCode,
        double TotalMs,
        double AuthMs,
        double AuthzMs,
        double DbWallMs,
        double DbAggregateMs,
        double ExternalApiMs,
        double SerializationMs,
        double CacheLookupMs,
        int DbQueryCount,
        string CacheStatus,
        string DatabaseOperations,
        string Threshold)
    {
        logger.LogWarning(
            "api.performance request_id={RequestId} method={Method} path={Path} status={StatusCode} total_ms={TotalMs} auth_ms={AuthMs} authz_ms={AuthzMs} db_wall_ms={DbWallMs} db_aggregate_ms={DbAggregateMs} external_api_ms={ExternalApiMs} serialization_ms={SerializationMs} cache_lookup_ms={CacheLookupMs} db_query_count={DbQueryCount} cache_status={CacheStatus} db_ops={DatabaseOperations} threshold={Threshold}",
            RequestId, Method, Path, StatusCode, TotalMs, AuthMs, AuthzMs, DbWallMs, DbAggregateMs, ExternalApiMs, SerializationMs, CacheLookupMs, DbQueryCount, CacheStatus, DatabaseOperations, Threshold);
    }
}
#pragma warning restore CA1848
