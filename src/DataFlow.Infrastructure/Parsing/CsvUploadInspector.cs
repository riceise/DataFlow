using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using DataFlow.Application.Imports;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class CsvUploadInspector(IOptions<ImportProcessingOptions> options)
{
    public async Task<UploadManifestPart> InspectAsync(Stream stream, string sourceName, int partIndex,
        DataSourceSchema preliminarySchema, PreviewBudget budget, ImportPartType partType = ImportPartType.Csv,
        string sourceLocator = "file:single-file", CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var records = ReadRecordsAsync(stream, preliminarySchema.CsvDelimiter, settings, cancellationToken);
        RawCsvRecord? header = null;
        var headers = Array.Empty<string>();
        var rawPreview = new List<string>();
        var previewRows = new List<IReadOnlyList<string>>();
        var rawBytes = 0;
        var truncated = false;
        var previewStopped = false;
        await foreach (var record in records)
        {
            if (header is null)
            {
                header = record;
                headers = Array.ConvertAll(record.Cells, cell => cell.Trim());
                rawBytes = Utf8Length(record.RawRecord);
                if (rawBytes > settings.MaxPreviewRawBytesPerPart || rawBytes > budget.RawBytesRemaining)
                {
                    throw Error("preview_limit_exceeded", record.StartRow);
                }

                budget.RawBytesRemaining -= rawBytes;
                continue;
            }

            var blank = IsBlank(record, preliminarySchema.CsvDelimiter[0]);
            // MaxRows is enforced by Worker aggregate budget, not inspector.

            if (previewStopped || rawPreview.Count >= settings.PreviewRows || budget.RowsRemaining == 0)
            {
                truncated = true;
                previewStopped = true;
                continue;
            }

            var recordBytes = Utf8Length(record.RawRecord);
            if (rawBytes + recordBytes > settings.MaxPreviewRawBytesPerPart || recordBytes > budget.RawBytesRemaining)
            {
                truncated = true;
                previewStopped = true;
                continue;
            }

            rawBytes += recordBytes;
            budget.RawBytesRemaining -= recordBytes;
            budget.RowsRemaining--;
            rawPreview.Add(record.RawRecord);
            if (!blank)
            {
                previewRows.Add(record.Cells);
            }
        }

        if (header is null)
        {
            throw Error("empty_file", 1);
        }

        var headerCells = header.Cells.Select((value, index) => new UploadManifestCell(value, header.StartRow, index + 1)).ToArray();
        return new UploadManifestPart(partIndex, partType, sourceName, null, null, null, sourceLocator,
            header.RawRecord, rawPreview, headers, headerCells, previewRows, header.StartRow, header.EndRow, truncated);
    }

    public UploadManifestPart ReparsePreview(UploadManifestPart part, DataSourceSchema schema)
    {
        if (part.RawHeaderRecord is null)
        {
            TabularHeaders.Validate(part.Headers, schema, part.PartType == ImportPartType.XlsxWorksheet ? "XLSX" : "CSV");
            return part;
        }

        var raw = part.RawHeaderRecord + string.Concat(part.RawPreviewRecords);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw), writable: false);
        var records = ReadRecordsAsync(stream, schema.CsvDelimiter, options.Value, CancellationToken.None);
        return ReparseCore(part, schema, records, options.Value).GetAwaiter().GetResult();
    }

    private static async Task<UploadManifestPart> ReparseCore(UploadManifestPart part, DataSourceSchema schema,
        IAsyncEnumerable<RawCsvRecord> records, ImportProcessingOptions settings)
    {
        RawCsvRecord? header = null;
        var rows = new List<IReadOnlyList<string>>();
        var rawCount = 0;
        var truncated = part.PreviewTruncated;
        await foreach (var record in records)
        {
            if (header is null)
            {
                header = record;
                continue;
            }

            if (rawCount >= settings.PreviewRows)
            {
                truncated = true;
                break;
            }

            rawCount++;
            if (!IsBlank(record, schema.CsvDelimiter[0]))
            {
                rows.Add(record.Cells);
            }
        }

        if (header is null)
        {
            throw Error("empty_file", 1);
        }

        var headers = Array.ConvertAll(header.Cells, cell => cell.Trim());
        TabularHeaders.Validate(headers, schema, "CSV");
        return part with { Headers = headers, PreviewRows = rows, PreviewTruncated = truncated };
    }

    private static async IAsyncEnumerable<RawCsvRecord> ReadRecordsAsync(Stream stream, string delimiter,
        ImportProcessingOptions settings, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (delimiter.Length != 1 || delimiter[0] is '"' or '\r' or '\n' or '\0')
        {
            throw Error("invalid_csv_delimiter", 1);
        }

        using var text = new StreamReader(stream, new UTF8Encoding(true, true), false, 4096, leaveOpen: true);
        var maxRawField = checked(2 * settings.MaxCellLength + 2);
        var maxRecord = checked((maxRawField + 1) * settings.MaxColumns + 2);
        using var reader = new RecordBoundedReader(text, maxRecord, cancellationToken);
        using var csv = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            DetectDelimiter = false,
            IgnoreBlankLines = false,
            AllowComments = false,
            TrimOptions = TrimOptions.None,
            CacheFields = false,
            MaxFieldSize = maxRawField,
            ExceptionMessagesContainRawData = false,
            BadDataFound = _ => throw Error("invalid_csv_syntax", reader.RowNumber)
        });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = csv.RawRow + 1;
            reader.BeginRecord(csv.CharCount, start);
            RawCsvRecord? record;
            try
            {
                if (!await csv.ReadAsync())
                {
                    yield break;
                }

                if (csv.Count > settings.MaxColumns)
                {
                    throw Error("column_limit_exceeded", start);
                }

                var cells = csv.Record ?? [];
                if (cells.Any(cell => cell.Length > settings.MaxCellLength))
                {
                    throw Error("cell_limit_exceeded", start);
                }

                record = new(start, csv.RawRow, cells, csv.RawRecord);
            }
            catch (DecoderFallbackException)
            {
                throw Error("invalid_csv_encoding", start);
            }
            catch (MaxFieldSizeException)
            {
                throw Error("cell_limit_exceeded", start);
            }
            catch (CsvHelperException)
            {
                throw Error("invalid_csv_syntax", start);
            }
            catch (InvalidDataException)
            {
                throw Error("invalid_csv_syntax", start);
            }

            yield return record;
        }
    }

    private static bool IsBlank(RawCsvRecord record, char delimiter)
    {
        if (record.Cells.Length > 1)
        {
            return false;
        }

        var raw = record.RawRecord.AsSpan().TrimEnd(['\r', '\n']);
        return char.IsWhiteSpace(delimiter) ? raw.IsEmpty : raw.IsWhiteSpace();
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
    private static ImportFileException Error(string code, int row) => new(code, "CSV inspection failed.", row);
    private sealed record RawCsvRecord(int StartRow, int EndRow, string[] Cells, string RawRecord);
}

public sealed class PreviewBudget(int rawBytesRemaining)
{
    public int RowsRemaining { get; set; } = 200;
    public int RawBytesRemaining { get; set; } = rawBytesRemaining;
}
