using System.Text.Json;

namespace Aevo.CoreApi.Contracts;

public static class QueryPlatformContract
{
    public const int QueryVersion = 1;
    public const int MaxAstNodes = 64;
    public const int MaxPageSize = 200;
    public const int MaxOffset = 10_000;
}

public sealed record QueryFieldCapabilities(
    bool Search,
    bool Filter,
    bool Sort,
    bool Group,
    bool Export,
    bool Import,
    bool Aggregate);

public sealed record QueryModelFieldMetadata(
    string Path,
    string Label,
    string Type,
    QueryFieldCapabilities Capabilities,
    IReadOnlyList<string> Operators,
    string? ReadPermission = null,
    string? WritePermission = null);

public sealed record QueryModelRelationMetadata(
    string Path,
    string TargetModel,
    string Cardinality);

public sealed record QueryDefaultOrder(string Field, string Direction);

public sealed record QueryModelMetadata(
    string TechnicalName,
    string Label,
    string TenantScope,
    string ReadPermission,
    IReadOnlyList<string> DefaultSearchFields,
    IReadOnlyList<QueryDefaultOrder> DefaultOrder,
    IReadOnlyList<QueryModelFieldMetadata> Fields,
    IReadOnlyList<QueryModelRelationMetadata> Relations);

public sealed record QueryModelsResponse(
    int Version,
    IReadOnlyList<QueryModelMetadata> Models);

public sealed record QueryModelResponse(
    int Version,
    QueryModelMetadata Model);

public sealed record QueryExecutionRequest(QueryAstV1? Query);

public sealed record QueryExportRequest(
    QueryAstV1? Query,
    IReadOnlyList<string>? SelectedFields,
    string? Format);

public sealed record QueryImportRequest(
    string? Model,
    string? Format);

public sealed record QueryAstV1
{
    public int Version { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
    public QueryFilterNode? Where { get; init; }
    public IReadOnlyList<QueryOrderV1>? OrderBy { get; init; }
    public QueryPaginationV1? Pagination { get; init; }
    public IReadOnlyList<string>? GroupBy { get; init; }
    public IReadOnlyList<QueryAggregate>? Aggregates { get; init; }
}

public sealed record QueryAggregate(string? Function, string? Field, string? Alias);

public sealed record QueryFilterNode
{
    public string? Type { get; init; }
    public string? Field { get; init; }
    public string? Operator { get; init; }
    public JsonElement? Value { get; init; }
    public IReadOnlyList<QueryFilterNode?>? Children { get; init; }
}

public sealed record QueryOrderV1(string? Field, string? Direction);

public sealed record QueryPaginationV1(int Limit, int Offset = 0);

public sealed record QueryColumn(string Path, string Label, string Type);

public sealed record QueryPage(int Limit, int Offset, int? NextOffset, bool HasMore);

public sealed record QueryExecutionResponse(
    int QueryVersion,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    QueryPage Page);

public sealed class QueryPlatformContractException(
    string code,
    string message,
    string path,
    int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
    public int StatusCode { get; } = statusCode;
}

public sealed record QueryPlatformEntitlement(
    bool Enabled,
    int? MaxRows,
    string Reason);

public sealed record QueryParameter(string Name, object Value);

public sealed record QueryPlan(
    QueryModelMetadata Model,
    IReadOnlyList<QueryModelFieldMetadata> Fields,
    string WhereSql,
    string OrderSql,
    IReadOnlyList<QueryParameter> Parameters,
    int Limit,
    int Offset);
