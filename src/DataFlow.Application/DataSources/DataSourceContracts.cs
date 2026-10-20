using System.Text.Json.Serialization;

namespace DataFlow.Application.DataSources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveDataSourceRequest
{
    public string? Name { get; init; }
    public IReadOnlyList<string?>? AcceptedFormats { get; init; }
    public DataSourceSchemaRequest? Schema { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataSourceSchemaRequest
{
    public IReadOnlyList<string?>? KeyFields { get; init; }
    public string? CsvDelimiter { get; init; } = ",";
    public IReadOnlyList<DataSourceFieldRequest?>? Fields { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataSourceFieldRequest
{
    public string? Name { get; init; }
    public string? Type { get; init; }
    public bool? Required { get; init; }
    public int? MaxLength { get; init; }
    public string? Format { get; init; }
    public string? RegexPattern { get; init; }
}

public sealed record DataSourceDto(
    Guid Id,
    string Name,
    string DataType,
    IReadOnlyList<string> AcceptedFormats,
    DataSourceSchemaDto Schema,
    int SchemaVersion,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DataSourceSchemaDto(
    int Version,
    IReadOnlyList<string> KeyFields,
    string CsvDelimiter,
    IReadOnlyList<DataSourceFieldDto> Fields);

public sealed record DataSourceFieldDto(
    string Name,
    string Type,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxLength,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);
