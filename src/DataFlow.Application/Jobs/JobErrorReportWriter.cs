using System.Globalization;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Jobs;

public static class JobErrorReportWriter
{
    public const string HeaderLine = "rowNumber,field,code,message,rawData\r\n";

    public static async Task VerifyJobAccessAsync(
        IDataFlowDbContext database,
        ICurrentUser currentUser,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var exists = await database.ImportJobs.AsNoTracking().AnyAsync(
            x => x.Id == jobId && database.DataSources.Any(s => s.Id == x.DataSourceId && s.OwnerUserId == currentUser.UserId),
            cancellationToken);
        if (!exists)
        {
            throw new DomainException("job_not_found", "Job was not found.");
        }
    }

    public static async Task WriteErrorsCsvAsync(
        IDataFlowDbContext database,
        Guid jobId,
        Stream output,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16_384,
            leaveOpen: true);

        await writer.WriteAsync(HeaderLine.AsMemory(), cancellationToken);

        var lastRowNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await database.ImportRows.AsNoTracking()
                .Where(x => x.ImportJobId == jobId && x.Status == ImportRowStatus.Invalid && x.RowNumber > lastRowNumber)
                .OrderBy(x => x.RowNumber)
                .Take(500)
                .ToArrayAsync(cancellationToken);

            if (batch.Length == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastRowNumber = row.RowNumber;
                var sanitizedRawData = SanitizeRawDataJson(row.RawData);
                var errors = row.GetErrors();
                if (errors.Count == 0)
                {
                    await WriteRecordAsync(writer, row.RowNumber, "$row", "invalid_row", "The row is invalid.", sanitizedRawData, cancellationToken);
                    continue;
                }

                foreach (var error in errors)
                {
                    await WriteRecordAsync(writer, row.RowNumber, error.Field, error.Code, error.Message, sanitizedRawData, cancellationToken);
                }
            }

            await writer.FlushAsync(cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
    }

    public static string SanitizeFormulaValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (IsFormulaTrigger(value[0]))
        {
            return "'" + value;
        }

        var trimmed = value.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && IsFormulaTrigger(trimmed[0]))
        {
            return "'" + value;
        }

        return value;
    }

    public static string FormatCsvCell(string? value, bool sanitizeFormula = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = sanitizeFormula ? SanitizeFormulaValue(value) : value;
        var needsQuotes = false;
        for (var i = 0; i < safe.Length; i++)
        {
            var ch = safe[i];
            if (ch is ',' or '"' or '\r' or '\n')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes && (char.IsWhiteSpace(safe[0]) || char.IsWhiteSpace(safe[^1])))
        {
            needsQuotes = true;
        }

        if (!needsQuotes)
        {
            return safe;
        }

        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SanitizeRawDataJson(string? rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawData);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string?>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    items.Add(element.ValueKind == JsonValueKind.String
                        ? SanitizeFormulaValue(element.GetString())
                        : element.GetRawText());
                }

                return JsonSerializer.Serialize(items);
            }
        }
        catch (JsonException)
        {
            // Keep non-JSON raw text and sanitize it as a regular CSV cell.
        }

        return SanitizeFormulaValue(rawData);
    }

    private static async Task WriteRecordAsync(
        StreamWriter writer,
        int rowNumber,
        string field,
        string code,
        string message,
        string rawData,
        CancellationToken cancellationToken)
    {
        var line = string.Concat(
            rowNumber.ToString(CultureInfo.InvariantCulture),
            ",",
            FormatCsvCell(field),
            ",",
            FormatCsvCell(code),
            ",",
            FormatCsvCell(message),
            ",",
            FormatCsvCell(rawData),
            "\r\n");

        await writer.WriteAsync(line.AsMemory(), cancellationToken);
    }

    private static bool IsFormulaTrigger(char ch) =>
        ch is '=' or '+' or '-' or '@' or '\t' or '\r';
}
