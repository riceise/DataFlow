using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using DataFlow.Application.Imports;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class XlsxUploadInspector(IOptions<ImportProcessingOptions> options)
{
    public async Task<UploadManifestInspection> InspectAsync(Stream stream, string sourceName,
        PreviewBudget previewBudget, CancellationToken cancellationToken)
    {
        try
        {
            return await InspectCoreAsync(stream, sourceName, previewBudget, cancellationToken);
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or
            OverflowException or XmlException)
        {
            throw Error("invalid_xlsx_archive");
        }
    }

    private async Task<UploadManifestInspection> InspectCoreAsync(Stream stream, string sourceName,
        PreviewBudget previewBudget, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || stream.Length == 0)
        {
            throw Error("empty_file");
        }

        ZipArchive archive;
        try { archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw Error("invalid_xlsx_archive"); }

        using (archive)
        {
            var settings = options.Value;
            XlsxRowParser.ValidateZipLimits(archive, settings);
            var budget = new XlsxRowParser.UncompressedBudget(settings.MaxUncompressedBytes);
            var (sheets, date1904) = await ReadWorkbookAsync(archive, budget, cancellationToken);
            if (sheets.Count == 0)
            {
                throw Error("empty_file");
            }

            if (sheets.Count > Math.Min(settings.MaxSheetsPerWorkbook, settings.MaxPartsPerJob))
            {
                throw Error("xlsx_sheet_limit_exceeded");
            }

            var dateStyles = await XlsxRowParser.ReadDateStylesAsync(archive, budget, cancellationToken);
            var sharedStrings = await XlsxRowParser.ReadSharedStringsAsync(archive, budget, settings.MaxCellLength,
                cancellationToken, settings.Xlsx);
            var parts = new List<UploadManifestPart>(sheets.Count);
            for (var index = 0; index < sheets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheet = sheets[index];
                var entry = XlsxRowParser.FindEntry(archive, sheet.Locator)
                    ?? throw Error("invalid_xlsx_archive");
                parts.Add(await InspectSheetAsync(archive, entry, sourceName, sheet, index, settings,
                    sharedStrings, dateStyles, date1904, budget, previewBudget, cancellationToken));
            }

            var metrics = new UploadContainerMetrics(stream.Length, archive.Entries.Count,
                archive.Entries.Sum(entry => entry.Length), archive.Entries.Count == 0 ? 0 : archive.Entries.Max(entry => entry.Length));
            return new UploadManifestInspection(parts, metrics);
        }
    }

    private static async Task<UploadManifestPart> InspectSheetAsync(ZipArchive archive, ZipArchiveEntry entry,
        string sourceName, WorkbookSheet sheet, int index, ImportProcessingOptions settings, List<string> sharedStrings,
        HashSet<int> dateStyles, bool date1904, XlsxRowParser.UncompressedBudget budget, PreviewBudget previewBudget,
        CancellationToken cancellationToken)
    {
        await using var raw = entry.Open();
        await using var bounded = new XlsxRowParser.BoundedEntryStream(raw, budget);
        using var reader = XlsxRowParser.CreateXmlReader(bounded);
        var headers = Array.Empty<string>();
        var headerCells = Array.Empty<UploadManifestCell>();
        var rows = new List<IReadOnlyList<string>>();
        var rawBytes = 0;
        var truncated = false;
        var expectedRow = 1;
        var foundHeader = false;
        var rawHeaderRecord = (string?)null;

        while (await XlsxRowParser.MoveToNextRowAsync(reader, expectedRow, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowNumber = ParseRowNumber(reader.GetAttribute("r"), expectedRow);
            budget.CurrentRowNumber = rowNumber;
            if (!foundHeader && rowNumber > 1)
            {
                throw Error("empty_csv_header");
            }

            var parsed = await XlsxRowParser.ReadRowCellsAsync(reader, rowNumber, settings, sharedStrings,
                dateStyles, date1904, cancellationToken);
            expectedRow = rowNumber + 1;
            if (!foundHeader)
            {
                foundHeader = true;
                if (parsed.HasFormulaOrUnsupported)
                {
                    throw Error("invalid_xlsx_header");
                }

                headers = XlsxRowParser.BuildHeaderCells(parsed);
                headerCells = parsed.Cells.Select(cell => new UploadManifestCell(cell.RawText, rowNumber, cell.ColumnIndex)).ToArray();
                var headerJson = JsonSerializer.Serialize(headers);
                rawBytes = Encoding.UTF8.GetByteCount(headerJson);
                if (rawBytes > settings.MaxPreviewRawBytesPerPart || rawBytes > previewBudget.RawBytesRemaining)
                {
                    throw Error("preview_limit_exceeded");
                }

                previewBudget.RawBytesRemaining -= rawBytes;
                continue;
            }

            if (XlsxRowParser.IsRowEmpty(parsed, headers.Length))
            {
                continue;
            }

            if (rows.Count >= settings.PreviewRows || previewBudget.RowsRemaining == 0)
            {
                truncated = true;
                break;
            }

            var cells = RawCells(parsed, headers.Length);
            var rowJson = JsonSerializer.Serialize(cells);
            var rowBytes = Encoding.UTF8.GetByteCount(rowJson);
            if (rawBytes + rowBytes > settings.MaxPreviewRawBytesPerPart || rowBytes > previewBudget.RawBytesRemaining)
            {
                truncated = true;
                break;
            }

            rawBytes += rowBytes;
            previewBudget.RawBytesRemaining -= rowBytes;
            previewBudget.RowsRemaining--;
            rows.Add(cells);
        }

        if (!foundHeader)
        {
            throw Error("empty_csv_header");
        }

        return new UploadManifestPart(index, ImportPartType.XlsxWorksheet, sourceName, sheet.Name, index,
            sheet.Visibility, sheet.Locator, rawHeaderRecord, [], headers, headerCells, rows, 1, 1, truncated);
    }

    private static IReadOnlyList<string> RawCells(XlsxRowParser.ParsedSheetRow row, int headerCount)
    {
        var cells = new string[Math.Max(headerCount, row.MaxColumn)];
        Array.Fill(cells, string.Empty);
        foreach (var cell in row.Cells)
        {
            cells[cell.ColumnIndex - 1] = cell.RawText;
        }

        return cells;
    }

    private static async Task<(List<WorkbookSheet> Sheets, bool Date1904)> ReadWorkbookAsync(ZipArchive archive,
        XlsxRowParser.UncompressedBudget budget, CancellationToken cancellationToken)
    {
        var workbook = XlsxRowParser.FindEntry(archive, "xl/workbook.xml") ?? throw Error("invalid_xlsx_archive");
        var sheetDefinitions = new List<(string Name, string Visibility, string RelationshipId)>();
        var date1904 = false;
        try
        {
            await using var raw = workbook.Open();
            await using var bounded = new XlsxRowParser.BoundedEntryStream(raw, budget);
            using var reader = XlsxRowParser.CreateXmlReader(bounded);
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "workbookPr")
                {
                    var value = reader.GetAttribute("date1904");
                    date1904 = value is "1" or "true" or "TRUE";
                }
                else if (reader.LocalName == "sheet")
                {
                    var name = reader.GetAttribute("name");
                    var relationship = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                        ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships")
                        ?? reader.GetAttribute("r:id");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationship))
                    {
                        throw Error("invalid_xlsx_archive");
                    }

                    sheetDefinitions.Add((name, reader.GetAttribute("state") ?? "visible", relationship));
                }
            }
        }
        catch (XmlException) { throw Error("invalid_xlsx_syntax"); }
        catch (InvalidDataException) { throw Error("invalid_xlsx_archive"); }

        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        var rels = XlsxRowParser.FindEntry(archive, "xl/_rels/workbook.xml.rels") ?? throw Error("invalid_xlsx_archive");
        try
        {
            await using var raw = rels.Open();
            await using var bounded = new XlsxRowParser.BoundedEntryStream(raw, budget);
            using var reader = XlsxRowParser.CreateXmlReader(bounded);
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship")
                {
                    var id = reader.GetAttribute("Id");
                    var target = reader.GetAttribute("Target");
                    var targetMode = reader.GetAttribute("TargetMode");
                    var type = reader.GetAttribute("Type");
                    if (id is not null && target is not null && !string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase) &&
                        type is "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" or
                            "http://purl.oclc.org/ooxml/officeDocument/relationships/worksheet")
                    {
                        if (!relationships.TryAdd(id, XlsxRowParser.NormalizeZipPath(target)))
                        {
                            throw Error("invalid_xlsx_archive");
                        }
                    }
                }
            }
        }
        catch (XmlException) { throw Error("invalid_xlsx_syntax"); }
        catch (InvalidDataException) { throw Error("invalid_xlsx_archive"); }

        var sheets = new List<WorkbookSheet>(sheetDefinitions.Count);
        var locators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in sheetDefinitions)
        {
            if (!relationships.TryGetValue(definition.RelationshipId, out var locator) || !locators.Add(locator) ||
                !locator.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || XlsxRowParser.FindEntry(archive, locator) is null)
            {
                throw Error("invalid_xlsx_archive");
            }

            sheets.Add(new WorkbookSheet(definition.Name, definition.Visibility, locator));
        }

        return (sheets, date1904);
    }

    private static int ParseRowNumber(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text, out var row) && row >= fallback && row <= 1_048_576
            ? row
            : throw Error("invalid_xlsx_syntax");
    }

    private static ImportFileException Error(string code) => new(code, "XLSX inspection failed.", 0);
    private sealed record WorkbookSheet(string Name, string Visibility, string Locator);
}
