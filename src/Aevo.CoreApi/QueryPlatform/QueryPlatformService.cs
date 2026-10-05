using System.Globalization;
using System.Text.Json;
using Aevo.CoreApi.Contracts;

namespace Aevo.CoreApi.QueryPlatform;

internal enum QueryValueKind
{
    Text,
    Timestamp
}

internal sealed record QueryPlatformFieldDefinition(
    QueryModelFieldMetadata Metadata,
    string SqlExpression,
    QueryValueKind ValueKind,
    int MaxLength);

internal static class QueryPlatformModelCatalog
{
    private static readonly string[] TextOperators =
    [
        "eq", "neq", "contains", "not_contains", "starts_with", "ends_with",
        "in", "not_in", "is_empty", "is_not_empty"
    ];

    private static readonly string[] StatusOperators = ["eq", "neq", "in", "not_in"];
    private static readonly string[] TimestampOperators = ["eq", "gt", "gte", "lt", "lte", "between", "before", "after", "on"];

    private static readonly IReadOnlyList<QueryPlatformFieldDefinition> StoreFields =
    [
        Field("code", "Store code", "string", "s.code", QueryValueKind.Text, 64, true, TextOperators),
        Field("name", "Store name", "string", "s.name", QueryValueKind.Text, 160, true, TextOperators),
        Field("status", "Status", "string", "s.status", QueryValueKind.Text, 32, false, StatusOperators),
        Field("createdAt", "Created at", "dateTime", "s.created_at", QueryValueKind.Timestamp, 0, false, TimestampOperators)
    ];

    private static readonly QueryModelMetadata StoresMetadata = new(
        "stores",
        "Stores",
        "organization-and-membership-store",
        "store.read",
        ["code", "name"],
        [new QueryDefaultOrder("createdAt", "asc")],
        StoreFields.Select(field => field.Metadata).ToArray(),
        []);

    public static IReadOnlyList<QueryModelMetadata> Models { get; } = [StoresMetadata];

    public static QueryModelMetadata? FindModel(string technicalName) =>
        Models.FirstOrDefault(model => string.Equals(model.TechnicalName, technicalName, StringComparison.Ordinal));

    public static QueryPlatformFieldDefinition? FindField(string path) =>
        StoreFields.FirstOrDefault(field => string.Equals(field.Metadata.Path, path, StringComparison.Ordinal));

    public static QueryPlatformFieldDefinition GetField(string path) =>
        FindField(path) ?? throw new InvalidOperationException("The validated query references an unregistered field.");

    private static QueryPlatformFieldDefinition Field(
        string path,
        string label,
        string type,
        string sqlExpression,
        QueryValueKind valueKind,
        int maxLength,
        bool searchable,
        IReadOnlyList<string> operators)
    {
        var metadata = new QueryModelFieldMetadata(
            path,
            label,
            type,
            new QueryFieldCapabilities(searchable, true, true, false, false, false, false),
            operators,
            "store.read");
        return new QueryPlatformFieldDefinition(metadata, sqlExpression, valueKind, maxLength);
    }
}

public sealed class QueryPlatformService
{
    private const int MaximumNodes = QueryPlatformContract.MaxAstNodes;
    private const int MaximumDepth = 12;
    private const int MaximumFields = 16;
    private const int MaximumOrderFields = 4;
    private const int MaximumGroupChildren = 16;
    private const int MaximumListValues = 100;
    private const int MaximumLimit = QueryPlatformContract.MaxPageSize;
    private const int MaximumOffset = QueryPlatformContract.MaxOffset;

    public IReadOnlyList<QueryModelMetadata> ListModels() => QueryPlatformModelCatalog.Models;

    public QueryModelMetadata? FindModel(string technicalName) => QueryPlatformModelCatalog.FindModel(technicalName);

