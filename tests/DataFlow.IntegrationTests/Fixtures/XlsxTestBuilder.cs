using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DataFlow.IntegrationTests.Fixtures;

internal sealed record XlsxCellSpec(
    string? Value,
    string Type = "s",
    int? StyleIndex = null,
    string? Formula = null,
    int? ColumnIndex = null)
{
    public static XlsxCellSpec SharedString(string value, int? columnIndex = null) =>
        new(value, "s", ColumnIndex: columnIndex);

    public static XlsxCellSpec InlineString(string value, int? columnIndex = null) =>
        new(value, "inlineStr", ColumnIndex: columnIndex);

    public static XlsxCellSpec Number(string rawNumber, int? styleIndex = null, int? columnIndex = null) =>
        new(rawNumber, "n", StyleIndex: styleIndex, ColumnIndex: columnIndex);

    public static XlsxCellSpec DateSerial(DateOnly date, int styleIndex = 1, int? columnIndex = null)
    {
        var days = date.DayNumber - new DateOnly(1899, 12, 30).DayNumber;
        if (date < new DateOnly(1900, 3, 1))
        {
            days = date.DayNumber - new DateOnly(1899, 12, 31).DayNumber;
        }

        return new(days.ToString(CultureInfo.InvariantCulture), "n", StyleIndex: styleIndex, ColumnIndex: columnIndex);
    }

    public static XlsxCellSpec IsoDate(string value, int? columnIndex = null) =>
        new(value, "d", ColumnIndex: columnIndex);

    public static XlsxCellSpec Boolean(bool value, int? columnIndex = null) =>
        new(value ? "1" : "0", "b", ColumnIndex: columnIndex);

    public static XlsxCellSpec RawBoolean(string rawValue, int? columnIndex = null) =>
        new(rawValue, "b", ColumnIndex: columnIndex);

    public static XlsxCellSpec FormulaCell(string formula, string? cachedValue = null, string type = "n", int? columnIndex = null) =>
        new(cachedValue, type, Formula: formula, ColumnIndex: columnIndex);

    public static XlsxCellSpec ErrorCell(string errorToken = "#DIV/0!", int? columnIndex = null) =>
        new(errorToken, "e", ColumnIndex: columnIndex);
}

internal sealed record XlsxRowSpec(int? RowNumber, IReadOnlyList<XlsxCellSpec> Cells);

internal sealed record XlsxSheetSpec(string Name, IReadOnlyList<XlsxRowSpec> Rows, string? EntryPath = null);

internal static class XlsxTestBuilder
{
    public static byte[] BuildSingleSheet(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<XlsxCellSpec>> dataRows,
        int extraZipEntries = 0)
    {
        var rows = new List<XlsxRowSpec>(dataRows.Count + 1)
        {
            new(1, headers.Select(h => XlsxCellSpec.SharedString(h)).ToArray())
        };

        for (var i = 0; i < dataRows.Count; i++)
        {
            rows.Add(new(i + 2, dataRows[i]));
        }

        return BuildWorkbook([new XlsxSheetSpec("Sheet1", rows)], extraZipEntries: extraZipEntries);
    }

    public static byte[] BuildWorkbook(
        IReadOnlyList<XlsxSheetSpec> sheets,
        int extraZipEntries = 0,
        string? customDateFormat = null)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sharedStrings = new List<string>();
            var sharedIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            int GetSharedStringIndex(string text)
            {
                if (!sharedIndex.TryGetValue(text, out var idx))
                {
                    idx = sharedStrings.Count;
                    sharedStrings.Add(text);
                    sharedIndex[text] = idx;
                }

                return idx;
            }

            WriteXmlEntry(archive, "[Content_Types].xml", writer =>
            {
                writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                writer.WriteStartElement("Default");
                writer.WriteAttributeString("Extension", "rels");
                writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
                writer.WriteEndElement();
                writer.WriteStartElement("Default");
                writer.WriteAttributeString("Extension", "xml");
                writer.WriteAttributeString("ContentType", "application/xml");
                writer.WriteEndElement();
                writer.WriteEndElement();
            });

