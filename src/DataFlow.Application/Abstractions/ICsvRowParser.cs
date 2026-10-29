using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Abstractions;

public interface ICsvRowParser
{
    IAsyncEnumerable<ParsedRow> ParseAsync(Stream stream, DataSourceSchema schema, CancellationToken cancellationToken = default);
}
