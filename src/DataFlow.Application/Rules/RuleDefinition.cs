using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
        var definition = ValidateDefinition(element);

        definition = definition with
        {
            KeyFields = definition.KeyFields.Order(StringComparer.Ordinal).ToArray(),
            Fields = definition.Fields.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f with
            {
                Header = (f.Header ?? f.Name).Trim(),
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

}
