using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Imports;

// One instance per job/file. Only keys are retained between rows.
public sealed class CustomerFileRowProcessor(Guid dataSourceId, DataSourceSchema schema, IEnumerable<string>? committedKeys = null)
{
    private readonly CustomerRowNormalizer _normalizer = new(schema);
    private readonly HashSet<string> _seenKeys = new(committedKeys ?? [], StringComparer.Ordinal);

    public CustomerRowValidation Process(ParsedRow row)
    {
        var result = _normalizer.Normalize(dataSourceId, row);
        if (result.NaturalKey is not null && !_seenKeys.Add(result.NaturalKey))
        {
            return result with
            {
                Customer = null,
                RowHash = null,
                Errors = Array.AsReadOnly<ImportRowError>([.. result.Errors,
                    new("$row", "duplicate_key_in_file", "The key already occurred in this file.")])
            };
        }

        return result;
    }
}
