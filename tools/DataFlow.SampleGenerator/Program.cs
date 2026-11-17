using System.IO.Compression;
using System.Text;

// Generates the public demo fixtures in samples/ from deterministic synthetic
// data (DataFlow-Architecture.md §22). No real personal data. Output is stable
// between runs, so the samples can be referenced by docs and tests:
//
//   dotnet run --project tools/DataFlow.SampleGenerator
//
// Files:
//   samples/customers-valid.csv / .xlsx      8 valid rows, clean import.
//   samples/customers-with-errors.csv/.xlsx  12 rows with documented errors.
//   samples/customers-with-errors-fixed.csv  corrections of the error file.
//
// Expected schema (documented in samples/README.md): externalId string 80,
// name string 40, email email 254, registeredAt date optional, isActive bool.
namespace DataFlow.SampleGenerator;

public static class Program
{
    private const string Header = "externalId,name,email,registeredAt,isActive";

    // (externalId, name, email, registeredAt, isActive)
    private static readonly string[][] ValidRows =
    [
        ["ext-100", "Alice Brown", "alice.brown@example.test", "2026-01-05", "true"],
        ["ext-101", "Boris Chen", "boris.chen@example.test", "2026-01-18", "false"],
        ["ext-102", "Carmen Diaz", "carmen.diaz@example.test", "2026-02-02", "true"],
        ["ext-103", "Daniel Egorov", "daniel.egorov@example.test", "2026-02-14", "true"],
        ["ext-104", "Emma Fischer", "emma.fischer@example.test", "2026-03-01", "false"],
        ["ext-105", "Fedor Grachev", "fedor.grachev@example.test", "2026-03-09", "true"],
        ["ext-106", "Grace Hooper", "grace.hooper@example.test", "2026-03-21", "true"],
        ["ext-107", "Deniz Ildirim", "deniz.ildirim@example.test", "2026-04-02", "true"],
    ];

    private static readonly string[][] ErrorRows =
    [
        ["ext-001", "Ivan Petrov", "ivan.petrov@example.test", "2026-01-15", "true"],
        ["ext-002", "Anna Ivanova", "anna.ivanova@example.test", "2026-02-01", "true"],
        ["ext-003", "Sergey Smirnov", "sergey.smirnov@example.test", "2026-02-10", "false"],
        ["ext-004", "Elena Orlova", "elena.orlova@example.test", "2026-03-05", "true"],
        ["ext-005", "", "olga.nikitina@example.test", "2026-03-10", "true"],
        ["ext-006", "Pavel Orlov", "", "2026-03-12", "true"],
        ["ext-007", "Maxim Volkov", "not-an-email", "2026-03-15", "true"],
        ["ext-008", "Victoria Belova", "victoria.belova@example.test", "15.03.2026", "true"],
        ["ext-009", "Georgy Denisov", "georgy.denisov@example.test", "2026-03-20", "maybe"],
        ["ext-010", "This name is intentionally far beyond the forty-character limit", "long.name@example.test", "2026-03-25", "true"],
        ["ext-001", "Ivan Petrov", "ivan.petrov@example.test", "2026-01-15", "true"],
        ["ext-012", "Karim Zuhdi", "karim.zuhdi@example.test", "", "true"],
    ];

    private static readonly string[][] FixedRows =
    [
        ["ext-001", "Ivan Petrov", "ivan.petrov@example.test", "2026-01-15", "true"],
        ["ext-002", "Anna Ivanova", "anna.ivanova@example.test", "2026-02-01", "true"],
        ["ext-003", "Sergey Smirnov", "sergey.smirnov@example.test", "2026-02-10", "false"],
        ["ext-004", "Elena Orlova", "elena.orlova@example.test", "2026-03-05", "true"],
        ["ext-005", "Olga Nikitina", "olga.nikitina@example.test", "2026-03-10", "true"],
        ["ext-006", "Pavel Orlov", "pavel.orlov@example.test", "2026-03-12", "true"],
        ["ext-007", "Maxim Volkov", "maxim.volkov@example.test", "2026-03-15", "true"],
        ["ext-008", "Victoria Belova", "victoria.belova@example.test", "2026-03-18", "true"],
        ["ext-009", "Georgy Denisov", "georgy.denisov@example.test", "2026-03-20", "false"],
        ["ext-010", "Vera Sokolova", "long.name@example.test", "2026-03-25", "true"],
        ["ext-011", "Vera Sokolova", "vera.sokolova@example.test", "2026-03-26", "true"],
        ["ext-012", "Karim Zuhdi", "karim.zuhdi@example.test", "2026-04-10", "true"],
    ];

    public static int Main()
    {
        var samples = FindSamplesDirectory();
        WriteCsv(Path.Combine(samples, "customers-valid.csv"), ValidRows);
        WriteCsv(Path.Combine(samples, "customers-with-errors.csv"), ErrorRows);
        WriteCsv(Path.Combine(samples, "customers-with-errors-fixed.csv"), FixedRows);
        WriteXlsx(Path.Combine(samples, "customers-valid.xlsx"), ValidRows);
        WriteXlsx(Path.Combine(samples, "customers-with-errors.xlsx"), ErrorRows);

        Console.WriteLine($"Generated 5 sample files in {samples}");
        Console.WriteLine("customers-valid.csv:               8 rows, all valid (Completed, 8 inserted).");
        Console.WriteLine("customers-with-errors.csv:         12 rows (CompletedWithErrors, 5 inserted, 7 invalid).");
        Console.WriteLine("customers-with-errors-fixed.csv:   12 rows (Completed, 7 inserted, 1 updated, 4 duplicate).");
        Console.WriteLine("customers-*.xlsx:                  same rows as the CSV files.");
        return 0;
    }

    private static string FindSamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataFlow.sln")))
        {
            directory = directory.Parent!;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("DataFlow.sln not found above the tool directory.");
        }

        var samples = Path.Combine(directory.FullName, "samples");
        Directory.CreateDirectory(samples);
        return samples;
    }

    private static void WriteCsv(string path, string[][] rows)
    {
        var builder = new StringBuilder().Append(Header).Append('\n');
        foreach (var row in rows)
        {
            builder.Append(string.Join(',', row)).Append('\n');
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // Minimal spreadsheet writer: one worksheet, inline string cells. The own
    // OOXML reader of DataFlow (and Excel) both treat the file as text values,
    // which keeps XLSX samples equivalent to their CSV variants.
    private static void WriteXlsx(string path, string[][] rows)
    {
        File.Delete(path); // ZipArchiveMode.Create requires a new file.
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create, Encoding.UTF8);
        Entry(archive, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
            """);
        Entry(archive, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Entry(archive, "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Customers" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Entry(archive, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
            """);

        var sheet = new StringBuilder(
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>

            """.TrimEnd() + "\n");
        WriteRow(sheet, 1, ["externalId", "name", "email", "registeredAt", "isActive"]);
        for (var index = 0; index < rows.Length; index++)
        {
            WriteRow(sheet, index + 2, rows[index]);
        }

        sheet.Append("</sheetData></worksheet>");
        Entry(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static void WriteRow(StringBuilder sheet, int number, string[] cells)
    {
        sheet.Append($"<row r=\"{number}\">");
        for (var column = 0; column < cells.Length; column++)
        {
            var reference = $"{(char)('A' + column)}{number}";
            sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{Escape(cells[column])}</t></is></c>");
        }

        sheet.Append("</row>\n");
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void Entry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
