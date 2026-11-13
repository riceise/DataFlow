using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Application.Rules;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Parsing;
using DataFlow.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class XlsxRowParserTests
{
    private static readonly string[] CustomerHeaders = ["externalId", "name", "email", "registeredAt", "isActive"];

    [Fact]
    public async Task FirstSheetOnly_MapsHeadersAndIgnoresSubsequentSheets()
    {
        var sheet1 = new XlsxSheetSpec(
            "Customers",
            [
                new(1,
                [
                    XlsxCellSpec.SharedString("email"),
                    XlsxCellSpec.SharedString("isActive"),
                    XlsxCellSpec.SharedString("name"),
                    XlsxCellSpec.SharedString("externalId"),
                    XlsxCellSpec.SharedString("registeredAt")
                ]),
                new(2,
                [
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.Boolean(true),
                    XlsxCellSpec.InlineString(" Анна "),
                    XlsxCellSpec.SharedString(" ID-1 "),
                    XlsxCellSpec.DateSerial(new DateOnly(2024, 2, 29), styleIndex: 2)
                ])
            ],
            EntryPath: "worksheets/primary.xml");

        var sheet2 = new XlsxSheetSpec(
            "IgnoredSheet",
            [
                new(1, [XlsxCellSpec.SharedString("brokenHeader")]),
                new(2, [XlsxCellSpec.FormulaCell("1/0", "#DIV/0!", "e")])
            ],
            EntryPath: "worksheets/secondary.xml");

        var bytes = XlsxTestBuilder.BuildWorkbook([sheet1, sheet2], customDateFormat: "yyyy-mm-dd");
        await using var stream = new MemoryStream(bytes);
        var row = Assert.Single(await ReadAll(stream));
        Assert.Equal(2, row.RowNumber);
        Assert.Equal(2, row.CheckpointRowNumber);
        Assert.Equal(" Анна ", row.Values["name"]);
        Assert.Equal(" ID-1 ", row.Values["externalId"]);
        Assert.Equal("a@example.com", row.Values["email"]);
        Assert.Equal("2024-02-29", row.Values["registeredAt"]);
        Assert.Equal("true", row.Values["isActive"]);
        Assert.Empty(row.Errors!);
        Assert.False(row.IsEmpty);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task FormulaCells_AreNeverExecuted_AndProduceRowError()
    {
        var bytes = XlsxTestBuilder.BuildSingleSheet(
            CustomerHeaders,
            [
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    XlsxCellSpec.FormulaCell("CONCATENATE(\"Alice\",\" Smith\")", "Alice Smith", "str"),
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.IsoDate("2026-01-15"),
                    XlsxCellSpec.Boolean(true)
                ],
                [
                    XlsxCellSpec.SharedString("ID-2"),
                    XlsxCellSpec.SharedString("Bob"),
                    XlsxCellSpec.SharedString("bob@example.com"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 1, 15)),
                    XlsxCellSpec.Boolean(false)
                ]
            ]);

        var rows = await ReadAll(bytes);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].Values["name"]);
        var error = Assert.Single(rows[0].Errors!);
        Assert.Equal("name", error.Field);
        Assert.Equal("formula_cell_not_supported", error.Code);

        var processor = new CustomerFileRowProcessor(Guid.NewGuid(), Schema());
        var firstValidation = processor.Process(rows[0]);
        Assert.False(firstValidation.IsValid);
        Assert.Equal("formula_cell_not_supported", Assert.Single(firstValidation.Errors).Code);

        var secondValidation = processor.Process(rows[1]);
        Assert.True(secondValidation.IsValid);
        Assert.Equal("Bob", secondValidation.Customer!.Name);
    }

    [Fact]
    public async Task FormulaOrErrorInHeader_RejectsFileImmediately()
    {
        var formulaHeader = XlsxTestBuilder.BuildWorkbook(
        [
            new("Sheet1",
            [
                new(1,
                [
                    XlsxCellSpec.SharedString("externalId"),
                    XlsxCellSpec.FormulaCell("\"name\"", "name", "str"),
                    XlsxCellSpec.SharedString("email"),
                    XlsxCellSpec.SharedString("registeredAt"),
                    XlsxCellSpec.SharedString("isActive")
                ])
            ])
        ]);

        var ex1 = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(formulaHeader));
        Assert.Equal("invalid_xlsx_header", ex1.Code);
        Assert.Equal(1, ex1.RowNumber);

        var errorHeader = XlsxTestBuilder.BuildWorkbook(
        [
            new("Sheet1",
            [
                new(1,
                [
                    XlsxCellSpec.SharedString("externalId"),
                    XlsxCellSpec.ErrorCell("#NAME?"),
                    XlsxCellSpec.SharedString("email"),
                    XlsxCellSpec.SharedString("registeredAt"),
                    XlsxCellSpec.SharedString("isActive")
                ])
            ])
        ]);

        var ex2 = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(errorHeader));
        Assert.Equal("invalid_xlsx_header", ex2.Code);
        Assert.Equal(1, ex2.RowNumber);
    }

    [Theory]
    [InlineData("e", "#DIV/0!")]
    [InlineData("e", "#VALUE!")]
    [InlineData("b", "2")]
    [InlineData("n", "not-a-number")]
    [InlineData("customType", "anything")]
    public async Task UnsupportedAndErrorCells_ProduceRowErrorAndContinue(string cellType, string rawValue)
    {
        var bytes = XlsxTestBuilder.BuildSingleSheet(
            CustomerHeaders,
            [
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    new XlsxCellSpec(rawValue, cellType),
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.IsoDate("2026-01-15"),
                    XlsxCellSpec.Boolean(true)
                ],
                [
                    XlsxCellSpec.SharedString("ID-2"),
                    XlsxCellSpec.SharedString("Valid Name"),
                    XlsxCellSpec.SharedString("b@example.com"),
                    XlsxCellSpec.IsoDate("2026-01-15"),
                    XlsxCellSpec.Boolean(true)
                ]
            ]);

        var rows = await ReadAll(bytes);
        Assert.Equal(2, rows.Count);
        var error = Assert.Single(rows[0].Errors!);
        Assert.Equal("name", error.Field);
        Assert.Equal("unsupported_cell_value", error.Code);
        Assert.Empty(rows[1].Errors!);
    }

    [Fact]
    public async Task UserDefinedTabularRules_MapCustomHeadersAndReportLogicalFieldNameForFormulaAndTypes()
    {
        var definition = RuleDefinitions.Parse(JsonSerializer.SerializeToElement(new
        {
            definitionFormatVersion = 1,
            dataType = "Tabular",
            columns = new { unknown = "ignore" },
            keyFields = new[] { "productCode" },
            fields = new object[]
            {
                new { name = "productCode", header = "Код товара", type = "string", required = true, regexPattern = "^PRD-[0-9]{3}$" },
                new { name = "quantity", header = "Количество", type = "integer", required = true, minimum = 1, maximum = 100 },
                new { name = "price", header = "Цена", type = "decimal", required = true, minimum = 0.01m },
                new { name = "status", header = "Статус", type = "string", required = true, allowedValues = new[] { "NEW", "ACTIVE" } }
            }
        })).Definition;

        var parserSchema = RuleRowProcessor.ParserSchema(definition);
        var bytes = XlsxTestBuilder.BuildSingleSheet(
            ["Код товара", "Количество", "Цена", "Статус", "ЛишняяКолонка"],
            [
                [
                    XlsxCellSpec.SharedString("PRD-001"),
                    XlsxCellSpec.Number("10"),
                    XlsxCellSpec.Number("199.95"),
                    XlsxCellSpec.SharedString("ACTIVE"),
                    XlsxCellSpec.SharedString("ignored")
                ],
                [
                    XlsxCellSpec.SharedString("PRD-002"),
                    XlsxCellSpec.FormulaCell("5+5", "10", "n"),
                    XlsxCellSpec.Number("50"),
                    XlsxCellSpec.SharedString("NEW"),
                    XlsxCellSpec.SharedString("ignored")
                ],
                [
                    XlsxCellSpec.SharedString("PRD-001"),
                    XlsxCellSpec.Number("5"),
                    XlsxCellSpec.Number("10"),
                    XlsxCellSpec.SharedString("NEW"),
                    XlsxCellSpec.SharedString("ignored")
                ]
            ]);

        var rows = await ReadAll(bytes, schema: parserSchema);
        var ruleProcessor = new RuleRowProcessor(Guid.NewGuid(), definition, []);

        var first = ruleProcessor.Process(rows[0]);
        Assert.True(first.IsValid);
        Assert.True(first.IsTabular);

        var second = ruleProcessor.Process(rows[1]);
        Assert.False(second.IsValid);
        var formulaErr = Assert.Single(second.Errors);
        Assert.Equal("quantity", formulaErr.Field);
        Assert.Equal("formula_cell_not_supported", formulaErr.Code);

        var third = ruleProcessor.Process(rows[2]);
        Assert.False(third.IsValid);
        Assert.Contains(third.Errors, e => e.Field == "$row" && e.Code == "duplicate_key_in_file");
    }

    [Theory]
    [InlineData("empty_sheet", "empty_file")]
    [InlineData("missing_row_1", "empty_csv_header")]
    [InlineData("blank_header_cell", "empty_csv_header")]
    [InlineData("duplicate_header", "duplicate_csv_header")]
    [InlineData("duplicate_case_header", "duplicate_csv_header")]
    [InlineData("duplicate_trimmed_header", "duplicate_csv_header")]
    [InlineData("unknown_header", "unknown_csv_header")]
    [InlineData("missing_header", "missing_csv_header")]
    public async Task InvalidHeaders_AreControlledFileRejections(string scenario, string expectedCode)
    {
        var bytes = scenario switch
        {
            "empty_sheet" => XlsxTestBuilder.BuildWorkbook([new("Sheet1", [])]),
            "missing_row_1" => XlsxTestBuilder.BuildWorkbook([new("Sheet1", [new(2, [XlsxCellSpec.SharedString("ID-1")])])]),
            "blank_header_cell" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", " "], []),
            "duplicate_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", "name"], []),
            "duplicate_case_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", "NAME"], []),
            "duplicate_trimmed_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", " name ", "email", "registeredAt", " NAME "], []),
            "unknown_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", "secretColumn"], []),
            _ => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "isActive"], [])
        };

        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(bytes));
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, exception.RowNumber);
        Assert.DoesNotContain("secretColumn", exception.ToString());
    }

    [Fact]
    public async Task TrimmedHeaders_TrailingStyleOnlyCells_AndOoxmlRowBounds_AreHandledCleanly()
    {
        var styleOnly = new XlsxCellSpec(null, "s", StyleIndex: 0);
        var sheet = new XlsxSheetSpec(
            "Sheet1",
            [
                new(1,
                [
                    XlsxCellSpec.SharedString(" externalId "),
                    XlsxCellSpec.InlineString(" name "),
                    XlsxCellSpec.SharedString(" email "),
                    XlsxCellSpec.SharedString("\tregisteredAt\t"),
                    XlsxCellSpec.SharedString(" isActive "),
                    styleOnly,
                    styleOnly
                ]),
                new(2,
                [
                    XlsxCellSpec.SharedString(" ID-1 "),
                    XlsxCellSpec.SharedString(" Alice "),
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.IsoDate(" 2026-01-15 "),
                    XlsxCellSpec.RawBoolean(" 1 "),
                    styleOnly
                ]),
                new(3, [styleOnly, styleOnly, styleOnly, styleOnly, styleOnly, styleOnly])
            ]);

        var rows = await ReadAll(XlsxTestBuilder.BuildWorkbook([sheet]));
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsEmpty);
        Assert.Empty(rows[0].Errors!);
        Assert.Equal("2026-01-15", rows[0].Values["registeredAt"]);
        Assert.Equal("true", rows[0].Values["isActive"]);
        Assert.True(rows[1].IsEmpty);

        var valid = new CustomerFileRowProcessor(Guid.NewGuid(), Schema()).Process(rows[0]);
        Assert.True(valid.IsValid);
        Assert.Equal("ID-1", valid.Customer!.ExternalId);

        // Style-only cells beyond MaxColumns still trigger column_limit_exceeded
        var colLimitEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(XlsxTestBuilder.BuildWorkbook([sheet]), new ImportProcessingOptions { MaxColumns = 6 }));
        Assert.Equal("column_limit_exceeded", colLimitEx.Code);

        // Row numbers above the Excel worksheet ceiling (1,048,576) are rejected as invalid syntax
        var outOfBoundsSheet = new XlsxSheetSpec(
            "Sheet1",
            [
                new(1, CustomerHeaders.Select(h => XlsxCellSpec.SharedString(h)).ToArray()),
                new(1_048_577, [XlsxCellSpec.SharedString("ID-1")])
            ]);
        var rowBoundsEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(XlsxTestBuilder.BuildWorkbook([outOfBoundsSheet])));
        Assert.Equal("invalid_xlsx_syntax", rowBoundsEx.Code);

        // Max OOXML sparse jump (row 1 -> row 1,048,576) coalesces the entire gap into a single range ParsedRow
        var sparseSheet = new XlsxSheetSpec(
            "Sheet1",
            [
                new(1, CustomerHeaders.Select(h => XlsxCellSpec.SharedString(h)).ToArray()),
                new(1_048_576,
                [
                    XlsxCellSpec.SharedString("ID-MAX"),
                    XlsxCellSpec.SharedString("Sparse"),
                    XlsxCellSpec.SharedString("sparse@example.com"),
                    XlsxCellSpec.IsoDate("2026-01-15"),
                    XlsxCellSpec.Boolean(true)
                ])
            ]);
        var sparseRows = await ReadAll(
            XlsxTestBuilder.BuildWorkbook([sparseSheet]),
            new ImportProcessingOptions { MaxRows = 1 });
        Assert.Equal(2, sparseRows.Count);
        Assert.True(sparseRows[0].IsEmpty);
        Assert.Equal(2, sparseRows[0].RowNumber);
        Assert.Equal(1_048_575, sparseRows[0].CheckpointRowNumber);
        Assert.False(sparseRows[1].IsEmpty);
        Assert.Equal(1_048_576, sparseRows[1].RowNumber);
        Assert.Equal(1_048_576, sparseRows[1].CheckpointRowNumber);
    }

    [Fact]
    public async Task BlankRowsAndSkippedRowNumbers_EmitEmptyRowsForCheckpoint_WhileExplicitEmptyCellsAreValidated()
    {
        var sheet = new XlsxSheetSpec(
            "Sheet1",
            [
                new(1, CustomerHeaders.Select(h => XlsxCellSpec.SharedString(h)).ToArray()),
                new(2, []),
                new(4,
                [
                    XlsxCellSpec.InlineString(""),
                    XlsxCellSpec.InlineString(""),
                    XlsxCellSpec.InlineString(""),
                    XlsxCellSpec.InlineString(""),
                    XlsxCellSpec.InlineString("")
                ]),
                new(5,
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    XlsxCellSpec.SharedString("Alice"),
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 1, 15)),
                    XlsxCellSpec.Boolean(true)
                ])
            ]);

        var rows = await ReadAll(XlsxTestBuilder.BuildWorkbook([sheet]));
        Assert.Equal(new[] { 2, 3, 4, 5 }, rows.Select(r => r.RowNumber));
        Assert.True(rows[0].IsEmpty);
        Assert.True(rows[1].IsEmpty);
        Assert.False(rows[2].IsEmpty);
        Assert.False(rows[3].IsEmpty);

        var processor = new CustomerFileRowProcessor(Guid.NewGuid(), Schema());
        var emptyCellsResult = processor.Process(rows[2]);
        Assert.Equal(4, emptyCellsResult.Errors.Count);
        Assert.All(emptyCellsResult.Errors, e => Assert.Equal("required", e.Code));
        Assert.True(processor.Process(rows[3]).IsValid);
    }

    [Fact]
    public async Task ExtraColumnsBeyondHeader_ProduceColumnCountMismatchRowError()
    {
        var bytes = XlsxTestBuilder.BuildSingleSheet(
            CustomerHeaders,
            [
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    XlsxCellSpec.SharedString("Alice"),
                    XlsxCellSpec.SharedString("a@example.com"),
                    XlsxCellSpec.IsoDate("2026-01-15"),
                    XlsxCellSpec.Boolean(true),
                    XlsxCellSpec.SharedString("ExtraValue")
                ]
            ]);

        var row = Assert.Single(await ReadAll(bytes));
        var error = Assert.Single(row.Errors!);
        Assert.Equal("$row", error.Field);
        Assert.Equal("column_count_mismatch", error.Code);
        using var raw = JsonDocument.Parse(row.RawData);
        Assert.Equal(6, raw.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ZipEntryLimit_RejectsArchiveWithTooManyEntries()
    {
        var bytes = XlsxTestBuilder.BuildSingleSheet(CustomerHeaders, [], extraZipEntries: 10);
        var ex = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(bytes, new ImportProcessingOptions { MaxZipEntries = 10 }));
        Assert.Equal("xlsx_zip_entry_limit_exceeded", ex.Code);
    }

    [Fact]
    public async Task UncompressedSizeLimit_RejectsOversizedArchiveEntries()
    {
        var rows = Enumerable.Range(1, 50).Select(i => (IReadOnlyList<XlsxCellSpec>)
        [
            XlsxCellSpec.InlineString($"ID-{i}"),
            XlsxCellSpec.InlineString(new string('A', 200)),
            XlsxCellSpec.InlineString($"u{i}@example.com"),
            XlsxCellSpec.IsoDate("2026-01-15"),
            XlsxCellSpec.Boolean(true)
        ]).ToArray();

        var bytes = XlsxTestBuilder.BuildSingleSheet(CustomerHeaders, rows);
        var ex = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(bytes, new ImportProcessingOptions { MaxUncompressedBytes = 2_048 }));
        Assert.Equal("xlsx_uncompressed_limit_exceeded", ex.Code);
    }

    [Fact]
    public async Task RowColumnAndCellLimits_AreEnforcedForXlsx()
    {
        var twoRows = XlsxTestBuilder.BuildSingleSheet(
            ["id"],
            [
                [XlsxCellSpec.SharedString("1")],
                [XlsxCellSpec.SharedString("2")],
                [XlsxCellSpec.SharedString("3")]
            ]);

        var rowEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(twoRows, new ImportProcessingOptions { MaxRows = 2 }, SimpleSchema()));
        Assert.Equal("row_limit_exceeded", rowEx.Code);
        Assert.Equal(4, rowEx.RowNumber);

        var colHeaderEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(XlsxTestBuilder.BuildSingleSheet(["id", "extra"], []),
                new ImportProcessingOptions { MaxColumns = 1 }, SimpleSchema()));
        Assert.Equal("column_limit_exceeded", colHeaderEx.Code);
        Assert.Equal(1, colHeaderEx.RowNumber);

        var cellEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(XlsxTestBuilder.BuildSingleSheet(["id"], [[XlsxCellSpec.SharedString("abcd")]]),
                new ImportProcessingOptions { MaxCellLength = 3 }, SimpleSchema()));
        Assert.Equal("cell_limit_exceeded", cellEx.Code);
        Assert.Equal(2, cellEx.RowNumber);

        var fileEx = await Assert.ThrowsAsync<ImportFileException>(() =>
            ReadAll(twoRows, new ImportProcessingOptions { MaxFileBytes = 1024 }, SimpleSchema()));
        Assert.Equal("file_too_large", fileEx.Code);
    }

    [Fact]
    public async Task CorruptedOrEmptyBytes_AreRejectedWithSafeFileErrorCodes()
    {
        var emptyEx = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(Array.Empty<byte>()));
        Assert.Equal("empty_file", emptyEx.Code);

        var corruptEx = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll("not-a-zip-archive"u8.ToArray()));
        Assert.Equal("invalid_xlsx_archive", corruptEx.Code);

        using var emptyZipStream = new MemoryStream();
        using (new ZipArchive(emptyZipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        var emptyZipEx = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(emptyZipStream.ToArray()));
        Assert.Equal("invalid_xlsx_archive", emptyZipEx.Code);
    }

    [Theory]
    [InlineData("empty_file")]
    [InlineData("empty_csv_header")]
    [InlineData("duplicate_csv_header")]
    [InlineData("unknown_csv_header")]
    [InlineData("missing_csv_header")]
    [InlineData("row_limit_exceeded")]
    [InlineData("column_limit_exceeded")]
    [InlineData("cell_limit_exceeded")]
    public async Task SharedFixtures_BothFormatsProduceSameFileRejectionCodes(string scenario)
    {
        var options = scenario switch
        {
            "row_limit_exceeded" => new ImportProcessingOptions { MaxRows = 1 },
            "column_limit_exceeded" => new ImportProcessingOptions { MaxColumns = 4 },
            "cell_limit_exceeded" => new ImportProcessingOptions { MaxCellLength = 5 },
            _ => new ImportProcessingOptions()
        };

        var csvBytes = scenario switch
        {
            "empty_file" => Array.Empty<byte>(),
            "empty_csv_header" => "externalId,name,email,registeredAt,\n"u8.ToArray(),
            "duplicate_csv_header" => "externalId,name,email,registeredAt,name\n"u8.ToArray(),
            "unknown_csv_header" => "externalId,name,email,registeredAt,unknown\n"u8.ToArray(),
            "missing_csv_header" => "externalId,name,email,isActive\n"u8.ToArray(),
            "row_limit_exceeded" => Encoding.UTF8.GetBytes(
                "externalId,name,email,registeredAt,isActive\n1,A,a@b.com,2026-01-01,true\n2,B,b@b.com,2026-01-01,true\n"),
            "column_limit_exceeded" => "externalId,name,email,registeredAt,isActive\n"u8.ToArray(),
            _ => "id\ntoolong\n"u8.ToArray()
        };

        var xlsxBytes = scenario switch
        {
            "empty_file" => Array.Empty<byte>(),
            "empty_csv_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", ""], []),
            "duplicate_csv_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", "name"], []),
            "unknown_csv_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "registeredAt", "unknown"], []),
            "missing_csv_header" => XlsxTestBuilder.BuildSingleSheet(["externalId", "name", "email", "isActive"], []),
            "row_limit_exceeded" => XlsxTestBuilder.BuildSingleSheet(
                CustomerHeaders,
                [
                    [XlsxCellSpec.SharedString("1"), XlsxCellSpec.SharedString("A"), XlsxCellSpec.SharedString("a@b.com"), XlsxCellSpec.IsoDate("2026-01-01"), XlsxCellSpec.Boolean(true)],
                    [XlsxCellSpec.SharedString("2"), XlsxCellSpec.SharedString("B"), XlsxCellSpec.SharedString("b@b.com"), XlsxCellSpec.IsoDate("2026-01-01"), XlsxCellSpec.Boolean(true)]
                ]),
            "column_limit_exceeded" => XlsxTestBuilder.BuildSingleSheet(CustomerHeaders, []),
            _ => XlsxTestBuilder.BuildSingleSheet(["id"], [[XlsxCellSpec.SharedString("toolong")]])
        };

        var targetSchema = scenario == "cell_limit_exceeded" ? SimpleSchema() : Schema();
        var csvParser = new CsvRowParser(Options.Create(options));
        var xlsxParser = Parser(options);

        var csvEx = await Assert.ThrowsAsync<ImportFileException>(async () =>
        {
            await using var s = new MemoryStream(csvBytes);
            await foreach (var _ in csvParser.ParseAsync(s, targetSchema))
            {
            }
        });

        var xlsxEx = await Assert.ThrowsAsync<ImportFileException>(async () =>
        {
            await using var s = new MemoryStream(xlsxBytes);
            await foreach (var _ in xlsxParser.ParseAsync(s, targetSchema))
            {
            }
        });

        Assert.Equal(scenario, csvEx.Code);
        Assert.Equal(csvEx.Code, xlsxEx.Code);
        Assert.Equal(csvEx.RowNumber, xlsxEx.RowNumber);
    }

    [Fact]
    public async Task SharedFixtures_TypesAndDuplicatesProduceIdenticalNormalizedResultsAcrossCsvAndXlsx()
    {
        var csvText = string.Join("\n",
            "externalId,name,email,registeredAt,isActive",
            "ID-1,Alice,alice@example.com,2026-01-15,true",
            "ID-2,Bob,not-an-email,2026-02-30,yes",
            "ID-1,Alice Duplicate,alice2@example.com,2026-01-15,false");

        var xlsxBytes = XlsxTestBuilder.BuildSingleSheet(
            CustomerHeaders,
            [
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    XlsxCellSpec.SharedString("Alice"),
                    XlsxCellSpec.SharedString("alice@example.com"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 1, 15)),
                    XlsxCellSpec.Boolean(true)
                ],
                [
                    XlsxCellSpec.SharedString("ID-2"),
                    XlsxCellSpec.SharedString("Bob"),
                    XlsxCellSpec.SharedString("not-an-email"),
                    XlsxCellSpec.SharedString("2026-02-30"),
                    XlsxCellSpec.SharedString("yes")
                ],
                [
                    XlsxCellSpec.SharedString("ID-1"),
                    XlsxCellSpec.SharedString("Alice Duplicate"),
                    XlsxCellSpec.SharedString("alice2@example.com"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 1, 15)),
                    XlsxCellSpec.Boolean(false)
                ]
            ]);

        var sourceId = Guid.NewGuid();
        var schema = Schema();
        var csvProcessor = new CustomerFileRowProcessor(sourceId, schema);
        var xlsxProcessor = new CustomerFileRowProcessor(sourceId, schema);

        await using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes(csvText));
        var csvResults = new List<CustomerRowValidation>();
        await foreach (var row in new CsvRowParser(Options.Create(new ImportProcessingOptions())).ParseAsync(csvStream, schema))
        {
            csvResults.Add(csvProcessor.Process(row));
        }

        var xlsxResults = (await ReadAll(xlsxBytes, schema: schema)).Select(xlsxProcessor.Process).ToList();

        Assert.Equal(3, csvResults.Count);
        Assert.Equal(csvResults.Count, xlsxResults.Count);
        for (var i = 0; i < csvResults.Count; i++)
        {
            Assert.Equal(csvResults[i].IsValid, xlsxResults[i].IsValid);
            Assert.Equal(csvResults[i].NaturalKey, xlsxResults[i].NaturalKey);
            Assert.Equal(csvResults[i].RowHash, xlsxResults[i].RowHash);
            Assert.Equal(csvResults[i].Customer, xlsxResults[i].Customer);
            Assert.Equal(csvResults[i].Errors, xlsxResults[i].Errors);
        }
    }

    private static XlsxRowParser Parser(ImportProcessingOptions? options = null) =>
        new(Options.Create(options ?? new ImportProcessingOptions()));

    private static DataSourceSchema SimpleSchema() => new(1, ["id"], [new("id", "string", true)]);

    private static DataSourceSchema Schema() => new(1, ["externalId"],
    [
        new("externalId", "string", true, 80),
        new("name", "string", true, 200),
        new("email", "email", true, 254),
        new("registeredAt", "date", false, format: "yyyy-MM-dd"),
        new("isActive", "boolean", true)
    ]);

    private static async Task<List<ParsedRow>> ReadAll(byte[] bytes, ImportProcessingOptions? options = null, DataSourceSchema? schema = null)
    {
        await using var stream = new MemoryStream(bytes);
        return await ReadAll(stream, options, schema);
    }

    private static async Task<List<ParsedRow>> ReadAll(Stream stream, ImportProcessingOptions? options = null, DataSourceSchema? schema = null)
    {
        var rows = new List<ParsedRow>();
        await foreach (var row in Parser(options).ParseAsync(stream, schema ?? Schema()))
        {
            rows.Add(row);
        }

        return rows;
    }
}