    public QueryPlan Compile(QueryAstV1? query)
    {
        if (query is null)
        {
            throw Invalid("QUERY_REQUIRED", "A Query AST v1 document is required.", "query");
        }

        if (query.Version != QueryPlatformContract.QueryVersion)
        {
            throw Invalid("AST_VERSION_UNSUPPORTED", "Only Query AST version 1 is supported.", "query.version");
        }

        var modelName = query.Model?.Trim();
        var model = string.IsNullOrEmpty(modelName) ? null : QueryPlatformModelCatalog.FindModel(modelName);
        if (model is null)
        {
            throw new QueryPlatformContractException(
                "MODEL_NOT_REGISTERED",
                "The requested model is not registered for query access.",
                "query.model",
                StatusCodes.Status404NotFound);
        }

        if (query.GroupBy is { Count: > 0 })
        {
            throw Invalid("QUERY_FEATURE_UNSUPPORTED", "Grouping is not supported by this model in Query AST v1 phase one.", "query.groupBy");
        }

        if (query.Aggregates is { Count: > 0 })
        {
            throw Invalid("QUERY_FEATURE_UNSUPPORTED", "Aggregates are not supported by this model in Query AST v1 phase one.", "query.aggregates");
        }

        var pagination = query.Pagination;
        if (pagination is null || pagination.Limit is < 1 or > MaximumLimit)
        {
            throw Invalid("QUERY_LIMIT_INVALID", "The query limit must be between 1 and 200.", "query.pagination.limit");
        }

        if (pagination.Offset is < 0 or > MaximumOffset)
        {
            throw Invalid("QUERY_OFFSET_INVALID", "The query offset must be between 0 and 10000.", "query.pagination.offset");
        }

        if (query.Fields is not { Count: > 0 and <= MaximumFields })
        {
            throw Invalid("QUERY_FIELDS_INVALID", "Between 1 and 16 fields must be selected.", "query.fields");
        }

        var selectedFields = new List<QueryModelFieldMetadata>(query.Fields.Count);
        var selectedPaths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < query.Fields.Count; index++)
        {
            var path = query.Fields[index]?.Trim();
            var field = path is null ? null : QueryPlatformModelCatalog.FindField(path);
            if (field is null)
            {
                throw Invalid("FIELD_NOT_ALLOWED", "A selected field is not available on this model.", $"query.fields[{index}]");
            }

            if (!selectedPaths.Add(field.Metadata.Path))
            {
                throw Invalid("QUERY_FIELDS_INVALID", "Selected fields must be unique.", $"query.fields[{index}]");
            }

            selectedFields.Add(field.Metadata);
        }

