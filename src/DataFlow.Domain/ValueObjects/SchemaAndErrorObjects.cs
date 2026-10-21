using System.Text.Json;
using System.Text.Json.Serialization;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.ValueObjects;

public sealed record DataSourceFieldDefinition
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "email",
        "date",
        "boolean",
        "decimal",
        "integer"
    };

    [JsonConstructor]
    public DataSourceFieldDefinition(
        string name,
        string type,
        bool required,
        int? maxLength = null,
        string? format = null,
        string? regexPattern = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("invalid_schema_field_name", "Schema field name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(type) || !AllowedTypes.Contains(type.Trim()))
        {
            throw new DomainException("invalid_schema_field_type", $"Unsupported field type '{type}'.");
        }

        if (maxLength is <= 0)
        {
            throw new DomainException("invalid_schema_max_length", $"Field '{name}' maxLength must be positive.");
        }

        Name = name.Trim();
        Type = type.Trim().ToLowerInvariant();
        Required = required;
        MaxLength = maxLength;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim();
        RegexPattern = string.IsNullOrWhiteSpace(regexPattern) ? null : regexPattern;
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("required")]
    public bool Required { get; }

    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; }

    [JsonPropertyName("regexPattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegexPattern { get; }
}

public sealed record DataSourceSchema
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    [JsonConstructor]
    public DataSourceSchema(
        int version,
        IReadOnlyList<string> keyFields,
        IReadOnlyList<DataSourceFieldDefinition> fields,
        string csvDelimiter = ",")
    {
        if (version <= 0)
        {
            throw new DomainException("invalid_schema_version", "Schema version must be greater than zero.");
        }

        if (fields is null || fields.Count == 0)
        {
            throw new DomainException("invalid_schema_fields", "Schema must contain at least one field.");
        }

        if (keyFields is null || keyFields.Count == 0)
        {
            throw new DomainException("invalid_schema_key_fields", "Schema must contain at least one key field.");
        }

        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (field is null)
            {
                throw new DomainException("invalid_schema_field", "Schema field definition cannot be null.");
            }

            if (!fieldNames.Add(field.Name))
            {
                throw new DomainException("duplicate_schema_field", $"Duplicate field '{field.Name}' in schema.");
            }
        }

        var normalizedKeyFields = new List<string>(keyFields.Count);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyField in keyFields)
        {
            if (string.IsNullOrWhiteSpace(keyField))
            {
                throw new DomainException("invalid_schema_key_field", "Key field name cannot be empty.");
            }

            var trimmed = keyField.Trim();
            if (!seenKeys.Add(trimmed))
            {
                throw new DomainException("duplicate_schema_key_field", $"Duplicate key field '{trimmed}' in schema.");
            }

            if (!fieldNames.Contains(trimmed))
            {
                throw new DomainException("unknown_schema_key_field", $"Key field '{trimmed}' is not defined in fields.");
            }

            normalizedKeyFields.Add(trimmed);
        }

        if (string.IsNullOrEmpty(csvDelimiter) || csvDelimiter.Length != 1)
        {
            throw new DomainException("invalid_csv_delimiter", "CSV delimiter must be a single character.");
        }

        Version = version;
        KeyFields = normalizedKeyFields.AsReadOnly();
        Fields = fields.ToList().AsReadOnly();
        CsvDelimiter = csvDelimiter;
    }

    [JsonPropertyName("version")]
    public int Version { get; }

    [JsonPropertyName("keyFields")]
    public IReadOnlyList<string> KeyFields { get; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<DataSourceFieldDefinition> Fields { get; }

    [JsonPropertyName("csvDelimiter")]
    public string CsvDelimiter { get; }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public DataSourceSchema WithVersion(int version)
    {
        if (version <= 0)
        {
            throw new DomainException("invalid_schema_version", "Schema version must be greater than zero.");
        }

        return new DataSourceSchema(version, KeyFields, Fields, CsvDelimiter);
    }

    public static DataSourceSchema FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainException("invalid_schema_json", "Schema JSON cannot be empty.");
        }

        try
        {
            var schema = JsonSerializer.Deserialize<DataSourceSchema>(json, SerializerOptions);
            return schema ?? throw new DomainException("invalid_schema_json", "Schema JSON deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new DomainException("invalid_schema_json", $"Invalid schema JSON format: {ex.Message}");
        }
    }

    public bool Equals(DataSourceSchema? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Version == other.Version
            && CsvDelimiter == other.CsvDelimiter
            && KeyFields.SequenceEqual(other.KeyFields, StringComparer.Ordinal)
            && Fields.SequenceEqual(other.Fields);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(CsvDelimiter, StringComparer.Ordinal);

        foreach (var keyField in KeyFields)
        {
            hash.Add(keyField, StringComparer.Ordinal);
        }

        foreach (var field in Fields)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }
}

public sealed record ImportRowError
{
    [JsonConstructor]
    public ImportRowError(string field, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new DomainException("invalid_error_field", "Error field cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException("invalid_error_code", "Error code cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new DomainException("invalid_error_message", "Error message cannot be empty.");
        }

        Field = field.Trim();
        Code = code.Trim();
        Message = message.Trim();
    }

    [JsonPropertyName("field")]
    public string Field { get; }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }
}
