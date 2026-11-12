using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dashboard;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Rules;
using DataFlow.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class Stage9XlsxReportsAndDashboardTests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task EquivalentCsvAndXlsxThousandRows_ProduceIdenticalBusinessResultsAndErrorsCsv()
    {
        var storage = new FileUploadsStage5Tests.FakeStorage();
        await using var factory = fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IFileStorage>(storage))));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });

        var csvSource = await CreateCustomerSourceAsync(client, "Customer CSV");
        var xlsxSource = await CreateCustomerSourceAsync(client, "Customer XLSX");

        var csvSeed = await UploadBytesAsync(client, csvSource.Id, MixedCustomerCsv.Seed(), "seed.csv", "csv-seed");
        var xlsxSeed = await UploadBytesAsync(client, xlsxSource.Id, MixedCustomerXlsx.Seed(), "seed.xlsx", "xlsx-seed");

        await ProcessJobAsync(storage, csvSeed.JobId);
        await ProcessJobAsync(storage, xlsxSeed.JobId);

        var csvMixed = await UploadBytesAsync(client, csvSource.Id, MixedCustomerCsv.File(), "mixed.csv", "csv-mixed");
        var xlsxMixed = await UploadBytesAsync(client, xlsxSource.Id, MixedCustomerXlsx.File(), "mixed.xlsx", "xlsx-mixed");

        await ProcessJobAsync(storage, csvMixed.JobId);
        await ProcessJobAsync(storage, xlsxMixed.JobId);

        var csvJob = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{csvMixed.JobId}"))!;
        var xlsxJob = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{xlsxMixed.JobId}"))!;

        Assert.Equal("CompletedWithErrors", csvJob.Status);
        Assert.Equal(csvJob.Status, xlsxJob.Status);
        Assert.Equal(MixedCustomerCsv.Total, xlsxJob.TotalRows);
        Assert.Equal(csvJob.ProcessedRows, xlsxJob.ProcessedRows);
        Assert.Equal(csvJob.InsertedRows, xlsxJob.InsertedRows);
        Assert.Equal(csvJob.UpdatedRows, xlsxJob.UpdatedRows);
        Assert.Equal(csvJob.DuplicateRows, xlsxJob.DuplicateRows);
        Assert.Equal(csvJob.InvalidRows, xlsxJob.InvalidRows);

        await using var db = fixture.CreateContext();
        var csvCustomers = await db.Customers.AsNoTracking()
            .Where(c => c.DataSourceId == csvSource.Id)
            .OrderBy(c => c.ExternalId)
            .ToListAsync();
        var xlsxCustomers = await db.Customers.AsNoTracking()
            .Where(c => c.DataSourceId == xlsxSource.Id)
            .OrderBy(c => c.ExternalId)
            .ToListAsync();

        Assert.Equal(300, csvCustomers.Count);
        Assert.Equal(csvCustomers.Count, xlsxCustomers.Count);
        for (var i = 0; i < csvCustomers.Count; i++)
        {
            Assert.Equal(csvCustomers[i].ExternalId, xlsxCustomers[i].ExternalId);
            Assert.Equal(csvCustomers[i].Name, xlsxCustomers[i].Name);
            Assert.Equal(csvCustomers[i].Email, xlsxCustomers[i].Email);
            Assert.Equal(csvCustomers[i].RegisteredAt, xlsxCustomers[i].RegisteredAt);
            Assert.Equal(csvCustomers[i].IsActive, xlsxCustomers[i].IsActive);
            Assert.Equal(csvCustomers[i].RowHash, xlsxCustomers[i].RowHash);
        }

        var csvRows = await db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == csvMixed.JobId)
            .OrderBy(r => r.RowNumber)
            .ToListAsync();
        var xlsxRows = await db.ImportRows.AsNoTracking()
            .Where(r => r.ImportJobId == xlsxMixed.JobId)
            .OrderBy(r => r.RowNumber)
            .ToListAsync();

        Assert.Equal(1000, xlsxRows.Count);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(csvRows[i].RowNumber, xlsxRows[i].RowNumber);
            Assert.Equal(csvRows[i].Status, xlsxRows[i].Status);
            Assert.Equal(csvRows[i].RowHash, xlsxRows[i].RowHash);
            Assert.Equal(csvRows[i].GetErrors(), xlsxRows[i].GetErrors());
        }

        // Verify streaming CSV error report over 700 invalid rows (> 500 batch boundary)
        using var reportResponse = await client.GetAsync($"/api/v1/import-jobs/{xlsxMixed.JobId}/errors.csv");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        Assert.Equal("text/csv", reportResponse.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"import-job-{xlsxMixed.JobId:N}-errors.csv", reportResponse.Content.Headers.ContentDisposition!.FileName);

        var reportText = await reportResponse.Content.ReadAsStringAsync();
        var parsedReport = ParseReportCsv(reportText);
        Assert.Equal(700, parsedReport.Count);
        Assert.Equal("5", parsedReport[0].RowNumber);
        Assert.Equal("name", parsedReport[0].Field);
        Assert.Equal("required", parsedReport[0].Code);
    }

    [Fact]
    public async Task UserDefinedRules_CsvAndXlsxProduceSameValidAndInvalidOutcomes()
    {
        var storage = new FileUploadsStage5Tests.FakeStorage();
        await using var factory = fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IFileStorage>(storage))));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });

        var ruleDef = JsonSerializer.SerializeToElement(new
        {
            definitionFormatVersion = 1,
            dataType = "Tabular",
            csv = new { delimiter = ";" },
            columns = new { unknown = "ignore" },
            keyFields = new[] { "orderCode" },
            fields = new object[]
            {
                new { name = "orderCode", header = "Код заказа", type = "string", required = true, regexPattern = "^ORD-[0-9]{3}$" },
                new { name = "quantity", header = "Количество", type = "integer", required = true, minimum = 1, maximum = 50 },
                new { name = "total", header = "Сумма", type = "decimal", required = true, minimum = 10.5m, maximum = 1000m },
                new { name = "orderDate", header = "Дата", type = "date", required = true, format = "yyyy-MM-dd" },
                new { name = "paid", header = "Оплачен", type = "boolean", required = true },
                new { name = "tier", header = "Тариф", type = "string", required = true, allowedValues = new[] { "STD", "PRO" } }
            }
        });

        using var createdRule = await client.PostAsJsonAsync("/api/v1/rule-sets", new SaveRuleSetRequest("Orders Rule", "Custom tabular rules", ruleDef));
        createdRule.EnsureSuccessStatusCode();
        var version = (await createdRule.Content.ReadFromJsonAsync<RuleVersionDto>())!;

        using var createdSource = await client.PostAsJsonAsync("/api/v1/rule-sources", new { name = "Orders Source", ruleSetVersionId = version.Id });
        createdSource.EnsureSuccessStatusCode();
        var sourceId = (await createdSource.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var csvContent = string.Join("\n",
            "Код заказа;Количество;Сумма;Дата;Оплачен;Тариф;Примечание",
            "ORD-001;5;120.50;2026-03-10;true;PRO;ok",
            "ORD-002;100;120.50;2026-03-10;false;STD;too many",
            "BAD-003;5;120.50;2026-03-10;true;VIP;bad regex and tier",
            "ORD-001;2;50.00;2026-03-11;true;STD;duplicate key");

        var xlsxBytes = XlsxTestBuilder.BuildSingleSheet(
            ["Код заказа", "Количество", "Сумма", "Дата", "Оплачен", "Тариф", "Примечание"],
            [
                [
                    XlsxCellSpec.SharedString("ORD-001"),
                    XlsxCellSpec.Number("5"),
                    XlsxCellSpec.Number("120.50"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 3, 10)),
                    XlsxCellSpec.Boolean(true),
                    XlsxCellSpec.SharedString("PRO"),
                    XlsxCellSpec.SharedString("ok")
                ],
                [
                    XlsxCellSpec.SharedString("ORD-002"),
                    XlsxCellSpec.Number("100"),
                    XlsxCellSpec.Number("120.50"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 3, 10)),
                    XlsxCellSpec.Boolean(false),
                    XlsxCellSpec.SharedString("STD"),
                    XlsxCellSpec.SharedString("too many")
                ],
                [
                    XlsxCellSpec.SharedString("BAD-003"),
                    XlsxCellSpec.Number("5"),
                    XlsxCellSpec.Number("120.50"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 3, 10)),
                    XlsxCellSpec.Boolean(true),
                    XlsxCellSpec.SharedString("VIP"),
                    XlsxCellSpec.SharedString("bad regex and tier")
                ],
                [
                    XlsxCellSpec.SharedString("ORD-001"),
                    XlsxCellSpec.Number("2"),
                    XlsxCellSpec.Number("50.00"),
                    XlsxCellSpec.DateSerial(new DateOnly(2026, 3, 11)),
                    XlsxCellSpec.Boolean(true),
                    XlsxCellSpec.SharedString("STD"),
                    XlsxCellSpec.SharedString("duplicate key")
                ]
            ]);

        var csvJobAccepted = await UploadBytesAsync(client, sourceId, Encoding.UTF8.GetBytes(csvContent), "orders.csv", "orders-csv", version.Id);
        var xlsxJobAccepted = await UploadBytesAsync(client, sourceId, xlsxBytes, "orders.xlsx", "orders-xlsx", version.Id);

        await ProcessJobAsync(storage, csvJobAccepted.JobId);
        await ProcessJobAsync(storage, xlsxJobAccepted.JobId);

        var csvJob = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{csvJobAccepted.JobId}"))!;
        var xlsxJob = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{xlsxJobAccepted.JobId}"))!;

        Assert.Equal("CompletedWithErrors", csvJob.Status);
        Assert.Equal("CompletedWithErrors", xlsxJob.Status);
        Assert.Equal(1, csvJob.ValidRows);
        Assert.Equal(1, xlsxJob.ValidRows);
        Assert.Equal(3, csvJob.InvalidRows);
        Assert.Equal(3, xlsxJob.InvalidRows);

        var csvRows = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>($"/api/v1/import-jobs/{csvJobAccepted.JobId}/rows"))!.Items;
        var xlsxRows = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>($"/api/v1/import-jobs/{xlsxJobAccepted.JobId}/rows"))!.Items;

        Assert.Equal(csvRows.Count, xlsxRows.Count);
        for (var i = 0; i < csvRows.Count; i++)
        {
            Assert.Equal(csvRows[i].RowNumber, xlsxRows[i].RowNumber);
            Assert.Equal(csvRows[i].Status, xlsxRows[i].Status);
            Assert.Equal(csvRows[i].NaturalKey, xlsxRows[i].NaturalKey);
            Assert.Equal(csvRows[i].Errors, xlsxRows[i].Errors);
        }

        await using var db = fixture.CreateContext();
        var finishedOutbox = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Type == "import.job.finished")
            .ToListAsync();
        var csvFinishedPayload = finishedOutbox
            .Select(m => JsonDocument.Parse(m.Payload).RootElement.Clone())
            .Single(p => p.GetProperty("jobId").GetGuid() == csvJobAccepted.JobId);
        Assert.Equal(1, csvFinishedPayload.GetProperty("ValidRows").GetInt64());
    }

    [Fact]
    public async Task UploadEndpoint_EnforcesImportMaxFileBytesForXlsx_BeforeWritingStorage()
    {
        var storage = new FileUploadsStage5Tests.FakeStorage();
        await using var factory = fixture.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:MaxFileBytes"] = "65536",
                ["Import:MaxFileBytes"] = "1024"
            }));
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IFileStorage>(storage)));
        });
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var source = await CreateCustomerSourceAsync(client, "XLSX Size Limit");

        var options = await client.GetFromJsonAsync<JsonElement>("/api/v1/files/options");
        Assert.Equal(65536, options.GetProperty("maxFileBytes").GetInt64());
        Assert.Equal(1024, options.GetProperty("maxXlsxFileBytes").GetInt64());

        var oversizedXlsx = MixedCustomerXlsx.Seed();
        Assert.True(oversizedXlsx.Length > 1024 && oversizedXlsx.Length < 65536);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(oversizedXlsx), "file", "oversized.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{source.Id}/files") { Content = content };
        request.Headers.Add("Idempotency-Key", "oversized-xlsx");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("file_too_large", problem.GetProperty("code").GetString());
        Assert.Equal(0, storage.Uploads);
    }

    [Fact]
    public async Task ErrorsCsvEndpoint_NeutralizesSpreadsheetFormulaInjection_AndQuotesRfc4180()
    {
        await using var db = fixture.CreateContext();
        var ownerId = Guid.NewGuid();
        var source = DataSource.Create("Formula Guard", DataSourceDataType.Customer, AcceptedFileFormat.All,
            new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true)]), DateTimeOffset.UtcNow);
        source.AssignOwner(ownerId);
        var job = ImportJob.Create(source.Id, "formulas.xlsx", Guid.NewGuid().ToString("N"), new string('f', 64), source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        db.ImportJobs.Add(job);

        var dangerousCells = new[]
        {
            "=cmd|' /C calc'!A0",
            "+SUM(A1:A2)",
            "-2+3",
            "@HYPERLINK(\"https://evil.example\")",
            "   =WEBSERVICE(\"https://evil.example\")",
            "\t+1",
            "\n=1+1",
            "Line1,\r\n\"Quoted\""
        };

        db.ImportRows.Add(ImportRow.Create(
            job.Id,
            2,
            ImportRowStatus.Invalid,
            JsonSerializer.Serialize(dangerousCells),
            "k1",
            [
                new ImportRowError("=field", "+code", "-message, with \"quotes\"\nand newline"),
                new ImportRowError("  @field2", "formula_cell_not_supported", "Formula cells are not supported; provide literal values.")
            ],
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync();

        using var ownerClient = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        ownerClient.DefaultRequestHeaders.Add("X-Test-User", ownerId.ToString());

        using var response = await ownerClient.GetAsync($"/api/v1/import-jobs/{job.Id}/errors.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        var records = ParseReportCsv(csv);
        Assert.Equal(2, records.Count);

        Assert.Equal("2", records[0].RowNumber);
        Assert.Equal("'=field", records[0].Field);
        Assert.Equal("'+code", records[0].Code);
        Assert.Equal("'-message, with \"quotes\"\nand newline", records[0].Message);

        Assert.Equal("2", records[1].RowNumber);
        Assert.Equal("'@field2", records[1].Field);
        Assert.Equal("formula_cell_not_supported", records[1].Code);

        var rawItems = JsonSerializer.Deserialize<string[]>(records[0].RawData)!;
        Assert.Equal("'=cmd|' /C calc'!A0", rawItems[0]);
        Assert.Equal("'+SUM(A1:A2)", rawItems[1]);
        Assert.Equal("'-2+3", rawItems[2]);
        Assert.Equal("'@HYPERLINK(\"https://evil.example\")", rawItems[3]);
        Assert.Equal("'   =WEBSERVICE(\"https://evil.example\")", rawItems[4]);
        Assert.Equal("'\t+1", rawItems[5]);
        Assert.Equal("'\n=1+1", rawItems[6]);
        Assert.Equal("Line1,\r\n\"Quoted\"", rawItems[7]);

        // Another user or missing job must receive 404 Problem Details
        using var otherClient = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        otherClient.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        using var forbidden = await otherClient.GetAsync($"/api/v1/import-jobs/{job.Id}/errors.csv");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

        using var missing = await ownerClient.GetAsync($"/api/v1/import-jobs/{Guid.NewGuid()}/errors.csv");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task DashboardSummary_CalculatesMetricsByPeriod_AndIsolatesUsers()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = fixture.CreateContext())
        {
            var schema = new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true)]);
            var source = DataSource.Create("CRM Клиенты", DataSourceDataType.Customer, AcceptedFileFormat.All, schema, now.AddDays(-20));
            source.AssignOwner(userId);

            var otherSource = DataSource.Create("Чужой источник", DataSourceDataType.Customer, AcceptedFileFormat.All, schema, now);
            otherSource.AssignOwner(otherUserId);

            db.DataSources.AddRange(source, otherSource);

            // 1. Completed job today (XLSX): 10 inserted, 5 updated, 2 duplicate
            var completedToday = ImportJob.Create(source.Id, "today_clients.xlsx", "k1", new string('1', 64), source.SchemaJson, 1, now.AddMinutes(-30));
            completedToday.AcquireLease("w", now.AddMinutes(-29), 60);
            completedToday.RecordBatchCompletion(18, 10, 5, 0, 2, now.AddMinutes(-28));
            completedToday.SetTotalRows(17, now.AddMinutes(-28));
            completedToday.Complete(now.AddMinutes(-28));

            // 2. CompletedWithErrors job 3 days ago (CSV, Tabular with ValidRows): 8 valid, 3 invalid
            var errorsWeek = ImportJob.Create(source.Id, "week_tabular.csv", "k2", new string('2', 64), source.SchemaJson, 1, now.AddDays(-3));
            errorsWeek.AcquireLease("w", now.AddDays(-3), 60);
            errorsWeek.RecordBatchCompletion(12, 0, 0, 3, 0, now.AddDays(-3), valid: 8);
            errorsWeek.SetTotalRows(11, now.AddDays(-3));
            errorsWeek.Complete(now.AddDays(-3));

            // 3. Failed job 2 days ago (XLSX): 4 inserted before failure
            var failedWeek = ImportJob.Create(source.Id, "failed_import.xlsx", "k3", new string('3', 64), source.SchemaJson, 1, now.AddDays(-2));
            failedWeek.AcquireLease("w", now.AddDays(-2), 60);
            failedWeek.RecordBatchCompletion(5, 4, 0, 0, 0, now.AddDays(-2));
            failedWeek.Fail("invalid_xlsx_syntax", "The import file cannot be processed.", false, now.AddDays(-2));

            // 4. Processing job today (CSV): 6 inserted so far
            var processingToday = ImportJob.Create(source.Id, "active_stream.csv", "k4", new string('4', 64), source.SchemaJson, 1, now.AddMinutes(-10));
            processingToday.AcquireLease("w", now.AddMinutes(-9), 60);
            processingToday.RecordBatchCompletion(7, 6, 0, 0, 0, now.AddMinutes(-8));

            // 5. Queued job 15 days ago (outside week, inside month)
            var queuedMonth = ImportJob.Create(source.Id, "older_queued.csv", "k5", new string('5', 64), source.SchemaJson, 1, now.AddDays(-15));

            // 6. Other user's job today (must not leak)
            var otherJob = ImportJob.Create(otherSource.Id, "other.csv", "k6", new string('6', 64), otherSource.SchemaJson, 1, now.AddMinutes(-5));

            db.ImportJobs.AddRange(completedToday, errorsWeek, failedWeek, processingToday, queuedMonth, otherJob);
            await db.SaveChangesAsync();
        }

        using var client = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Test-User", userId.ToString());

        var weekSummary = (await client.GetFromJsonAsync<DashboardSummaryDto>("/api/v1/dashboard/summary?period=week&recentLimit=3"))!;
        Assert.Equal("week", weekSummary.Period);
        Assert.Equal(4, weekSummary.TotalJobs);
        Assert.Equal(1, weekSummary.CompletedJobs);
        Assert.Equal(1, weekSummary.CompletedWithErrorsJobs);
        Assert.Equal(1, weekSummary.FailedJobs);
        Assert.Equal(0, weekSummary.CancelledJobs);
        Assert.Equal(2, weekSummary.AttentionJobs);
        Assert.Equal(1, weekSummary.ActiveJobs);
        Assert.Equal(0, weekSummary.QueuedJobs);
        Assert.Equal(1, weekSummary.ProcessingJobs);
        Assert.Equal(66.67, weekSummary.CompletionSuccessRate);
        Assert.Equal(38, weekSummary.ProcessedRows);
        Assert.Equal(20, weekSummary.InsertedRows);
        Assert.Equal(5, weekSummary.UpdatedRows);
        Assert.Equal(3, weekSummary.InvalidRows);
        Assert.Equal(2, weekSummary.DuplicateRows);
        Assert.Equal(8, weekSummary.ValidRows);
        Assert.Equal(35, weekSummary.AcceptedRows);
        Assert.Equal(3, weekSummary.RecentJobs.Count);
        Assert.Equal("active_stream.csv", weekSummary.RecentJobs[0].OriginalFileName);
        Assert.Equal("CSV", weekSummary.RecentJobs[0].Format);
        Assert.Equal("today_clients.xlsx", weekSummary.RecentJobs[1].OriginalFileName);
        Assert.Equal("XLSX", weekSummary.RecentJobs[1].Format);

        var todaySummary = (await client.GetFromJsonAsync<DashboardSummaryDto>("/api/v1/dashboard/summary?period=today"))!;
        Assert.Equal(2, todaySummary.TotalJobs);
        Assert.Equal(1, todaySummary.CompletedJobs);
        Assert.Equal(1, todaySummary.ProcessingJobs);
        Assert.Equal(100, todaySummary.CompletionSuccessRate);

        var monthSummary = (await client.GetFromJsonAsync<DashboardSummaryDto>("/api/v1/dashboard/summary?period=month"))!;
        Assert.Equal(5, monthSummary.TotalJobs);
        Assert.Equal(1, monthSummary.QueuedJobs);
        Assert.Equal(2, monthSummary.ActiveJobs);

        using var badPeriod = await client.GetAsync("/api/v1/dashboard/summary?period=year");
        Assert.Equal(HttpStatusCode.BadRequest, badPeriod.StatusCode);

        using var badLimit = await client.GetAsync("/api/v1/dashboard/summary?recentLimit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    private static async Task<DataSourceDto> CreateCustomerSourceAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/data-sources", new
        {
            name,
            acceptedFormats = new[] { "Csv", "Xlsx" },
            schema = new
            {
                keyFields = new[] { "externalId" },
                fields = new object[]
                {
                    new { name = "externalId", type = "string", required = true, maxLength = 80 },
                    new { name = "name", type = "string", required = true, maxLength = 40 },
                    new { name = "email", type = "email", required = true, maxLength = 254 },
                    new { name = "registeredAt", type = "date", required = false, format = "yyyy-MM-dd" },
                    new { name = "isActive", type = "boolean", required = true }
                }
            }
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DataSourceDto>())!;
    }

    private static async Task<JobAcceptedDto> UploadBytesAsync(
        HttpClient client,
        Guid sourceId,
        byte[] bytes,
        string fileName,
        string idempotencyKey,
        Guid? ruleSetVersionId = null)
    {
        var url = ruleSetVersionId is null
            ? $"/api/v1/data-sources/{sourceId}/files"
            : $"/api/v1/data-sources/{sourceId}/files?ruleSetVersionId={ruleSetVersionId}";

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
    }

    private async Task ProcessJobAsync(FileUploadsStage5Tests.FakeStorage storage, Guid jobId)
    {
        await using var db = fixture.CreateContext();
        var options = Options.Create(new ImportProcessingOptions());
        var reader = new FakeStorageReader(storage);
        var processor = new CsvImportProcessor(
            db,
            new ImportJobStore(db),
            reader,
            new CsvRowParser(options),
            options,
            NullLogger<CsvImportProcessor>.Instance,
            new XlsxRowParser(options));
        await processor.ProcessAsync(jobId, 1, Guid.NewGuid(), "stage9-worker", CancellationToken.None);
    }

    private static List<ErrorCsvRow> ParseReportCsv(string csvText)
    {
        using var stringReader = new StringReader(csvText);
        using var csv = new CsvReader(stringReader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            TrimOptions = TrimOptions.None,
            PrepareHeaderForMatch = args => args.Header.ToLowerInvariant()
        });
        return csv.GetRecords<ErrorCsvRow>().ToList();
    }

    private sealed record ErrorCsvRow(string RowNumber, string Field, string Code, string Message, string RawData);

    private sealed class FakeStorageReader(FileUploadsStage5Tests.FakeStorage storage) : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(storage.Objects[objectKey]));
    }
}
