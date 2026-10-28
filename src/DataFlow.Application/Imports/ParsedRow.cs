using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Imports;

// Format-neutral parser output. RowNumber is physical; RawData is JSON preserving
// the original cells (including unmapped cells in a malformed record).
// Only the parser decides IsEmpty; an explicit record of empty cells is data.
public sealed record ParsedRow(
    int RowNumber,
    IReadOnlyDictionary<string, string?> Values,
    string RawData,
    bool IsEmpty = false,
    IReadOnlyList<ImportRowError>? Errors = null);
