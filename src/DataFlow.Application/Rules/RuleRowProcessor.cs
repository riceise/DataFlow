using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Rules;

public sealed class RuleRowProcessor
{
    private readonly Guid _sourceId;
    private readonly RuleDefinition _definition;
    private readonly Dictionary<string, Regex> _patterns;
    private readonly HashSet<string> _keys;
    private readonly CustomerRowNormalizer? _customer;

    public RuleRowProcessor(Guid sourceId, RuleDefinition definition, IEnumerable<string> committedKeys)
    {
        _sourceId = sourceId; _definition = definition;
        _keys = new(committedKeys, StringComparer.Ordinal);
        _patterns = definition.Fields.Where(f => f.RegexPattern is not null).ToDictionary(f => f.Name, f => RuleDefinitions.CompileRegex(f.RegexPattern!));
        if (definition.DataType == "Customer")
        {
            _customer = new(new DataSourceSchema(1, definition.KeyFields,
            definition.Fields.Select(f => new DataSourceFieldDefinition(f.Name, f.Type, f.Required, f.MaxLength, f.Format)).ToArray(), definition.Csv.Delimiter));
        }
    }

    public static DataSourceSchema ParserSchema(RuleDefinition definition) => new(1, [definition.Fields[0].Header ?? definition.Fields[0].Name],
        definition.Fields.Select(f => new DataSourceFieldDefinition(f.Header ?? f.Name, f.Type, f.Required, f.MaxLength, f.Format, logicalName: f.Name)).ToArray(),
        definition.Csv.Delimiter, definition.Columns.Unknown == "ignore");

    public CustomerRowValidation Process(ParsedRow row)
    {
        if (row.IsEmpty)
        {
            return new(null, null, null, [], true);
        }

        var errors = new List<ImportRowError>(row.Errors ?? []);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in _definition.Fields)
        {
            var raw = row.Values.GetValueOrDefault(field.Header ?? field.Name)?.Trim();
            var value = string.IsNullOrEmpty(raw) ? null : raw;
            values[field.Name] = value;
            void Error(string code, string message) => errors.Add(new(field.Name, code, message));
            if (value is null)
            {
                if ((field.Required || _definition.KeyFields.Contains(field.Name)) &&
                    !errors.Any(e => e.Field == field.Name || e.Field == (field.Header ?? field.Name)))
                {
                    Error("required", "A value is required.");
                }

                continue;
            }
            if (!RuleDefinitions.TryValue(field.Type, value, out var canonical))
            { Error("invalid_type", $"A valid {field.Type} value is required."); continue; }
            values[field.Name] = canonical;
            if (field.MinLength is { } min && value.Length < min)
            {
                Error("min_length", "The value is too short.");
            }

            if (field.MaxLength is { } max && value.Length > max)
            {
                Error("max_length", "The value is too long.");
            }

            if (field.Minimum is not null || field.Maximum is not null)
            {
                var number = decimal.Parse(canonical!, CultureInfo.InvariantCulture);
                if (number < field.Minimum || number > field.Maximum)
                {
                    Error("out_of_range", "The value is outside the allowed range.");
                }
            }
            if (field.AllowedValues is { } allowed && !allowed.Contains(canonical, StringComparer.Ordinal))
            {
                Error("not_allowed", "The value is not allowed.");
            }

            if (_patterns.TryGetValue(field.Name, out var regex))
            {
                try
                {
                    if (!regex.IsMatch(value))
                    {
                        Error("pattern_mismatch", "The value does not match the required pattern.");
                    }
                }
                catch (RegexMatchTimeoutException) { throw new ImportFileException("rule_execution_limit_exceeded", "Rule execution limit exceeded.", row.RowNumber); }
            }
        }
        if (_customer is not null)
        {
            var result = _customer.Normalize(_sourceId, row with { Values = values });
            // Customer normalization preserves the historical NaturalKey and RowHash contracts.
            result = result with { Errors = errors.Concat(result.Errors).Distinct().ToArray() };
            if (result.Errors.Count != 0)
            {
                result = result with { Customer = null, RowHash = null };
            }

            return Track(result);
        }
        string? key = null;
        if (_definition.KeyFields.Length > 0 && _definition.KeyFields.All(k => values.GetValueOrDefault(k) is not null &&
            !errors.Any(e => e.Field == k && e.Code is "invalid_type" or "required")))
        {
            var keyJson = JsonSerializer.Serialize(_definition.KeyFields.Select(k => new[] { k, values[k] }), RuleDefinitions.Json);
            key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyJson)));
        }
        return Track(new(null, key, null, errors, false, true));
    }

    private CustomerRowValidation Track(CustomerRowValidation result)
    {
        if (result.NaturalKey is not null && !_keys.Add(result.NaturalKey))
        {
            return result with { Customer = null, RowHash = null, Errors = [.. result.Errors, new("$row", "duplicate_key_in_file", "The key already occurred in this file.")] };
        }

        return result;
    }
}
