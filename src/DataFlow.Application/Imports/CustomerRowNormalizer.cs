using System.Globalization;
using System.Net.Mail;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.Normalization;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Imports;

public sealed class CustomerRowNormalizer
{
    private readonly DataSourceSchema _schema;

    public CustomerRowNormalizer(DataSourceSchema schema)
    {
        var names = schema.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (new[] { "externalId", "name", "email", "isActive" }.Any(name =>
                !schema.Fields.Any(field => field.Name == name && field.Required)) ||
            schema.KeyFields.Any(field => !names.Contains(field)))
        {
            throw new DomainException("invalid_schema", "Unsupported Customer schema snapshot.");
        }

        // Snapshots originate from the source API. Reject unsupported snapshots
        // rather than silently coercing fields or executing a regex outside the MVP.
        foreach (var field in schema.Fields)
        {
            var expectedType = field.Name switch
            {
                "externalId" or "name" => "string",
                "email" => "email",
                "registeredAt" => "date",
                "isActive" => "boolean",
                _ => null
            };
            if (expectedType != field.Type || field.RegexPattern is not null ||
                (field.Type == "date" && field.Format is not (null or "yyyy-MM-dd")))
            {
                throw new DomainException("invalid_schema", "Unsupported Customer schema snapshot.");
            }
        }

        _schema = schema;
    }

    public CustomerRowValidation Normalize(Guid dataSourceId, ParsedRow row)
    {
        if (row.IsEmpty)
        {
            return new(null, null, null, [], true);
        }

        var errors = new List<ImportRowError>(row.Errors ?? []);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        DateOnly? registeredAt = null;
        var isActive = false;

        foreach (var field in _schema.Fields)
        {
            var value = row.Values.GetValueOrDefault(field.Name)?.Trim();
            value = string.IsNullOrEmpty(value) ? null : value;
            values.Add(field.Name, value);
            if (value is null)
            {
                if (field.Required || _schema.KeyFields.Contains(field.Name, StringComparer.Ordinal))
                {
                    errors.Add(new(field.Name, "required", "A value is required."));
                }

                continue;
            }

            if (field.MaxLength is int maximum && value.Length > maximum)
            {
                errors.Add(new(field.Name, "max_length", "The value exceeds the maximum length."));
            }

            switch (field.Type)
            {
                case "email":
                    if (!MailAddress.TryCreate(value, out var address) || address.Address != value ||
                        value.Contains('\r') || value.Contains('\n'))
                    {
                        errors.Add(new(field.Name, "invalid_email", "A valid email address is required."));
                    }

                    break;
                case "date":
                    if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        registeredAt = date;
                    }
                    else
                    {
                        errors.Add(new(field.Name, "invalid_date", "A valid date in yyyy-MM-dd format is required."));
                    }

                    break;
                case "boolean":
                    if (value is "true" or "false")
                    {
                        isActive = value == "true";
                    }
                    else
                    {
                        errors.Add(new(field.Name, "invalid_type", "A boolean value of true or false is required."));
                    }

                    break;
            }
        }

        var data = new NormalizedCustomerData(values.GetValueOrDefault("externalId") ?? "",
            values.GetValueOrDefault("name") ?? "", values.GetValueOrDefault("email") ?? "", registeredAt, isActive);
        // Retain a usable key even if another field is invalid: its first occurrence
        // must reserve the key for duplicate detection and eventual resume.
        string? naturalKey = null;
        if (_schema.KeyFields.All(field => values.GetValueOrDefault(field) is not null &&
            !errors.Any(error => error.Field == field && error.Code != "max_length")))
        {
            naturalKey = RowCanonicalizer.BuildNaturalKey(dataSourceId, _schema.KeyFields, data);
        }

        return errors.Count == 0
            ? new(data, naturalKey, RowCanonicalizer.ComputeSha256(RowCanonicalizer.Canonicalize(data)), [], false)
            : new(null, naturalKey, null, errors.AsReadOnly(), false);
    }
}

public sealed record CustomerRowValidation(
    NormalizedCustomerData? Customer,
    string? NaturalKey,
    string? RowHash,
    IReadOnlyList<ImportRowError> Errors,
    bool IsEmpty)
{
    public bool IsValid => !IsEmpty && Customer is not null && Errors.Count == 0;
}
