using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Application.Rules;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleDefinition
{
    public int DefinitionFormatVersion { get; init; } = 1;
    public string DataType { get; init; } = "Tabular";
    public CsvDefinition Csv { get; init; } = new();
    public ColumnDefinition Columns { get; init; } = new();
    public string[] KeyFields { get; init; } = [];
    public RuleField[] Fields { get; init; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CsvDefinition { public string Delimiter { get; init; } = ","; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ColumnDefinition { public string Unknown { get; init; } = "reject"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleField
{
    public string Name { get; init; } = "";
    public string? Header { get; init; }
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public string[]? AllowedValues { get; init; }
    public string? RegexPattern { get; init; }
    public string? Format { get; init; }
}

public static partial class RuleDefinitions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 16 };

    public static (RuleDefinition Definition, string Canonical, string Hash) Parse(JsonElement element)
    {
        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > 256 * 1024)
        {
            throw Invalid("Definition exceeds 256 KiB.");
        }

        CheckProperties(element);
        RuleDefinition definition;
        try { definition = element.Deserialize<RuleDefinition>(Json) ?? throw Invalid("Definition is required."); }
        catch (JsonException) { throw Invalid("Invalid definition JSON or unknown property."); }
        if (definition.DefinitionFormatVersion != 1 || definition.DataType is not ("Customer" or "Tabular") ||
            definition.Fields is not { Length: > 0 and <= 64 } || definition.KeyFields is null || definition.Csv is null || definition.Columns is null)
        {
            throw Invalid("Unsupported format, data type or field count.");
        }

        if (definition.Csv.Delimiter is not { Length: 1 } || definition.Csv.Delimiter[0] is '"' or '\r' or '\n' or '\0' ||
            definition.Columns.Unknown is not ("reject" or "ignore"))
        {
            throw Invalid("Invalid CSV or column settings.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields)
        {
            if (field is null || field.Name is null || !Regex.IsMatch(field.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant) ||
                !names.Add(field.Name) || (field.Header ?? field.Name) is not { Length: > 0 and <= 200 } ||
                !headers.Add(field.Header ?? field.Name))
            {
                throw Invalid("Field names and headers must be valid and unique.");
            }

            if (field.Type is not ("string" or "email" or "integer" or "decimal" or "date" or "boolean"))
            {
                throw Invalid($"Unsupported type for {field.Name}.");
            }

            var text = field.Type is "string" or "email";
            var numeric = field.Type is "integer" or "decimal";
            if (field.MinLength is < 0 or > 65536 || field.MaxLength is <= 0 or > 65536 || field.MinLength > field.MaxLength ||
                (!text && (field.MinLength is not null || field.MaxLength is not null || field.RegexPattern is not null)) ||
                (!numeric && (field.Minimum is not null || field.Maximum is not null)) || field.Minimum > field.Maximum ||
                (field.Format is not null && (field.Type != "date" || field.Format != "yyyy-MM-dd")))
            {
                throw Invalid($"Invalid constraints for {field.Name}.");
            }

            if (field.AllowedValues is { } allowed && (allowed.Length is 0 or > 200 || allowed.Any(v => v is null || v.Length > 256 || v.Trim().Length == 0)))
            {
                throw Invalid($"Invalid allowed values for {field.Name}.");
            }

            if (field.AllowedValues is { } values)
            {
                foreach (var value in values)
                {
                    if (!TryValue(field.Type, value.Trim(), out _))
                    {
                        throw Invalid($"Invalid allowed value for {field.Name}.");
                    }
                }
            }

            if (field.RegexPattern is { } pattern)
            {
                if (pattern.Length > 256)
                {
                    throw Invalid("Regex exceeds 256 characters.");
                }

                try { _ = CompileRegex(pattern); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw Invalid($"Unsupported regex for {field.Name}."); }
            }
        }
        if (definition.KeyFields.Distinct(StringComparer.Ordinal).Count() != definition.KeyFields.Length ||
            definition.KeyFields.Any(key => !names.Contains(key)))
        {
            throw Invalid("Invalid key fields.");
        }

        if (definition.DataType == "Customer")
        {
            ValidateCustomer(definition);
        }

        definition = definition with
        {
            KeyFields = definition.KeyFields.Order(StringComparer.Ordinal).ToArray(),
            Fields = definition.Fields.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f with
            {
                Header = f.Header ?? f.Name,
                Format = f.Type == "date" ? "yyyy-MM-dd" : null,
                AllowedValues = f.AllowedValues?.Select(v => { TryValue(f.Type, v.Trim(), out var canonical); return canonical!; })
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            }).ToArray()
        };
        var canonicalJson = JsonSerializer.Serialize(definition, Json);
        return (definition, canonicalJson, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))));
    }

    public static Regex CompileRegex(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    public static bool TryValue(string type, string value, out string? canonical)
    {
        canonical = value;
        switch (type)
        {
            case "integer":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    return false;
                }

                canonical = integer.ToString(CultureInfo.InvariantCulture); return true;
            case "decimal":
                if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    return false;
                }

                canonical = number.ToString("G29", CultureInfo.InvariantCulture); return true;
            case "date": return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            case "boolean": return value is "true" or "false";
            case "email": return System.Net.Mail.MailAddress.TryCreate(value, out var address) && address.Address == value && !value.Contains('\r') && !value.Contains('\n');
            default: return true;
        }
    }

    private static void ValidateCustomer(RuleDefinition definition)
    {
        if (!definition.KeyFields.SequenceEqual(["externalId"]))
        {
            throw Invalid("Customer key must be externalId.");
        }

        foreach (var name in new[] { "externalId", "name", "email", "isActive" })
        {
            if (!definition.Fields.Any(f => f.Name == name && f.Required))
            {
                throw Invalid($"Customer requires {name}.");
            }
        }

        foreach (var f in definition.Fields)
        {
            var type = f.Name switch { "externalId" or "name" => "string", "email" => "email", "registeredAt" => "date", "isActive" => "boolean", _ => "" };
            var max = f.Name switch { "externalId" => 80, "name" => 200, "email" => 254, _ => 0 };
            if (f.Type != type || (max > 0 && (f.MaxLength is null || f.MaxLength > max)))
            {
                throw Invalid($"Invalid Customer field {f.Name}.");
            }
        }
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) { throw Invalid("Duplicate JSON property."); } CheckProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                CheckProperties(value);
            }
        }
    }

    private static DomainException Invalid(string message) => new("invalid_rule_definition", message);
}
