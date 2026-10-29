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
    IReadOnlyList<ImportRowError>? Errors = null,
    int? LastPhysicalRowNumber = null)
{
    // Multiline CSV records start at RowNumber and commit through their final
    // physical line. A resume checkpoint must never point inside a quoted cell.
    public int CheckpointRowNumber => LastPhysicalRowNumber ?? RowNumber;
}
