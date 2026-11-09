using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class XlsxRowParser(IOptions<ImportProcessingOptions> options) : IXlsxRowParser
{
    private static readonly HashSet<int> BuiltInDateNumFmtIds =
    [
        14, 15, 16, 17, 18, 19, 20, 21, 22,
        27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58
    ];

    public async IAsyncEnumerable<ParsedRow> ParseAsync(Stream stream, DataSourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var limits = options.Value;
        Validator.ValidateObject(limits, new ValidationContext(limits), validateAllProperties: true);
        cancellationToken.ThrowIfCancellationRequested();

        using var archiveBuffer = await ReadArchiveToMemoryAsync(stream, limits.MaxFileBytes, cancellationToken);
        if (archiveBuffer.Length == 0)
        {
            throw new ImportFileException("empty_file", "An XLSX header is required.", 1);
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX file is not a valid ZIP archive.", 0);
        }

        using (archive)
        {
            ValidateZipLimits(archive, limits);
            var budget = new UncompressedBudget(limits.MaxUncompressedBytes);
            var (sheetPath, date1904) = await ResolveFirstWorksheetAsync(archive, budget, cancellationToken);
            var sheetEntry = FindEntry(archive, sheetPath)
                ?? throw new ImportFileException("invalid_xlsx_archive", "The first worksheet entry was not found.", 0);
            var dateStyles = await ReadDateStylesAsync(archive, budget, cancellationToken);
            var sharedStrings = await ReadSharedStringsAsync(archive, budget, limits.MaxCellLength, cancellationToken);

            await using var rawSheetStream = sheetEntry.Open();
            await using var boundedSheetStream = new BoundedEntryStream(rawSheetStream, budget);
            using var reader = CreateXmlReader(boundedSheetStream);

            string[]? headers = null;
            Dictionary<string, string>? fieldLogicalNames = null;
            var dataRows = 0;
            var expectedPhysicalRow = 1;

            while (await MoveToNextRowAsync(reader, expectedPhysicalRow, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowNumber = ParseRowNumber(reader.GetAttribute("r"), expectedPhysicalRow);
                budget.CurrentRowNumber = rowNumber;

                if (expectedPhysicalRow == 1 && rowNumber > 1)
                {
                    throw new ImportFileException("empty_csv_header", "XLSX column names must not be empty.", 1);
                }

                while (expectedPhysicalRow < rowNumber)
                {
                    yield return new(expectedPhysicalRow, new Dictionary<string, string?>(), "[]", IsEmpty: true,
                        LastPhysicalRowNumber: expectedPhysicalRow);
                    expectedPhysicalRow++;
                }

                var parsedCells = await ReadRowCellsAsync(reader, rowNumber, limits, sharedStrings, dateStyles, date1904, cancellationToken);
                expectedPhysicalRow = rowNumber + 1;

                if (rowNumber == 1)
                {
                    if (parsedCells.HasFormulaOrUnsupported)
                    {
                        throw new ImportFileException("invalid_xlsx_header", "The XLSX header contains a formula or unsupported cell.", 1);
                    }

                    if (parsedCells.MaxColumn == 0)
                    {
                        throw new ImportFileException("empty_csv_header", "XLSX column names must not be empty.", 1);
                    }

                    headers = BuildHeaderCells(parsedCells);
                    ValidateHeader(headers, schema);
                    fieldLogicalNames = schema.Fields.ToDictionary(f => f.Name, f => f.LogicalName, StringComparer.Ordinal);
                    continue;
                }

                if (headers is null || fieldLogicalNames is null)
                {
                    throw new ImportFileException("empty_file", "An XLSX header is required.", 1);
                }

                if (IsRowEmpty(parsedCells, headers.Length))
                {
                    yield return new(rowNumber, new Dictionary<string, string?>(), "[]", IsEmpty: true,
                        LastPhysicalRowNumber: rowNumber);
                    continue;
                }

                if (++dataRows > limits.MaxRows)
                {
                    throw new ImportFileException("row_limit_exceeded", "The XLSX file exceeds the row limit.", rowNumber);
                }

                var cellCount = Math.Max(headers.Length, parsedCells.MaxColumn);
                var rawCells = new string[cellCount];
                Array.Fill(rawCells, string.Empty);
                var errors = new List<ImportRowError>();

                foreach (var cell in parsedCells.Cells)
                {
                    rawCells[cell.ColumnIndex - 1] = cell.RawText;
                    if (cell.ErrorCode is not null && cell.ErrorMessage is not null)
                    {
                        var fieldName = cell.ColumnIndex <= headers.Length
                            ? fieldLogicalNames.GetValueOrDefault(headers[cell.ColumnIndex - 1], headers[cell.ColumnIndex - 1])
                            : "$row";
                        errors.Add(new ImportRowError(fieldName, cell.ErrorCode, cell.ErrorMessage));
                    }
                }

                if (parsedCells.MaxColumn > headers.Length)
                {
                    errors.Add(new ImportRowError("$row", "column_count_mismatch", "The column count does not match the header."));
                }

                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var index = 0; index < headers.Length; index++)
                {
                    var cell = parsedCells.ByColumn.GetValueOrDefault(index + 1);
                    values.Add(headers[index], cell is { ErrorCode: not null } ? null : rawCells[index]);
                }

                yield return new(rowNumber, values, JsonSerializer.Serialize(rawCells), Errors: errors,
                    LastPhysicalRowNumber: rowNumber);
            }

            if (headers is null)
            {
                throw new ImportFileException("empty_file", "An XLSX header is required.", 1);
            }
        }
    }

    private static async Task<MemoryStream> ReadArchiveToMemoryAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        var memory = new MemoryStream();
        var buffer = new byte[16_384];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ImportFileException("file_too_large", "The XLSX file exceeds the maximum file size.", 0);
            }

            memory.Write(buffer, 0, read);
        }

        memory.Position = 0;
        return memory;
    }

    private static void ValidateZipLimits(ZipArchive archive, ImportProcessingOptions limits)
    {
        if (archive.Entries.Count == 0)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive contains no entries.", 0);
        }

        if (archive.Entries.Count > limits.MaxZipEntries)
        {
            throw new ImportFileException("xlsx_zip_entry_limit_exceeded", "The XLSX archive exceeds the ZIP entry limit.", 0);
        }

        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.Length > limits.MaxUncompressedBytes)
            {
                throw new ImportFileException("xlsx_uncompressed_limit_exceeded", "The XLSX archive exceeds the uncompressed size limit.", 0);
            }

            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > limits.MaxUncompressedBytes)
            {
                throw new ImportFileException("xlsx_uncompressed_limit_exceeded", "The XLSX archive exceeds the uncompressed size limit.", 0);
            }
        }
    }

    private static async Task<(string SheetPath, bool Date1904)> ResolveFirstWorksheetAsync(
        ZipArchive archive, UncompressedBudget budget, CancellationToken cancellationToken)
    {
        var workbookEntry = FindEntry(archive, "xl/workbook.xml")
            ?? throw new ImportFileException("invalid_xlsx_archive", "The XLSX workbook definition is missing.", 0);

        string? firstRelId = null;
        var foundSheet = false;
        var date1904 = false;

        await using (var rawStream = workbookEntry.Open())
        await using (var stream = new BoundedEntryStream(rawStream, budget))
        using (var reader = CreateXmlReader(stream))
        {
            try
            {
                while (await reader.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "workbookPr")
                    {
                        var attr = reader.GetAttribute("date1904");
                        date1904 = attr is "1" or "true" or "TRUE";
                    }
                    else if (reader.LocalName == "sheet" && !foundSheet)
                    {
                        foundSheet = true;
                        firstRelId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                            ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships")
                            ?? reader.GetAttribute("r:id");
                    }
                }
            }
            catch (XmlException)
            {
                throw new ImportFileException("invalid_xlsx_syntax", "The XLSX workbook XML is malformed.", 0);
            }
            catch (InvalidDataException)
            {
                throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", 0);
            }
        }

        if (!foundSheet)
        {
            throw new ImportFileException("empty_file", "The XLSX workbook contains no worksheets.", 1);
        }

        var relsEntry = FindEntry(archive, "xl/_rels/workbook.xml.rels");
        if (relsEntry is not null && !string.IsNullOrWhiteSpace(firstRelId))
        {
            await using var rawRels = relsEntry.Open();
            await using var stream = new BoundedEntryStream(rawRels, budget);
            using var reader = CreateXmlReader(stream);
            try
            {
                while (await reader.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" &&
                        string.Equals(reader.GetAttribute("Id"), firstRelId, StringComparison.Ordinal))
                    {
                        var target = reader.GetAttribute("Target");
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            return (NormalizeZipPath(target), date1904);
                        }
                    }
                }
            }
            catch (XmlException)
            {
                throw new ImportFileException("invalid_xlsx_syntax", "The XLSX relationships XML is malformed.", 0);
            }
            catch (InvalidDataException)
            {
                throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", 0);
            }
        }

        return ("xl/worksheets/sheet1.xml", date1904);
    }

    private static async Task<HashSet<int>> ReadDateStylesAsync(
        ZipArchive archive, UncompressedBudget budget, CancellationToken cancellationToken)
    {
        var stylesEntry = FindEntry(archive, "xl/styles.xml");
        if (stylesEntry is null)
        {
            return [];
        }

        var customDateFormats = new HashSet<int>();
        var dateStyleIndices = new HashSet<int>();
        await using var rawStream = stylesEntry.Open();
        await using var stream = new BoundedEntryStream(rawStream, budget);
        using var reader = CreateXmlReader(stream);
        var inCellXfs = false;
        var xfIndex = 0;

        try
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "numFmt")
                    {
                        var idText = reader.GetAttribute("numFmtId");
                        var code = reader.GetAttribute("formatCode");
                        if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                            !string.IsNullOrWhiteSpace(code) && IsCustomDateFormat(code))
                        {
                            customDateFormats.Add(id);
                        }
                    }
                    else if (reader.LocalName == "cellXfs")
                    {
                        inCellXfs = !reader.IsEmptyElement;
                        xfIndex = 0;
                    }
                    else if (inCellXfs && reader.LocalName == "xf")
                    {
                        var idText = reader.GetAttribute("numFmtId");
                        if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numFmtId) &&
                            (BuiltInDateNumFmtIds.Contains(numFmtId) || customDateFormats.Contains(numFmtId)))
                        {
                            dateStyleIndices.Add(xfIndex);
                        }

                        xfIndex++;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                {
                    inCellXfs = false;
                }
            }
        }
        catch (XmlException)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX styles XML is malformed.", 0);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", 0);
        }

        return dateStyleIndices;
    }

    private static bool IsCustomDateFormat(string formatCode)
    {
        var builder = new StringBuilder(formatCode.Length);
        var inQuotes = false;
        var inBrackets = false;
        for (var i = 0; i < formatCode.Length; i++)
        {
            var ch = formatCode[i];
            if (ch == '\\' && i + 1 < formatCode.Length)
            {
                i++;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (ch == '[')
            {
                inBrackets = true;
                continue;
            }

            if (ch == ']')
            {
                inBrackets = false;
                continue;
            }

            if (!inBrackets)
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        var cleaned = builder.ToString();
        return cleaned.Contains('y') && (cleaned.Contains('m') || cleaned.Contains('d'));
    }

    private static async Task<List<string>> ReadSharedStringsAsync(
        ZipArchive archive, UncompressedBudget budget, int maxCellLength, CancellationToken cancellationToken)
    {
        var entry = FindEntry(archive, "xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var strings = new List<string>();
        await using var rawStream = entry.Open();
        await using var stream = new BoundedEntryStream(rawStream, budget);
        using var reader = CreateXmlReader(stream);

        var current = new StringBuilder();
        var inSi = false;
        var inT = false;
        var inPhonetic = false;

        try
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (reader.LocalName == "si")
                        {
                            current.Clear();
                            if (reader.IsEmptyElement)
                            {
                                strings.Add(string.Empty);
                            }
                            else
                            {
                                inSi = true;
                            }
                        }
                        else if (inSi && reader.LocalName is "rPh" or "phoneticPr")
                        {
                            if (!reader.IsEmptyElement)
                            {
                                inPhonetic = true;
                            }
                        }
                        else if (inSi && !inPhonetic && reader.LocalName == "t" && !reader.IsEmptyElement)
                        {
                            inT = true;
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.CDATA:
                        if (inSi && inT && !inPhonetic && current.Length <= maxCellLength)
                        {
                            var remaining = maxCellLength + 1 - current.Length;
                            var value = reader.Value;
                            current.Append(value.Length <= remaining ? value : value.AsSpan(0, remaining));
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (reader.LocalName == "t")
                        {
                            inT = false;
                        }
                        else if (reader.LocalName is "rPh" or "phoneticPr")
                        {
                            inPhonetic = false;
                        }
                        else if (reader.LocalName == "si" && inSi)
                        {
                            strings.Add(current.ToString());
                            inSi = false;
                        }

                        break;
                }
            }
        }
        catch (XmlException)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX shared strings XML is malformed.", 0);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", 0);
        }

        return strings;
    }

    private static async Task<bool> MoveToNextRowAsync(XmlReader reader, int rowNumber, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                {
                    return true;
                }
            }

            return false;
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (XmlException)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX worksheet XML is malformed.", rowNumber);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", rowNumber);
        }
    }

    private static async Task<ParsedSheetRow> ReadRowCellsAsync(
        XmlReader reader,
        int rowNumber,
        ImportProcessingOptions limits,
        List<string> sharedStrings,
        HashSet<int> dateStyles,
        bool date1904,
        CancellationToken cancellationToken)
    {
        var cells = new List<ParsedSheetCell>();
        var byColumn = new Dictionary<int, ParsedSheetCell>();
        if (reader.IsEmptyElement)
        {
            return new(cells, byColumn, 0, false, 0);
        }

        var previousColumn = 0;
        var maxColumn = 0;
        var hasFormulaOrUnsupported = false;
        var explicitEmptyCells = 0;

        try
        {
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c")
                {
                    continue;
                }

                var colIndex = ParseCellColumn(reader.GetAttribute("r"), rowNumber, previousColumn);
                if (colIndex <= previousColumn)
                {
                    throw new ImportFileException("invalid_xlsx_syntax", "The XLSX cell coordinates are out of order.", rowNumber);
                }

                if (colIndex > limits.MaxColumns)
                {
                    throw new ImportFileException("column_limit_exceeded", "The XLSX record exceeds the column limit.", rowNumber);
                }

                previousColumn = colIndex;
                maxColumn = Math.Max(maxColumn, colIndex);

                var cellType = reader.GetAttribute("t");
                var styleAttr = reader.GetAttribute("s");
                var styleIndex = int.TryParse(styleAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStyle)
                    ? parsedStyle
                    : -1;

                var cell = await ReadSingleCellAsync(reader, rowNumber, colIndex, cellType, styleIndex,
                    limits.MaxCellLength, sharedStrings, dateStyles, date1904, cancellationToken);

                if (cell.ErrorCode is not null)
                {
                    hasFormulaOrUnsupported = true;
                }

                if (cell.HasExplicitEmptyValue)
                {
                    explicitEmptyCells++;
                }

                cells.Add(cell);
                byColumn[colIndex] = cell;
            }
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (XmlException)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX worksheet XML is malformed.", rowNumber);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx_archive", "The XLSX archive stream is corrupted.", rowNumber);
        }

        return new(cells, byColumn, maxColumn, hasFormulaOrUnsupported, explicitEmptyCells);
    }

    private static async Task<ParsedSheetCell> ReadSingleCellAsync(
        XmlReader reader,
        int rowNumber,
        int colIndex,
        string? cellType,
        int styleIndex,
        int maxCellLength,
        List<string> sharedStrings,
        HashSet<int> dateStyles,
        bool date1904,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            if (cellType == "e")
            {
                return new(colIndex, "#ERROR", "unsupported_cell_value", "The cell contains an unsupported or error value.", false);
            }

            return new(colIndex, string.Empty, null, null, false);
        }

        var hasFormula = false;
        var hasValueElement = false;
        var hasInlineString = false;
        var valueBuilder = new StringBuilder();
        var inlineBuilder = new StringBuilder();
        var inV = false;
        var inIs = false;
        var inT = false;
        var inPhonetic = false;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.LocalName == "f")
                    {
                        hasFormula = true;
                    }
                    else if (reader.LocalName == "v")
                    {
                        hasValueElement = true;
                        inV = !reader.IsEmptyElement;
                    }
                    else if (reader.LocalName == "is")
                    {
                        hasInlineString = true;
                        inIs = !reader.IsEmptyElement;
                    }
                    else if (inIs && reader.LocalName is "rPh" or "phoneticPr")
                    {
                        if (!reader.IsEmptyElement)
                        {
                            inPhonetic = true;
                        }
                    }
                    else if (inIs && !inPhonetic && reader.LocalName == "t")
                    {
                        inT = !reader.IsEmptyElement;
                    }

                    break;

                case XmlNodeType.Text:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.CDATA:
                    if (inV && valueBuilder.Length <= maxCellLength)
                    {
                        var remaining = maxCellLength + 1 - valueBuilder.Length;
                        var text = reader.Value;
                        valueBuilder.Append(text.Length <= remaining ? text : text.AsSpan(0, remaining));
                    }
                    else if (inIs && inT && !inPhonetic && inlineBuilder.Length <= maxCellLength)
                    {
                        var remaining = maxCellLength + 1 - inlineBuilder.Length;
                        var text = reader.Value;
                        inlineBuilder.Append(text.Length <= remaining ? text : text.AsSpan(0, remaining));
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (reader.LocalName == "v")
                    {
                        inV = false;
                    }
                    else if (reader.LocalName == "t")
                    {
                        inT = false;
                    }
                    else if (reader.LocalName is "rPh" or "phoneticPr")
                    {
                        inPhonetic = false;
                    }
                    else if (reader.LocalName == "is")
                    {
                        inIs = false;
                    }
                    else if (reader.LocalName == "c")
                    {
                        return EvaluateCell(rowNumber, colIndex, cellType, styleIndex, maxCellLength,
                            hasFormula, hasValueElement, hasInlineString, valueBuilder.ToString(), inlineBuilder.ToString(),
                            sharedStrings, dateStyles, date1904);
                    }

                    break;
            }
        }

        throw new ImportFileException("invalid_xlsx_syntax", "Unexpected end of XLSX cell element.", rowNumber);
    }

    private static ParsedSheetCell EvaluateCell(
        int rowNumber,
        int colIndex,
        string? cellType,
        int styleIndex,
        int maxCellLength,
        bool hasFormula,
        bool hasValueElement,
        bool hasInlineString,
        string valueText,
        string inlineText,
        List<string> sharedStrings,
        HashSet<int> dateStyles,
        bool date1904)
    {
        if (hasFormula)
        {
            var rawFormulaMarker = valueText.Length is > 0 and <= 128 ? valueText : "=<formula>";
            return new(colIndex, rawFormulaMarker, "formula_cell_not_supported",
                "Formula cells are not supported; provide literal values.", false);
        }

        if (cellType == "e")
        {
            var rawError = valueText.Length is > 0 and <= 128 ? valueText : "#ERROR";
            return new(colIndex, rawError, "unsupported_cell_value",
                "The cell contains an unsupported or error value.", false);
        }

        switch (cellType)
        {
            case "s":
                {
                    if (!hasValueElement || valueText.Length == 0)
                    {
                        return new(colIndex, string.Empty, null, null, false);
                    }

                    if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sstIndex) ||
                        sstIndex < 0 || sstIndex >= sharedStrings.Count)
                    {
                        throw new ImportFileException("invalid_xlsx_syntax", "The XLSX shared string index is out of range.", rowNumber);
                    }

                    var resolved = sharedStrings[sstIndex];
                    if (resolved.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    return new(colIndex, resolved, null, null, resolved.Length == 0);
                }

            case "inlineStr":
                {
                    if (inlineText.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    return new(colIndex, inlineText, null, null, hasInlineString && inlineText.Length == 0);
                }

            case "str":
                {
                    if (valueText.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    return new(colIndex, valueText, null, null, hasValueElement && valueText.Length == 0);
                }

            case "b":
                {
                    if (!hasValueElement || valueText.Length == 0)
                    {
                        return new(colIndex, string.Empty, null, null, false);
                    }

                    if (valueText is "1" or "true" or "TRUE")
                    {
                        return new(colIndex, "true", null, null, false);
                    }

                    if (valueText is "0" or "false" or "FALSE")
                    {
                        return new(colIndex, "false", null, null, false);
                    }

                    return new(colIndex, valueText.Length <= maxCellLength ? valueText : valueText[..maxCellLength],
                        "unsupported_cell_value", "The cell contains an unsupported or error value.", false);
                }

            case "d":
                {
                    if (!hasValueElement || valueText.Length == 0)
                    {
                        return new(colIndex, string.Empty, null, null, false);
                    }

                    if (valueText.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    if (DateOnly.TryParseExact(valueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
                    {
                        return new(colIndex, dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, null, false);
                    }

                    if (DateTimeOffset.TryParse(valueText, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto) &&
                        dto.TimeOfDay == TimeSpan.Zero)
                    {
                        return new(colIndex, DateOnly.FromDateTime(dto.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, null, false);
                    }

                    return new(colIndex, valueText, null, null, false);
                }

            case null:
            case "":
            case "n":
                {
                    if (!hasValueElement || valueText.Length == 0)
                    {
                        return new(colIndex, string.Empty, null, null, false);
                    }

                    if (valueText.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    if (!decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        return new(colIndex, valueText, "unsupported_cell_value",
                            "The cell contains an unsupported or error value.", false);
                    }

                    if (styleIndex >= 0 && dateStyles.Contains(styleIndex))
                    {
                        var formattedDate = FormatExcelSerialDate(number, date1904);
                        return new(colIndex, formattedDate, null, null, false);
                    }

                    var canonicalNumber = number.ToString("G29", CultureInfo.InvariantCulture);
                    if (canonicalNumber.Length > maxCellLength)
                    {
                        throw new ImportFileException("cell_limit_exceeded", "The XLSX record exceeds the cell length limit.", rowNumber);
                    }

                    return new(colIndex, canonicalNumber, null, null, false);
                }

            default:
                return new(colIndex, valueText.Length <= maxCellLength ? valueText : valueText[..maxCellLength],
                    "unsupported_cell_value", "The cell contains an unsupported or error value.", false);
        }
    }

    private static string FormatExcelSerialDate(decimal serial, bool date1904)
    {
        var wholeDays = decimal.Truncate(serial);
        var fraction = serial - wholeDays;
        if (!date1904 && wholeDays == 60)
        {
            return "1900-02-29";
        }

        if (wholeDays < 1 || wholeDays > 2_958_465)
        {
            return serial.ToString("G29", CultureInfo.InvariantCulture);
        }

        var days = (int)wholeDays;
        var baseDate = date1904
            ? new DateOnly(1904, 1, 1).AddDays(days)
            : days < 60
                ? new DateOnly(1899, 12, 31).AddDays(days)
                : new DateOnly(1899, 12, 30).AddDays(days);

        if (fraction == 0)
        {
            return baseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var ticks = (long)decimal.Round(fraction * TimeSpan.TicksPerDay);
        var dateTime = baseDate.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromTicks(Math.Clamp(ticks, 0, TimeSpan.TicksPerDay - 1))));
        return dateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool IsRowEmpty(ParsedSheetRow row, int headerCount)
    {
        if (row.HasFormulaOrUnsupported)
        {
            return false;
        }

        if (row.Cells.Count == 0)
        {
            return true;
        }

        if (row.Cells.Any(c => !string.IsNullOrWhiteSpace(c.RawText)))
        {
            return false;
        }

        return row.ExplicitEmptyCells < headerCount;
    }

    private static string[] BuildHeaderCells(ParsedSheetRow headerRow)
    {
        var headers = new string[headerRow.MaxColumn];
        Array.Fill(headers, string.Empty);
        foreach (var cell in headerRow.Cells)
        {
            headers[cell.ColumnIndex - 1] = cell.RawText;
        }

        return headers;
    }

    private static void ValidateHeader(string[] headers, DataSourceSchema schema)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ImportFileException("empty_csv_header", "XLSX column names must not be empty.", 1);
            }

            if (!seen.Add(name))
            {
                throw new ImportFileException("duplicate_csv_header", "XLSX column names must be unique.", 1);
            }
        }

        var expected = schema.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        if (!schema.AllowUnknownColumns && headers.Any(name => !expected.Contains(name)))
        {
            throw new ImportFileException("unknown_csv_header", "The XLSX header contains an unknown column.", 1);
        }

        if (expected.Except(headers, StringComparer.Ordinal).Any())
        {
            throw new ImportFileException("missing_csv_header", "The XLSX header is missing a schema column.", 1);
        }
    }

    private static int ParseRowNumber(string? attribute, int fallback)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return fallback;
        }

        if (!int.TryParse(attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < fallback)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX row number is invalid.", fallback);
        }

        return number;
    }

    private static int ParseCellColumn(string? reference, int rowNumber, int previousColumn)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return previousColumn + 1;
        }

        var column = 0;
        var letterCount = 0;
        var index = 0;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            letterCount++;
            if (letterCount > 3)
            {
                throw new ImportFileException("column_limit_exceeded", "The XLSX record exceeds the column limit.", rowNumber);
            }

            column = column * 26 + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
            index++;
        }

        if (letterCount == 0 || index == reference.Length)
        {
            throw new ImportFileException("invalid_xlsx_syntax", "The XLSX cell reference is invalid.", rowNumber);
        }

        for (var i = index; i < reference.Length; i++)
        {
            if (!char.IsAsciiDigit(reference[i]))
            {
                throw new ImportFileException("invalid_xlsx_syntax", "The XLSX cell reference is invalid.", rowNumber);
            }
        }

        return column;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string normalizedPath)
    {
        foreach (var entry in archive.Entries)
        {
            var entryPath = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.Equals(entryPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string NormalizeZipPath(string target)
    {
        var cleaned = target.Replace('\\', '/').Trim();
        if (cleaned.StartsWith('/'))
        {
            cleaned = cleaned.TrimStart('/');
        }
        else if (!cleaned.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = "xl/" + cleaned;
        }

        var parts = new List<string>();
        foreach (var segment in cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    throw new ImportFileException("invalid_xlsx_archive", "Invalid worksheet path in XLSX archive.", 0);
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static XmlReader CreateXmlReader(Stream stream) =>
        XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });

    private sealed record ParsedSheetCell(
        int ColumnIndex,
        string RawText,
        string? ErrorCode,
        string? ErrorMessage,
        bool HasExplicitEmptyValue);

    private sealed record ParsedSheetRow(
        IReadOnlyList<ParsedSheetCell> Cells,
        IReadOnlyDictionary<int, ParsedSheetCell> ByColumn,
        int MaxColumn,
        bool HasFormulaOrUnsupported,
        int ExplicitEmptyCells);

    private sealed class UncompressedBudget(long maxBytes)
    {
        private long _consumed;
        public int CurrentRowNumber { get; set; }

        public void Consume(int bytes)
        {
            _consumed += bytes;
            if (_consumed > maxBytes)
            {
                throw new ImportFileException("xlsx_uncompressed_limit_exceeded",
                    "The XLSX archive exceeds the uncompressed size limit.", CurrentRowNumber);
            }
        }
    }

    private sealed class BoundedEntryStream(Stream inner, UncompressedBudget budget) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                budget.Consume(read);
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                budget.Consume(read);
            }

            return read;
        }
    }
}
