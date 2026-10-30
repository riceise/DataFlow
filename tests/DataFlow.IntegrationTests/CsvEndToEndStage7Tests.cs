using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using DataFlow.IntegrationTests.Fixtures;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace DataFlow.IntegrationTests;

public sealed class MinioFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("icr.io/fusion-open/minio/minio@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e")
        .WithEnvironment("MINIO_ROOT_USER", "dataflow_test")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "dataflow_test_password")
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/minio/health/ready")))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public sealed class CsvEndToEndStage7Tests(DataSourcesApiFixture postgres, RabbitMqFixture rabbit, MinioFixture minio) :
    IClassFixture<DataSourcesApiFixture>, IClassFixture<RabbitMqFixture>, IClassFixture<MinioFixture>
{
    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public async Task MixedThousandRows_UploadOutboxRabbitWorkerRowsAndCustomers_AgreeWithFixture(int batchSize)
    {
        var bucket = $"imports-{Guid.NewGuid():N}";
        var configuration = Configuration(bucket, batchSize);
        await using var api = postgres.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using (var scope = api.Services.CreateScope())
        {
            var s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }

        using var sourceResponse = await client.PostAsJsonAsync("/api/v1/data-sources", SourceRequest());
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        var source = (await sourceResponse.Content.ReadFromJsonAsync<DataSourceDto>())!;
        var log = new WorkerLog();
        using var worker = CreateWorker(configuration, log);
        // First upload is queued durably while Worker is stopped, proving HTTP
        // acceptance does not perform parsing or mutate business data.
        var seed = await UploadAsync(client, source.Id, MixedCustomerCsv.Seed(), "seed");
        var pending = await GetJobAsync(client, seed.JobId);
        Assert.Equal("Queued", pending.Status);
        Assert.Null(pending.TotalRows);
        await using (var database = postgres.CreateContext())
        {
            Assert.False(await database.Customers.AnyAsync(x => x.DataSourceId == source.Id));
            Assert.False(await database.ImportRows.AnyAsync(x => x.ImportJobId == seed.JobId));
            Assert.Null((await RequestedMessageAsync(database, seed.JobId)).PublishedAt);
        }

        await worker.StartAsync();
        try
        {
            await WaitAsync(async () => (await GetJobAsync(client, seed.JobId)).Status == "Completed", log);
            var seedResult = await GetJobAsync(client, seed.JobId);
            Assert.Equal(200, seedResult.InsertedRows);
            var bytes = MixedCustomerCsv.File();
            var accepted = await UploadAsync(client, source.Id, bytes, "mixed");
            await WaitAsync(async () => (await GetJobAsync(client, accepted.JobId)).CompletedAt is not null, log);
            var completed = await GetJobAsync(client, accepted.JobId);
            Assert.Equal("CompletedWithErrors", completed.Status);
            Assert.Equal(MixedCustomerCsv.Total, completed.TotalRows);
            Assert.Equal(MixedCustomerCsv.Total, completed.ProcessedRows);
            Assert.Equal(MixedCustomerCsv.Inserted, completed.InsertedRows);
            Assert.Equal(MixedCustomerCsv.Updated, completed.UpdatedRows);
            Assert.Equal(MixedCustomerCsv.Duplicate, completed.DuplicateRows);
            Assert.Equal(MixedCustomerCsv.Invalid, completed.InvalidRows);
            Assert.Equal(completed.ProcessedRows, completed.InsertedRows + completed.UpdatedRows + completed.DuplicateRows + completed.InvalidRows);

            var rows = new List<JobRowDto>();
            for (var page = 1; page <= 5; page++)
            {
                var result = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>($"/api/v1/import-jobs/{accepted.JobId}/rows?page={page}&pageSize=200"))!;
                Assert.Equal(1000, result.Total);
                Assert.Equal(200, result.Items.Count);
                rows.AddRange(result.Items);
            }
            for (var index = 0; index < 1000; index++)
            {
                var expected = MixedCustomerCsv.Expected(index);
                Assert.Equal(index + 2, rows[index].RowNumber);
                Assert.Equal(expected.Status, rows[index].Status);
                if (expected.Code is null) { Assert.Empty(rows[index].Errors); }
                else
                {
                    Assert.NotNull(rows[index].RawData);
                    var error = Assert.Single(rows[index].Errors);
                    Assert.Equal(expected.Field, error.Field);
                    Assert.Equal(expected.Code, error.Code);
                    Assert.False(string.IsNullOrWhiteSpace(error.Message));
                }
            }
            var invalidPage = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>($"/api/v1/import-jobs/{accepted.JobId}/rows?status=Invalid&pageSize=200"))!;
            Assert.Equal(700, invalidPage.Total);
            Assert.All(invalidPage.Items, row => Assert.Equal("Invalid", row.Status));

            await using var db = postgres.CreateContext();
            var customers = await db.Customers.AsNoTracking().Where(x => x.DataSourceId == source.Id).ToListAsync();
            Assert.Equal(300, customers.Count);
            foreach (var group in Enumerable.Range(0, 100))
            {
                foreach (var slot in Enumerable.Range(0, 3))
                {
                    var customer = Assert.Single(customers, x => x.ExternalId == MixedCustomerCsv.Key(group, slot));
                    Assert.Equal(MixedCustomerCsv.Name(group, slot), customer.Name);
                    Assert.Equal(MixedCustomerCsv.Email(group, slot), customer.Email);
                    Assert.Equal(new DateOnly(2026, 1, 15), customer.RegisteredAt);
                    Assert.True(customer.IsActive);
                }
            }
            var storedJob = await db.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == accepted.JobId);
            Assert.Equal(1001, storedJob.LastCommittedRowNumber);
            var message = await RequestedMessageAsync(db, accepted.JobId);
            Assert.NotNull(message.PublishedAt);
            var inbox = await db.ProcessedMessages.SingleAsync(x => x.JobId == accepted.JobId);
            Assert.Equal(message.Id.ToString(), inbox.MessageId);
            Assert.Equal(storedJob.RunVersion, inbox.RunVersion);
            var finished = await db.OutboxMessages.Where(x => x.Type == "import.job.finished").ToListAsync();
            Assert.Single(finished, x => JsonDocument.Parse(x.Payload).RootElement.GetProperty("jobId").GetGuid() == accepted.JobId);

            // Verify the real S3 adapter preserved the exact immutable source bytes.
            using (var scope = api.Services.CreateScope())
            {
                await using var body = await scope.ServiceProvider.GetRequiredService<DataFlow.Application.Abstractions.IImportFileReader>()
                    .OpenReadAsync(storedJob.StorageKey, default);
                using var copy = new MemoryStream();
                await body.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
            // Publish the original envelope again and observe its ack only after
            // inbox/job/outbox are durable. No direct processor calls or storage fakes.
            await WaitAsync(() => Task.FromResult(log.Acknowledged >= 2), log);
            var priorAcks = log.Acknowledged;
            await using (var session = await RabbitMqSession.ConnectAsync(rabbit.Options, true, default))
            {
                var envelope = ImportMessageEnvelope.Create(new ClaimedOutbox(message.Id, message.Type, message.Payload, message.OccurredAt, 0));
                await session.Channel.BasicPublishAsync(RabbitMqSession.Exchange, RabbitMqSession.RoutingKey, true,
                    new BasicProperties { Persistent = true }, JsonSerializer.SerializeToUtf8Bytes(envelope, ImportMessageEnvelope.JsonOptions));
            }
            await WaitAsync(() => Task.FromResult(log.Messages.Any(x => x == "Previously processed import message ignored") && log.Acknowledged > priorAcks), log);
            Assert.Equal(1000, await db.ImportRows.CountAsync(x => x.ImportJobId == accepted.JobId));
            Assert.Equal(300, await db.Customers.CountAsync(x => x.DataSourceId == source.Id));
            Assert.Equal(1, (await db.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == accepted.JobId)).AttemptCount);
            var repeated = await UploadAsync(client, source.Id, bytes, "mixed");
            Assert.Equal(accepted.JobId, repeated.JobId);
            Assert.Equal(2, await db.ImportJobs.CountAsync(x => x.DataSourceId == source.Id));
            Assert.DoesNotContain(log.Messages, text => text.Contains("@example.test", StringComparison.Ordinal) || text.Contains("Клиент", StringComparison.Ordinal));
        }
        finally { await worker.StopAsync(); }
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

    private static object SourceRequest() => new
    {
        name = "Mixed CSV",
        acceptedFormats = new[] { "Csv" },
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
    };

    private static async Task<JobAcceptedDto> UploadAsync(HttpClient client, Guid sourceId, byte[] bytes, string key)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", "mixed.csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{sourceId}/files") { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
    }

    private static async Task<JobDto> GetJobAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{id}"))!;

    private static async Task<DataFlow.Domain.Entities.OutboxMessage> RequestedMessageAsync(DataFlowDbContext database, Guid jobId) =>
        Assert.Single(await database.OutboxMessages.AsNoTracking().Where(x => x.Type == "import.job.created").ToListAsync(),
            x => JsonDocument.Parse(x.Payload).RootElement.GetProperty("jobId").GetGuid() == jobId);

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
        public int Acknowledged => Messages.Count(x => x == "Import delivery acknowledged");
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
