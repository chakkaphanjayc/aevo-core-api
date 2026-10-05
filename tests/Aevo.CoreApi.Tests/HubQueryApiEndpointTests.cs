using System.Text.Json;
using Aevo.CoreApi;
using Aevo.CoreApi.Data;
using Aevo.CoreApi.QueryPlatform;
using Aevo.CoreApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class HubQueryApiEndpointTests
{
    [Theory]
    [InlineData("GET", "/api/v1/query/models")]
    [InlineData("GET", "/api/v1/query/models/stores")]
    [InlineData("POST", "/api/v1/query/execute")]
    public async Task QueryEndpointsRequireAnAppScopedHubSession(string method, string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<AppSessionReader>();
        builder.Services.AddSingleton<CoreDataStore>();
        builder.Services.AddSingleton<QueryPlatformService>();
        await using var app = builder.Build();
        app.MapHubQueryApi();
        await app.StartAsync();

        var endpointDataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var routePattern = path == "/api/v1/query/models/stores" ? "/api/v1/query/models/{technicalName}" : path;
        var route = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == routePattern
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.Ordinal) == true);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.Method = method;
        context.Request.Path = path;
        if (path == "/api/v1/query/models/stores") context.Request.RouteValues["technicalName"] = "stores";
        context.Response.Body = new MemoryStream();

        var requestDelegate = route.RequestDelegate ?? throw new InvalidOperationException("The test route has no request delegate.");
        await requestDelegate(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("AUTHENTICATION_REQUIRED", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(response.RootElement.GetProperty("error").TryGetProperty("requestId", out _));
    }
}
