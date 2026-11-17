using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataFlow.IntegrationTests;

// Stage 12 acceptance: the public demo path from samples/ through the real
// stack (PostgreSQL, RabbitMQ, MinIO, no fakes), plus the documented behavior
// when an upload exceeds the configured row limit.
public sealed class AcceptanceStage12Tests(DataSourcesApiFixture postgres, RabbitMqFixture rabbit, MinioFixture minio) :
    IClassFixture<DataSourcesApiFixture>, IClassFixture<RabbitMqFixture>, IClassFixture<MinioFixture>
{
    // (rowNumber, field, code) of samples/customers-with-errors.csv; field "$row"
    // marks an in-file duplicate key. Physical row 1 is the header, so the first
    // data row of the samples file is physical row 2.
    private static readonly (int Row, string Field, string Code)[] ExpectedErrors =
    [
        (6, "name", "required"),
        (7, "email", "required"),
        (8, "email", "invalid_email"),
        (9, "registeredAt", "invalid_date"),
        (10, "isActive", "invalid_type"),
        (11, "name", "max_length"),
        (12, "$row", "duplicate_key_in_file"),
    ];

    [Fact]
    public async Task SampleMainPath_ErrorsReportCorrectedUploadAndIdempotency_MatchDocumentedOutcome()
    {
        await using var stack = await StartStackAsync();
        var client = stack.Client;
        async Task<bool> Terminal(Guid id) => (await GetJobAsync(client, id)).CompletedAt is not null;
        try
        {
            var samples = SamplesDirectory();

            var errorsJob = await UploadAsync(client, stack.Source.Id, await ReadSampleAsync(samples, "customers-with-errors.csv"), "errors");
            await WaitAsync(() => Terminal(errorsJob.JobId), stack.Log);
            var errors = await GetJobAsync(client, errorsJob.JobId);
            Assert.Equal("CompletedWithErrors", errors.Status);
            Assert.Equal(12, errors.TotalRows);
            Assert.Equal(5, errors.InsertedRows);
            Assert.Equal(7, errors.InvalidRows);
            Assert.Equal(0, errors.UpdatedRows);
            Assert.Equal(0, errors.DuplicateRows);

            var invalidRows = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(
                $"/api/v1/import-jobs/{errorsJob.JobId}/rows?status=Invalid&pageSize=200"))!;
            Assert.Equal(7, invalidRows.Total);
            Assert.Equal(ExpectedErrors.Select(x => x.Row), invalidRows.Items.Select(x => x.RowNumber));
            foreach (var (row, expected) in invalidRows.Items.Zip(ExpectedErrors))
            {
                var error = Assert.Single(row.Errors);
                Assert.Equal(expected.Field, error.Field);
                Assert.Equal(expected.Code, error.Code);
                Assert.False(string.IsNullOrWhiteSpace(error.Message));
                Assert.NotNull(row.RawData);
            }

            var report = await client.GetStringAsync($"/api/v1/import-jobs/{errorsJob.JobId}/errors.csv");
            Assert.Equal(ExpectedErrors, ParseReport(report));

            // The corrected file inserts the rows that failed validation, keeps
            // unchanged customers as duplicates and completes the optional date
            // of one imported row, which becomes an update.
            var fixedJob = await UploadAsync(client, stack.Source.Id, await ReadSampleAsync(samples, "customers-with-errors-fixed.csv"), "fixed");
            await WaitAsync(() => Terminal(fixedJob.JobId), stack.Log);
            var corrected = await GetJobAsync(client, fixedJob.JobId);
            Assert.Equal("Completed", corrected.Status);
            Assert.Equal(12, corrected.TotalRows);
            Assert.Equal(7, corrected.InsertedRows);
            Assert.Equal(1, corrected.UpdatedRows);
            Assert.Equal(4, corrected.DuplicateRows);
            Assert.Equal(0, corrected.InvalidRows);
            Assert.Equal(12, await CountCustomersAsync(stack.Source.Id));

            // Repeating the same HTTP request returns the same job.
            var repeated = await UploadAsync(client, stack.Source.Id, await ReadSampleAsync(samples, "customers-with-errors-fixed.csv"), "fixed");
            Assert.Equal(fixedJob.JobId, repeated.JobId);
            Assert.Equal(2, await JobCountAsync(stack.Source.Id));

            var cleanJob = await UploadAsync(client, stack.Source.Id, await ReadSampleAsync(samples, "customers-valid.csv"), "valid");
            await WaitAsync(() => Terminal(cleanJob.JobId), stack.Log);
            var clean = await GetJobAsync(client, cleanJob.JobId);
            Assert.Equal("Completed", clean.Status);
            Assert.Equal(8, clean.InsertedRows);
            Assert.Equal(0, clean.InvalidRows);
            Assert.Equal(20, await CountCustomersAsync(stack.Source.Id));
        }
        finally { await stack.DisposeAsync(); }
    }

    [Fact]
    public async Task SampleXlsxErrors_ProduceSameOutcomeAndReportRowsAsCsv()
    {
        await using var stack = await StartStackAsync();
        var client = stack.Client;
        async Task<bool> Terminal(Guid id) => (await GetJobAsync(client, id)).CompletedAt is not null;
        try
        {
            var samples = SamplesDirectory();
            var csvJob = await UploadAsync(client, stack.Source.Id, await ReadSampleAsync(samples, "customers-with-errors.csv"), "csv");
            var xlsxSource = await CreateSourceAsync(client, "Синтетические клиенты XLSX");
            var xlsxJob = await UploadAsync(client, xlsxSource.Id, await ReadSampleAsync(samples, "customers-with-errors.xlsx"), "xlsx", "xlsx");
            await WaitAsync(() => Terminal(csvJob.JobId), stack.Log);
            await WaitAsync(() => Terminal(xlsxJob.JobId), stack.Log);
            var csv = await GetJobAsync(client, csvJob.JobId);
            var xlsx = await GetJobAsync(client, xlsxJob.JobId);
            Assert.Equal("CompletedWithErrors", xlsx.Status);
            Assert.Equal(csv.TotalRows, xlsx.TotalRows);
            Assert.Equal(csv.InsertedRows, xlsx.InsertedRows);
            Assert.Equal(csv.InvalidRows, xlsx.InvalidRows);

            var csvReport = await client.GetStringAsync($"/api/v1/import-jobs/{csvJob.JobId}/errors.csv");
            var xlsxReport = await client.GetStringAsync($"/api/v1/import-jobs/{xlsxJob.JobId}/errors.csv");
            Assert.Equal(ParseReport(csvReport).ToList(), ParseReport(xlsxReport).ToList());
        }
        finally { await stack.DisposeAsync(); }
    }

    [Fact]
    public async Task UploadAboveRowLimit_FailsJobWithoutCommittedRowsAndIsNotRetryable()
    {
        var bucket = $"imports-{Guid.NewGuid():N}";
        var configuration = Configuration(bucket, 250);
        configuration["Import:MaxRows"] = "7";
        await using var api = postgres.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await CreateBucketAsync(api, bucket);
        var source = await CreateSourceAsync(client);
        var log = new WorkerLog();
        using var worker = CreateWorker(configuration, log);
        await worker.StartAsync();
        try
        {
            async Task<bool> Terminal(Guid id) => (await GetJobAsync(client, id)).CompletedAt is not null;
            var job = await UploadAsync(client, source.Id, await ReadSampleAsync(SamplesDirectory(), "customers-valid.csv"), "limit");
            await WaitAsync(() => Terminal(job.JobId), log);
            var failed = await GetJobAsync(client, job.JobId);
            Assert.Equal("Failed", failed.Status);
            Assert.Equal("row_limit_exceeded", failed.LastErrorCode);
            Assert.Null(failed.TotalRows);
            Assert.Equal(0, failed.ProcessedRows);
            await using var database = postgres.CreateContext();
            Assert.Equal(0, await database.ImportRows.CountAsync(x => x.ImportJobId == job.JobId));
            Assert.Equal(0, await database.Customers.CountAsync(x => x.DataSourceId == source.Id));

            using var retry = await client.PostAsync($"/api/v1/import-jobs/{job.JobId}/retry", null);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Contains("job_not_retryable", await retry.Content.ReadAsStringAsync());
        }
        finally { await worker.StopAsync(); await api.DisposeAsync(); }
    }

    private async Task<AcceptanceStack> StartStackAsync()
    {
        var bucket = $"imports-{Guid.NewGuid():N}";
        var configuration = Configuration(bucket, 250);
        var api = postgres.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await CreateBucketAsync(api, bucket);
        var source = await CreateSourceAsync(client);
        var log = new WorkerLog();
        var worker = CreateWorker(configuration, log);
        await worker.StartAsync();
        return new AcceptanceStack(api, client, source, log, worker);
    }

    private sealed class AcceptanceStack(
        WebApplicationFactory<Program> api,
        HttpClient client,
        DataSourceDto source,
        WorkerLog log,
        IHost worker) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Api { get; } = api;
        public HttpClient Client { get; } = client;
        public DataSourceDto Source { get; } = source;
        public WorkerLog Log { get; } = log;
        public IHost Worker { get; } = worker;

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync();
            Worker.Dispose();
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private Dictionary<string, string?> Configuration(string bucket, int batchSize)
    {
        using var database = postgres.CreateContext();
        return new()
        {
            ["ConnectionStrings:DataFlow"] = database.Database.GetConnectionString(),
            ["Storage:Endpoint"] = minio.Endpoint,
            ["Storage:Bucket"] = bucket,
            ["Storage:AccessKey"] = "dataflow_test",
            ["Storage:SecretKey"] = "dataflow_test_password",
            ["Storage:Region"] = "us-east-1",
            ["Storage:UseSsl"] = "false",
            ["RabbitMq:Host"] = rabbit.Options.Host,
            ["RabbitMq:Port"] = rabbit.Options.Port.ToString(),
            ["RabbitMq:User"] = rabbit.Options.User,
            ["RabbitMq:Password"] = rabbit.Options.Password,
            ["RabbitMq:VirtualHost"] = "/",
            ["Import:BatchSize"] = batchSize.ToString(),
            ["Outbox:PollIntervalSeconds"] = "1"
        };
    }

    private static IHost CreateWorker(Dictionary<string, string?> configuration, WorkerLog log)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Services.AddDataFlowConfiguration(builder.Configuration);
        builder.Services.AddDataFlowPersistence();
        builder.Services.AddDataFlowStorage();
        builder.Services.AddDataFlowImportWorker();
        return builder.Build();
    }

    private async Task<DataSourceDto> CreateSourceAsync(HttpClient client, string name = "Синтетические клиенты")
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
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DataSourceDto>())!;
    }

    private static async Task<JobAcceptedDto> UploadAsync(HttpClient client, Guid sourceId, byte[] bytes,
        string key, string extension = "csv")
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", $"{key}.{extension}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{sourceId}/files") { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
    }

    private static async Task<JobDto> GetJobAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{id}"))!;

    private async Task<long> CountCustomersAsync(Guid sourceId)
    {
        await using var database = postgres.CreateContext();
        return await database.Customers.AsNoTracking().LongCountAsync(x => x.DataSourceId == sourceId);
    }

    private async Task<long> JobCountAsync(Guid sourceId)
    {
        await using var database = postgres.CreateContext();
        return await database.ImportJobs.AsNoTracking().LongCountAsync(x => x.DataSourceId == sourceId);
    }

    private static async Task CreateBucketAsync(WebApplicationFactory<Program> api, string bucket)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAmazonS3>().PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    }

    private static async Task<byte[]> ReadSampleAsync(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) { throw new InvalidOperationException($"Sample {path} is missing; run tools/DataFlow.SampleGenerator."); }
        return await File.ReadAllBytesAsync(path);
    }

    private static string SamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataFlow.sln")))
        {
            directory = directory.Parent!;
        }

        if (directory is null) { throw new InvalidOperationException("DataFlow.sln not found above the test directory."); }
        return Path.Combine(directory.FullName, "samples");
    }

    // errors.csv lines start with rowNumber,field,code — the reported message and
    // rawData follow; the documented codes and fields contain no commas.
    private static IEnumerable<(int Row, string Field, string Code)> ParseReport(string report) =>
        report.Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(line => line.Split(','))
            .Select(parts => (int.Parse(parts[0]), parts[1], parts[2]));

    private static async Task WaitAsync(Func<Task<bool>> condition, WorkerLog log)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            Assert.False(timeout.IsCancellationRequested, "Worker deadline exceeded. Logs: " + string.Join(Environment.NewLine, log.Messages));
            await Task.Delay(50);
        }
    }

    private sealed class WorkerLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
