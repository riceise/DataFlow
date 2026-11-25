using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Infrastructure.Parsing;

public static class TabularHeaders
{
    public static void Validate(IReadOnlyList<string> headers, DataSourceSchema schema, string format)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ImportFileException("empty_csv_header", $"{format} column names must not be empty.", 1);
            }

            if (!seen.Add(name))
            {
                throw new ImportFileException("duplicate_csv_header", $"{format} column names must be unique.", 1);
            }
        }

        var expected = schema.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (!schema.AllowUnknownColumns && headers.Any(name => !expected.Contains(name)))
        {
            throw new ImportFileException("unknown_csv_header", $"The {format} header contains an unknown column.", 1);
        }

        if (expected.Except(headers, StringComparer.Ordinal).Any())
        {
            throw new ImportFileException("missing_csv_header", $"The {format} header is missing a schema column.", 1);
        }
    }
}
