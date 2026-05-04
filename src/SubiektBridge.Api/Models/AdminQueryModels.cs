using System.Text.Json.Serialization;

namespace SubiektBridge.Api.Models;

public sealed record QueryRequestDto(
    [property: JsonPropertyName("sql")] string Sql,
    [property: JsonPropertyName("max_rows")] int? MaxRows = 100
);

public sealed record QueryResultDto(
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<object?>> Rows,
    [property: JsonPropertyName("truncated")] bool Truncated
);
