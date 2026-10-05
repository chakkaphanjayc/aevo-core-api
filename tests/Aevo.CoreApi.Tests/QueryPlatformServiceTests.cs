using System.Text.Json;
using Aevo.CoreApi.Contracts;
using Aevo.CoreApi.QueryPlatform;
using Xunit;

namespace Aevo.CoreApi.Tests;

public sealed class QueryPlatformServiceTests
{
    private static readonly string[] DefaultSearchFields = ["code", "name"];
    private static readonly string[] RegisteredStoreFields = ["code", "name", "status", "createdAt"];
    private readonly QueryPlatformService service = new();

    [Fact]
    public void MetadataExposesOnlyTheRegisteredTenantScopedModelAndAllowlistedOperators()
    {
        var model = Assert.Single(service.ListModels());

        Assert.Equal("stores", model.TechnicalName);
        Assert.Equal("organization-and-membership-store", model.TenantScope);
        Assert.Equal("store.read", model.ReadPermission);
        Assert.Equal(DefaultSearchFields, model.DefaultSearchFields);
        Assert.Equal(RegisteredStoreFields, model.Fields.Select(field => field.Path));
        Assert.All(model.Fields, field => Assert.False(field.Capabilities.Import || field.Capabilities.Export));
        Assert.DoesNotContain("sql", model.Fields.Select(field => field.Path), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileMapsAllowlistedFieldsAndBindsValuesAsParameters()
    {
        const string userValue = "North' OR true --";
        var query = ValidQuery() with
        {
            Where = new QueryFilterNode
            {
                Type = "condition",
                Field = "name",
                Operator = "contains",
                Value = JsonValue(userValue)
            }
        };

        var plan = service.Compile(query);

        Assert.Contains("s.name", plan.WhereSql, StringComparison.Ordinal);
        Assert.DoesNotContain(userValue, plan.WhereSql, StringComparison.Ordinal);
        Assert.Equal(userValue, Assert.Single(plan.Parameters).Value);
        Assert.Equal("s.created_at ASC, s.id ASC", plan.OrderSql);
    }

    [Fact]
    public void CompileAddsAnAllowlistedTextSearchAcrossDefaultSearchFields()
    {
        var query = ValidQuery() with
        {
            Where = new QueryFilterNode { Type = "text", Value = JsonValue("North") }
        };

        var plan = service.Compile(query);

        Assert.Contains("s.code", plan.WhereSql, StringComparison.Ordinal);
        Assert.Contains("s.name", plan.WhereSql, StringComparison.Ordinal);
        Assert.Equal("North", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void CompileUsesStableOrderingForUserSuppliedSorts()
    {
        var query = ValidQuery() with
        {
            OrderBy = [new QueryOrderV1("name", "desc")]
        };

        var plan = service.Compile(query);

        Assert.Equal("s.name DESC, s.id ASC", plan.OrderSql);
    }

    [Fact]
    public void CompileRejectsUnsupportedAstVersion()
    {
        var error = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Version = 2 }));

        Assert.Equal("AST_VERSION_UNSUPPORTED", error.Code);
        Assert.Equal("query.version", error.Path);
    }

    [Fact]
    public void CompileRejectsUnregisteredModelsAndFields()
    {
        var modelError = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Model = "users" }));
        var fieldError = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Fields = ["organizationId"] }));

        Assert.Equal("MODEL_NOT_REGISTERED", modelError.Code);
        Assert.Equal("FIELD_NOT_ALLOWED", fieldError.Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("gt")]
    public void CompileRejectsOperatorsOutsideFieldMetadata(string operation)
    {
        var query = ValidQuery() with
        {
            Where = new QueryFilterNode
            {
                Type = "condition",
                Field = "name",
                Operator = operation,
                Value = JsonValue("North")
            }
        };

        var error = Assert.Throws<QueryPlatformContractException>(() => service.Compile(query));

        Assert.Equal("OPERATOR_NOT_ALLOWED", error.Code);
        Assert.Equal("query.where.operator", error.Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void CompileRejectsLimitsOutsideTheBoundedRange(int limit)
    {
        var error = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Pagination = new QueryPaginationV1(limit) }));

        Assert.Equal("QUERY_LIMIT_INVALID", error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void CompileAcceptsBothLimitBoundaries(int limit)
    {
        var plan = service.Compile(ValidQuery() with { Pagination = new QueryPaginationV1(limit) });

        Assert.Equal(limit, plan.Limit);
    }

    [Fact]
    public void CompileRejectsGroupingAndAggregatesUntilTheModelDeclaresThem()
    {
        var groupError = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { GroupBy = ["status"] }));
        var aggregateError = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Aggregates = [new QueryAggregate("count", null, "total")] }));

        Assert.Equal("QUERY_FEATURE_UNSUPPORTED", groupError.Code);
        Assert.Equal("QUERY_FEATURE_UNSUPPORTED", aggregateError.Code);
    }

    [Fact]
    public void CompileRejectsAstWithMoreThanSixtyFourNodes()
    {
        var leaf = new QueryFilterNode
        {
            Type = "condition",
            Field = "status",
            Operator = "eq",
            Value = JsonValue("ACTIVE")
        };
        var branch = new QueryFilterNode
        {
            Type = "and",
            Children = Enumerable.Repeat<QueryFilterNode?>(leaf, 16).ToArray()
        };
        var root = new QueryFilterNode
        {
            Type = "and",
            Children = Enumerable.Repeat<QueryFilterNode?>(branch, 16).ToArray()
        };

        var error = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Where = root }));

        Assert.Equal("AST_NODE_LIMIT_EXCEEDED", error.Code);
    }

    [Fact]
    public void CompileRejectsOffsetBeyondTheConfiguredBound()
    {
        var error = Assert.Throws<QueryPlatformContractException>(
            () => service.Compile(ValidQuery() with { Pagination = new QueryPaginationV1(20, 10_001) }));

        Assert.Equal("QUERY_OFFSET_INVALID", error.Code);
    }

    private static QueryAstV1 ValidQuery() => new()
    {
        Version = 1,
        Model = "stores",
        Fields = ["code", "name"],
        Pagination = new QueryPaginationV1(20)
    };

    private static JsonElement JsonValue(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }
}