        var orderSql = CompileOrderBy(query.OrderBy, model);
        var state = new CompileState();
        var whereSql = query.Where is null ? "true" : CompileNode(query.Where, "query.where", 1, state, model);
        return new QueryPlan(
            model,
            selectedFields,
            whereSql,
            orderSql,
            state.Parameters,
            pagination.Limit,
            pagination.Offset);
    }

    private static string CompileNode(QueryFilterNode? node, string path, int depth, CompileState state, QueryModelMetadata model)
    {
        if (node is null)
        {
            throw Invalid("QUERY_NODE_INVALID", "Query groups cannot contain null nodes.", path);
        }

        state.NodeCount++;
        if (state.NodeCount > MaximumNodes)
        {
            throw Invalid("AST_NODE_LIMIT_EXCEEDED", "A query may contain at most 64 AST nodes.", path);
        }

        if (depth > MaximumDepth)
        {
            throw Invalid("AST_DEPTH_EXCEEDED", "The query AST is nested too deeply.", path);
        }

        return node.Type switch
        {
            "condition" => CompileCondition(node, path, state),
            "text" => CompileText(node, path, state, model),
            "and" => CompileGroup(node, path, depth, state, model, "AND"),
            "or" => CompileGroup(node, path, depth, state, model, "OR"),
            "not" => CompileNot(node, path, depth, state, model),
            _ => throw Invalid("QUERY_NODE_INVALID", "The query node type is not supported.", $"{path}.type")
        };
    }

    private static string CompileCondition(QueryFilterNode node, string path, CompileState state)
    {
        if (node.Children is not null || string.IsNullOrWhiteSpace(node.Field) || string.IsNullOrWhiteSpace(node.Operator))
        {
            throw Invalid("QUERY_NODE_INVALID", "A condition requires a field and operator and cannot contain children.", path);
        }

        var field = QueryPlatformModelCatalog.FindField(node.Field.Trim());
        if (field is null)
        {
            throw Invalid("FIELD_NOT_ALLOWED", "The filter field is not available on this model.", $"{path}.field");
        }

        var operation = node.Operator.Trim();
        if (!field.Metadata.Operators.Contains(operation, StringComparer.Ordinal))
        {
            throw Invalid("OPERATOR_NOT_ALLOWED", "The operator is not allowed for this field.", $"{path}.operator");
        }

        var sql = field.SqlExpression;
        if (operation is "is_empty" or "is_not_empty")
        {
            if (node.Value is not null || field.ValueKind != QueryValueKind.Text)
            {
                throw Invalid("VALUE_INVALID", "This operator does not accept a value.", $"{path}.value");
            }

            return operation == "is_empty"
                ? $"coalesce({sql}, '') = ''"
                : $"coalesce({sql}, '') <> ''";
        }

        if (operation is "in" or "not_in")
        {
            var values = ReadStringList(node.Value, path, field);
            var parameter = state.Add(values);
            return operation == "in" ? $"{sql} = any(@{parameter})" : $"{sql} <> all(@{parameter})";
        }

        if (operation == "between")
        {
            if (field.ValueKind != QueryValueKind.Timestamp)
            {
                throw Invalid("OPERATOR_NOT_ALLOWED", "The between operator is not available for this field.", $"{path}.operator");
            }

            var values = ReadTimestampRange(node.Value, path);
            var from = state.Add(values.Start);
            var to = state.Add(values.End);
            return $"({sql} >= @{from} and {sql} <= @{to})";
        }

        if (operation == "on")
        {
            var day = ReadDate(node.Value, path);
            var from = state.Add(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            var to = state.Add(new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            return $"({sql} >= @{from} and {sql} < @{to})";
        }

        var value = ReadScalar(node.Value, path, field);
        var parameterName = state.Add(value);
        return operation switch
        {
            "eq" => $"{sql} = @{parameterName}",
            "neq" => $"{sql} <> @{parameterName}",
            "contains" => $"position(lower(@{parameterName}) in lower({sql})) > 0",
            "not_contains" => $"position(lower(@{parameterName}) in lower({sql})) = 0",
            "starts_with" => $"left(lower({sql}), length(@{parameterName})) = lower(@{parameterName})",
            "ends_with" => $"right(lower({sql}), length(@{parameterName})) = lower(@{parameterName})",
            "gt" or "after" => $"{sql} > @{parameterName}",
            "gte" => $"{sql} >= @{parameterName}",
            "lt" or "before" => $"{sql} < @{parameterName}",
            "lte" => $"{sql} <= @{parameterName}",
            _ => throw Invalid("OPERATOR_NOT_ALLOWED", "The operator is not implemented for this field.", $"{path}.operator")
        };
    }

    private static string CompileText(QueryFilterNode node, string path, CompileState state, QueryModelMetadata model)
    {
        if (node.Field is not null || node.Operator is not null || node.Children is not null)
        {
            throw Invalid("QUERY_NODE_INVALID", "A text node accepts only a text value.", path);
        }

        var value = ReadTextValue(node.Value, path);
        var parameterName = state.Add(value);
        var expressions = model.DefaultSearchFields
            .Select(QueryPlatformModelCatalog.GetField)
            .Select(field => $"position(lower(@{parameterName}) in lower({field.SqlExpression})) > 0");
        return $"({string.Join(" or ", expressions)})";
    }

    private static string CompileGroup(
        QueryFilterNode node,
        string path,
        int depth,
        CompileState state,
        QueryModelMetadata model,
        string separator)
    {
        if (node.Field is not null || node.Operator is not null || node.Value is not null
            || node.Children is not { Count: > 0 and <= MaximumGroupChildren })
        {
            throw Invalid("QUERY_GROUP_INVALID", "A query group requires between 1 and 16 child nodes.", $"{path}.children");
        }

        var children = node.Children
            .Select((child, index) => CompileNode(child, $"{path}.children[{index}]", depth + 1, state, model));
        return $"({string.Join($" {separator} ", children)})";
    }

    private static string CompileNot(QueryFilterNode node, string path, int depth, CompileState state, QueryModelMetadata model)
    {
        if (node.Field is not null || node.Operator is not null || node.Value is not null
            || node.Children is not { Count: 1 })
        {
            throw Invalid("QUERY_GROUP_INVALID", "A not node requires exactly one child node.", $"{path}.children");
        }

        return $"not ({CompileNode(node.Children[0], $"{path}.children[0]", depth + 1, state, model)})";
    }

    private static string CompileOrderBy(IReadOnlyList<QueryOrderV1>? orderBy, QueryModelMetadata model)
    {
        if (orderBy is { Count: > MaximumOrderFields })
        {
            throw Invalid("QUERY_ORDER_INVALID", "A query may order by at most four fields.", "query.orderBy");
        }

        var clauses = new List<string>();
        var orderedFields = new HashSet<string>(StringComparer.Ordinal);
        if (orderBy is { Count: > 0 })
        {
            for (var index = 0; index < orderBy.Count; index++)
            {
                var item = orderBy[index];
                var field = item?.Field is null ? null : QueryPlatformModelCatalog.FindField(item.Field.Trim());
                if (field is null)
                {
                    throw Invalid("FIELD_NOT_ALLOWED", "The order field is not available on this model.", $"query.orderBy[{index}].field");
                }

                if (!orderedFields.Add(field.Metadata.Path))
                {
                    throw Invalid("QUERY_ORDER_INVALID", "Order fields must be unique.", $"query.orderBy[{index}].field");
                }

                var direction = item!.Direction?.Trim();
                if (direction is not ("asc" or "desc"))
                {
                    throw Invalid("QUERY_ORDER_INVALID", "Order direction must be asc or desc.", $"query.orderBy[{index}].direction");
                }

                clauses.Add($"{field.SqlExpression} {direction.ToUpperInvariant()}");
            }
        }
        else
        {
            foreach (var defaultOrder in model.DefaultOrder)
            {
                var field = QueryPlatformModelCatalog.GetField(defaultOrder.Field);
                orderedFields.Add(field.Metadata.Path);
                clauses.Add($"{field.SqlExpression} {defaultOrder.Direction.ToUpperInvariant()}");
            }
        }

        clauses.Add("s.id ASC");
        return string.Join(", ", clauses);
    }

    private static object ReadScalar(JsonElement? element, string path, QueryPlatformFieldDefinition field)
    {
        var value = RequiredValue(element, path);
        if (field.ValueKind == QueryValueKind.Timestamp)
        {
            if (value.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                throw Invalid("VALUE_INVALID", "A valid ISO date-time value is required.", $"{path}.value");
            }

            return timestamp;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("VALUE_INVALID", "A string value is required.", $"{path}.value");
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > field.MaxLength)
        {
            throw Invalid("VALUE_INVALID", "A non-empty string within the field length is required.", $"{path}.value");
        }

        return text;
    }

    private static string[] ReadStringList(JsonElement? element, string path, QueryPlatformFieldDefinition field)
    {
        var value = RequiredValue(element, path);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > MaximumListValues)
        {
            throw Invalid("VALUE_INVALID", "A list must contain between 1 and 100 values.", $"{path}.value");
        }

        return value.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw Invalid("VALUE_INVALID", "List values must be strings.", $"{path}.value");
                }

                var text = item.GetString()?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length > field.MaxLength)
                {
                    throw Invalid("VALUE_INVALID", "List values must be non-empty and within the field length.", $"{path}.value");
                }

                return text;
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ReadTimestampRange(JsonElement? element, string path)
    {
        var value = RequiredValue(element, path);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            throw Invalid("VALUE_INVALID", "The between operator requires exactly two date-time values.", $"{path}.value");
        }

        var values = value.EnumerateArray().ToArray();
        var start = ReadTimestampValue(values[0], path);
        var end = ReadTimestampValue(values[1], path);
        if (start > end)
        {
            throw Invalid("VALUE_INVALID", "The between start must not be after its end.", $"{path}.value");
        }

        return (start, end);
    }

    private static DateTimeOffset ReadTimestampValue(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw Invalid("VALUE_INVALID", "A valid ISO date-time value is required.", $"{path}.value");
        }

        return timestamp;
    }

    private static DateOnly ReadDate(JsonElement? element, string path)
    {
        var value = RequiredValue(element, path);
        if (value.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            throw Invalid("VALUE_INVALID", "The on operator requires a date in yyyy-MM-dd format.", $"{path}.value");
        }

        return day;
    }

    private static string ReadTextValue(JsonElement? element, string path)
    {
        var value = RequiredValue(element, path);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Length > 128)
        {
            throw Invalid("VALUE_INVALID", "Text search must contain between 1 and 128 characters.", $"{path}.value");
        }

        return text;
    }

    private static JsonElement RequiredValue(JsonElement? element, string path)
    {
        if (element is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value)
        {
            throw Invalid("VALUE_INVALID", "A filter value is required.", $"{path}.value");
        }

        return value;
    }

    private static QueryPlatformContractException Invalid(string code, string message, string path) =>
        new(code, message, path);

    private sealed class CompileState
    {
        private int parameterCount;

        public int NodeCount { get; set; }
        public List<QueryParameter> Parameters { get; } = [];

        public string Add(object value)
        {
            var name = $"query_value_{++parameterCount}";
            Parameters.Add(new QueryParameter(name, value));
            return name;
        }
    }
}
