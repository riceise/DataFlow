using DataFlow.Application.Imports;

namespace DataFlow.Infrastructure.Parsing;

// CsvHelper retains the current record, not just one field. Bound its input even
// for a record with millions of tiny columns or an unterminated quoted field.
// CharCount excludes lookahead, so each new record gets its own absolute budget.
internal sealed class RecordBoundedReader(TextReader inner, int maxRecordLength, CancellationToken cancellationToken) : TextReader
{
    private long _read;
    private long _limit;
    public int RowNumber { get; private set; }

    public void BeginRecord(long consumed, int rowNumber)
    {
        _limit = consumed + maxRecordLength;
        RowNumber = rowNumber;
    }

    public override async Task<int> ReadAsync(char[] buffer, int index, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_read >= _limit)
        {
            throw new ImportFileException("csv_record_too_large", "The CSV record exceeds the parsing limits.", RowNumber);
        }

        // CsvHelper's async API has no CancellationToken; propagate it into the
        // actual stream I/O instead of merely cancelling the awaiting caller.
        var read = await inner.ReadAsync(buffer.AsMemory(index, (int)Math.Min(count, _limit - _read)), cancellationToken);
        _read += read;
        return read;
    }
}
