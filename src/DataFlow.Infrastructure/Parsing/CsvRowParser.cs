using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class CsvRowParser(IOptions<ImportProcessingOptions> options) : ICsvRowParser
{
    public async IAsyncEnumerable<ParsedRow> ParseAsync(Stream stream, DataSourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limits = options.Value;
        Validator.ValidateObject(limits, new ValidationContext(limits), validateAllProperties: true);
        if (schema.CsvDelimiter.Length != 1 || schema.CsvDelimiter[0] is '"' or '\r' or '\n' or '\0')
        {
            throw new ImportFileException("invalid_csv_delimiter", "The CSV delimiter is not supported.", 1);
        }

        // Do not auto-detect other encodings. UTF-8 preamble is consumed, invalid
        // UTF-8 is rejected rather than silently replaced. The caller owns stream.
        using var text = new StreamReader(stream, new UTF8Encoding(true, true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var maxRawField = checked(2 * limits.MaxCellLength + 2); // RFC quotes + doubled quotes
        var maxRecord = checked((maxRawField + 1) * limits.MaxColumns + 2);
        using var reader = new RecordBoundedReader(text, maxRecord, cancellationToken);
        using var csv = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = schema.CsvDelimiter,
            DetectDelimiter = false,
            IgnoreBlankLines = false,
            AllowComments = false,
            TrimOptions = TrimOptions.None,
            CacheFields = false,
            MaxFieldSize = maxRawField,
            ExceptionMessagesContainRawData = false,
            BadDataFound = _ => throw new ImportFileException("invalid_csv_syntax", "The CSV record has invalid quoting.", reader.RowNumber)
        });

        var header = await ReadRecordAsync(csv, reader, limits, cancellationToken)
            ?? throw new ImportFileException("empty_file", "A CSV header is required.", 1);
        ValidateHeader(header.Cells, schema);
        var headers = header.Cells;
        var dataRows = 0;

        while (await ReadRecordAsync(csv, reader, limits, cancellationToken) is { } record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(record.RawRecord))
            {
                yield return new(record.StartRow, new Dictionary<string, string?>(), "[]", IsEmpty: true,
                    LastPhysicalRowNumber: record.EndRow);
                continue;
            }

            if (++dataRows > limits.MaxRows)
            {
                throw new ImportFileException("row_limit_exceeded", "The CSV file exceeds the row limit.", record.StartRow);
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var index = 0; index < Math.Min(headers.Length, record.Cells.Length); index++)
            {
                values.Add(headers[index], record.Cells[index]);
            }

            // An array preserves all cells, including extras and original whitespace.
            // No library exception/diagnostic is persisted in RawData or Errors.
            var errors = record.Cells.Length == headers.Length
                ? Array.Empty<ImportRowError>()
                : [new ImportRowError("$row", "column_count_mismatch", "The column count does not match the header.")];
            yield return new(record.StartRow, values, JsonSerializer.Serialize(record.Cells), Errors: errors,
                LastPhysicalRowNumber: record.EndRow);
        }
    }

    private static async Task<CsvRecord?> ReadRecordAsync(CsvParser csv, RecordBoundedReader reader,
        ImportProcessingOptions limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startRow = csv.RawRow + 1;
        reader.BeginRecord(csv.CharCount, startRow);
        try
        {
            if (!await csv.ReadAsync())
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (csv.Count > limits.MaxColumns)
            {
                throw new ImportFileException("column_limit_exceeded", "The CSV record exceeds the column limit.", startRow);
            }

            var cells = csv.Record ?? [];
            if (cells.Any(cell => cell.Length > limits.MaxCellLength))
            {
                throw new ImportFileException("cell_limit_exceeded", "The CSV record exceeds the cell length limit.", startRow);
            }

            return new(startRow, csv.RawRow, cells, csv.RawRecord);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportFileException("invalid_csv_encoding", "The CSV file must use valid UTF-8 encoding.", startRow);
        }
        catch (MaxFieldSizeException)
        {
            throw new ImportFileException("cell_limit_exceeded", "The CSV record exceeds the cell length limit.", startRow);
        }
        catch (CsvHelperException)
        {
            throw new ImportFileException("invalid_csv_syntax", "The CSV file could not be parsed.", startRow);
        }
    }

    private static void ValidateHeader(string[] headers, DataSourceSchema schema)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ImportFileException("empty_csv_header", "CSV column names must not be empty.", 1);
            }

            if (!seen.Add(name))
            {
                throw new ImportFileException("duplicate_csv_header", "CSV column names must be unique.", 1);
            }
        }

        var expected = schema.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (headers.Any(name => !expected.Contains(name)))
        {
            throw new ImportFileException("unknown_csv_header", "The CSV header contains an unknown column.", 1);
        }

        if (expected.Except(headers, StringComparer.Ordinal).Any())
        {
            throw new ImportFileException("missing_csv_header", "The CSV header is missing a schema column.", 1);
        }
    }

    private sealed record CsvRecord(int StartRow, int EndRow, string[] Cells, string RawRecord);
}
