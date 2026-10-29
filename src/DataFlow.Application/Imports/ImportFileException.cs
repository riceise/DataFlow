namespace DataFlow.Application.Imports;

// Controlled file rejection; messages never contain cell contents or parser diagnostics.
public sealed class ImportFileException(string code, string message, int rowNumber) : Exception(message)
{
    public string Code { get; } = code;
    public int RowNumber { get; } = rowNumber;
}