            WriteXmlEntry(archive, "xl/workbook.xml", writer =>
            {
                writer.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                writer.WriteStartElement("sheets");
                for (var i = 0; i < sheets.Count; i++)
                {
                    writer.WriteStartElement("sheet");
                    writer.WriteAttributeString("name", sheets[i].Name);
                    writer.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", $"rId{i + 1}");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            });

            WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", writer =>
            {
                writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 0; i < sheets.Count; i++)
                {
                    var target = sheets[i].EntryPath ?? $"worksheets/sheet{i + 1}.xml";
                    writer.WriteStartElement("Relationship");
                    writer.WriteAttributeString("Id", $"rId{i + 1}");
                    writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
                    writer.WriteAttributeString("Target", target);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            });

            WriteXmlEntry(archive, "xl/styles.xml", writer =>
            {
                writer.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                if (customDateFormat is not null)
                {
                    writer.WriteStartElement("numFmts");
                    writer.WriteAttributeString("count", "1");
                    writer.WriteStartElement("numFmt");
                    writer.WriteAttributeString("numFmtId", "164");
                    writer.WriteAttributeString("formatCode", customDateFormat);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteStartElement("cellXfs");
                writer.WriteAttributeString("count", customDateFormat is null ? "2" : "3");
                // Style 0: general
                writer.WriteStartElement("xf");
                writer.WriteAttributeString("numFmtId", "0");
                writer.WriteEndElement();
                // Style 1: built-in date (14)
                writer.WriteStartElement("xf");
                writer.WriteAttributeString("numFmtId", "14");
                writer.WriteAttributeString("applyNumberFormat", "1");
                writer.WriteEndElement();
                if (customDateFormat is not null)
                {
                    // Style 2: custom date (164)
                    writer.WriteStartElement("xf");
                    writer.WriteAttributeString("numFmtId", "164");
                    writer.WriteAttributeString("applyNumberFormat", "1");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            });

            for (var s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                var entryPath = sheet.EntryPath ?? $"worksheets/sheet{s + 1}.xml";
                if (!entryPath.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    entryPath = "xl/" + entryPath.TrimStart('/');
                }

                WriteXmlEntry(archive, entryPath, writer =>
                {
                    writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                    writer.WriteStartElement("sheetData");
                    var autoRow = 1;
                    foreach (var row in sheet.Rows)
                    {
                        var r = row.RowNumber ?? autoRow;
                        autoRow = r + 1;
                        writer.WriteStartElement("row");
                        writer.WriteAttributeString("r", r.ToString(CultureInfo.InvariantCulture));
                        var autoCol = 1;
                        foreach (var cell in row.Cells)
                        {
                            var col = cell.ColumnIndex ?? autoCol;
                            autoCol = col + 1;
                            writer.WriteStartElement("c");
                            writer.WriteAttributeString("r", $"{ColumnName(col)}{r}");
                            if (cell.StyleIndex is int styleIndex)
                            {
                                writer.WriteAttributeString("s", styleIndex.ToString(CultureInfo.InvariantCulture));
                            }

                            if (cell.Type == "s" && cell.Formula is null)
                            {
                                if (cell.Value is not null)
                                {
                                    writer.WriteAttributeString("t", "s");
                                    var sstIdx = GetSharedStringIndex(cell.Value);
                                    writer.WriteElementString("v", sstIdx.ToString(CultureInfo.InvariantCulture));
                                }
                            }
                            else if (cell.Type == "inlineStr" && cell.Formula is null)
                            {
                                writer.WriteAttributeString("t", "inlineStr");
                                if (cell.Value is not null)
                                {
                                    writer.WriteStartElement("is");
                                    writer.WriteStartElement("t");
                                    if (cell.Value.Length > 0 && (char.IsWhiteSpace(cell.Value[0]) || char.IsWhiteSpace(cell.Value[^1])))
                                    {
                                        writer.WriteAttributeString("xml", "space", null, "preserve");
                                    }

                                    writer.WriteString(cell.Value);
                                    writer.WriteEndElement();
                                    writer.WriteEndElement();
                                }
                            }
                            else
                            {
                                if (!string.IsNullOrEmpty(cell.Type))
                                {
                                    writer.WriteAttributeString("t", cell.Type);
                                }

                                if (cell.Formula is not null)
                                {
                                    writer.WriteElementString("f", cell.Formula);
                                }

                                if (cell.Value is not null)
                                {
                                    writer.WriteElementString("v", cell.Value);
                                }
                            }

                            writer.WriteEndElement();
                        }

                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteEndElement();
                });
            }

            WriteXmlEntry(archive, "xl/sharedStrings.xml", writer =>
            {
                writer.WriteStartElement("sst", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                writer.WriteAttributeString("count", sharedStrings.Count.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("uniqueCount", sharedStrings.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var str in sharedStrings)
                {
                    writer.WriteStartElement("si");
                    writer.WriteStartElement("t");
                    if (str.Length > 0 && (char.IsWhiteSpace(str[0]) || char.IsWhiteSpace(str[^1])))
                    {
                        writer.WriteAttributeString("xml", "space", null, "preserve");
                    }

                    writer.WriteString(str);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            });

            for (var extra = 0; extra < extraZipEntries; extra++)
            {
                var entry = archive.CreateEntry($"xl/extra/item{extra}.xml", CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write("<extra/>"u8);
            }
        }

        return memory.ToArray();
    }

    public static string ColumnName(int columnIndex)
    {
        var chars = new Stack<char>();
        var current = columnIndex;
        while (current > 0)
        {
            var modulo = (current - 1) % 26;
            chars.Push((char)('A' + modulo));
            current = (current - 1) / 26;
        }

        return new string(chars.ToArray());
    }

    private static void WriteXmlEntry(ZipArchive archive, string path, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        });
        write(writer);
    }
}
