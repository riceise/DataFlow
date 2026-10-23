using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class FileUploadsStage5Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>, IAsyncLifetime
{
    private readonly FakeStorage _storage = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dataflow-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _sourceId;
    private const string Csv = "externalId,name,email\n1,Test,test@example.com\n";
    private string Url => $"/api/v1/data-sources/{_sourceId}/files";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.UploadRequests.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.ImportJobs.ExecuteDeleteAsync();
        await db.DataSources.ExecuteDeleteAsync();
        var source = DataSource.Create("Uploads", DataSourceDataType.Customer, AcceptedFileFormat.Csv | AcceptedFileFormat.Pdf,
            new DataSourceSchema(1, ["externalId"], [new DataSourceFieldDefinition("externalId", "string", true)]), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        _sourceId = source.Id;
        _factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TempDirectory"] = _temp,
                ["Storage:MaxFileBytes"] = "1024"
            }));
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IFileStorage>(_storage)));
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (Directory.Exists(_temp))
        {
            Assert.Empty(Directory.GetFiles(_temp));
            Directory.Delete(_temp);
        }
    }

    [Fact]
    public async Task UploadAndRepeat_AreAtomicAndDoNotUploadTwice()
    {
        using var first = await Upload("same");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var result = (await first.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
        Assert.Equal("Queued", result.Status);
        Assert.Equal($"/api/v1/jobs/{result.JobId}", first.Headers.Location!.OriginalString);
        using var repeat = await Upload("same");
        Assert.Equal(result, await repeat.Content.ReadFromJsonAsync<JobAcceptedDto>());
        Assert.Equal(1, _storage.Uploads);
        Assert.Single(_storage.Objects);
        Assert.Matches($"^sources/{_sourceId:N}/[0-9]{{4}}/[0-9]{{2}}/[0-9]{{2}}/[a-f0-9]{{32}}$", _storage.Objects.Keys.Single());
        await using var db = fixture.CreateContext();
        var job = Assert.Single(await db.ImportJobs.ToListAsync());
        var source = await db.DataSources.SingleAsync();
        Assert.Equal(source.SchemaJson, job.SchemaJson);
        Assert.Equal(source.SchemaVersion, job.SchemaVersion);
        Assert.Equal(ImportJobStatus.Queued, job.Status);
        Assert.Equal(job.Id, (await db.UploadRequests.SingleAsync()).JobId);
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal("import.job.created", message.Type);
        Assert.Equal(job.Id, JsonDocument.Parse(message.Payload).RootElement.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task SameKeyDifferentContent_ReturnsConflict()
    {
        using var first = await Upload("same");
        using var conflict = await Upload("same", Csv + "2,Other,other@example.com");
        await Problem(conflict, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(1, _storage.Uploads);
        await Counts(1);
    }

    [Fact]
    public async Task FiveConcurrentRequests_CreateOneJobAndOneOutbox()
    {
        _storage.Barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Upload("concurrent")));
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                ids.Add((await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!.JobId);
            }
        }
        Assert.Single(ids.Distinct());
        Assert.Equal(5, _storage.Uploads);
        Assert.Equal(4, _storage.Deletes);
        Assert.Single(_storage.Objects);
        await Counts(1);
    }

    [Fact]
    public async Task DifferentKeysSameContent_CreateSeparateJobs()
    {
        using var first = await Upload("first");
        using var second = await Upload("second");
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.NotEqual((await first.Content.ReadFromJsonAsync<JobAcceptedDto>())!.JobId,
            (await second.Content.ReadFromJsonAsync<JobAcceptedDto>())!.JobId);
        Assert.Equal(2, _storage.Uploads);
        await Counts(2);
    }

    [Fact]
    public async Task ConcurrentDifferentContentSameKey_UniqueIndexRejectsLoserAndCompensates()
    {
        _storage.BarrierTarget = 2;
        _storage.Barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = await Task.WhenAll(Upload("race", Csv), Upload("race", Csv + "different"));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
            await Problem(Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict),
                HttpStatusCode.Conflict, "idempotency_conflict");
            await Counts(1);
            Assert.Equal(1, _storage.Deletes);
            Assert.Single(_storage.Objects);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task CancellationAfterStorageUpload_CompensatesWithoutCreatingRecords()
    {
        using var cancellation = new CancellationTokenSource();
        _storage.AfterUpload = cancellation.Cancel;
        var path = Path.Combine(_temp, "cancellation-test");
        Directory.CreateDirectory(_temp);
        await File.WriteAllTextAsync(path, Csv);
        try
        {
            using var scope = _factory.Services.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync<UploadFileCommand, JobAcceptedDto>(
                new(_sourceId, "cancel", path, "customers.csv", new string('a', 64), AcceptedFileFormat.Csv), cancellation.Token));
            Assert.Equal(1, _storage.Uploads);
            Assert.Equal(1, _storage.Deletes);
            Assert.Empty(_storage.Objects);
        }
        finally { File.Delete(path); }
        await Counts(0);
    }

    [Fact]
    public async Task KeysAreOrdinalAndScopedToSource()
    {
        using var first = await Upload("Case");
        using var second = await Upload("case");
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        await using var db = fixture.CreateContext();
        var original = await db.DataSources.SingleAsync();
        var source = DataSource.Create("Other", original.DataType, original.AcceptedFormats,
            original.GetParsedSchema(), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        _sourceId = source.Id;
        using var third = await Upload("Case");
        third.EnsureSuccessStatusCode();
        await Counts(3);
    }

    [Theory]
    [InlineData("oversize", "file_too_large")]
    [InlineData("empty", "empty_file")]
    [InlineData("exe", "file_format_not_allowed")]
    [InlineData("pdf", "invalid_file_format")]
    [InlineData("binary", "invalid_file_format")]
    public async Task InvalidFiles_DoNotWriteStorageOrDatabase(string variant, string code)
    {
        using var response = await Upload("key", variant switch { "oversize" => new string('a', 1025), "empty" => "", "binary" => "a\0b", _ => Csv },
            variant switch { "exe" => "test.exe", "pdf" => "test.pdf", _ => "test.csv" });
        await Problem(response, HttpStatusCode.BadRequest, code);
        Assert.Equal(0, _storage.Uploads);
        await Counts(0);
    }

    [Fact]
    public async Task PdfSignature_IsAcceptedAndContentTypeIsIgnored()
    {
        using var response = await Upload("pdf", "%PDF-1.7\nexample", "document.pdf");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, _storage.Uploads);
        await Counts(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("long")]
    public async Task InvalidKey_ReturnsMachineReadableError(string? key)
    {
        using var response = await Upload(key == "long" ? new string('a', 129) : key);
        await Problem(response, HttpStatusCode.BadRequest, "invalid_idempotency_key");
        Assert.Equal(0, _storage.Uploads);
        await Counts(0);
    }

    [Fact]
    public async Task MissingSource_DoesNotUpload()
    {
        _sourceId = Guid.NewGuid();
        using var response = await Upload("key");
        await Problem(response, HttpStatusCode.NotFound, "source_not_found");
        Assert.Equal(0, _storage.Uploads);
        await Counts(0);
    }

    [Fact]
    public async Task StorageFailure_LeavesNoJobOrTempFile()
    {
        _storage.FailUpload = true;
        using var response = await Upload("key");
        await Problem(response, HttpStatusCode.InternalServerError, "internal_error");
        await Counts(0);
        Assert.Empty(_storage.Objects);
    }

    [Fact]
    public async Task TransactionFailure_RollsBackAndCompensatesObject()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_upload_outbox() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''test transaction failure''; END'; CREATE TRIGGER fail_upload_outbox BEFORE INSERT ON outbox_messages FOR EACH ROW EXECUTE FUNCTION fail_upload_outbox();");
        try
        {
            using var response = await Upload("key");
            await Problem(response, HttpStatusCode.InternalServerError, "internal_error");
            await Counts(0);
            Assert.Equal(1, _storage.Uploads);
            Assert.Equal(1, _storage.Deletes);
            Assert.Empty(_storage.Objects);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_upload_outbox ON outbox_messages; DROP FUNCTION fail_upload_outbox();"); }
    }

    [Fact]
    public async Task FailedCompensation_PersistsUnlinkedRecordAndCleanupRetries()
    {
        using var cancellation = new CancellationTokenSource();
        _storage.AfterUpload = cancellation.Cancel;
        _storage.FailDelete = true;
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "compensation-test");
        await File.WriteAllTextAsync(path, Csv);
        try
        {
            using var scope = _factory.Services.CreateScope();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .DispatchAsync<UploadFileCommand, JobAcceptedDto>(new(_sourceId, "cancel", path, "customers.csv",
                    new string('a', 64), AcceptedFileFormat.Csv), cancellation.Token));
        }
        finally { File.Delete(path); }
        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.ImportJobs.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
        Assert.Null((await db.UploadRequests.SingleAsync()).JobId);
        Assert.Single(_storage.Objects);
        await db.UploadRequests.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        _storage.FailDelete = false;
        var cleanup = new OrphanCleanupService(_factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StorageOptions()), TimeProvider.System, NullLogger<OrphanCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);
        Assert.Empty(_storage.Objects);
        Assert.Equal(0, await db.UploadRequests.CountAsync());
    }

    [Fact]
    public async Task ListFilterPaginationAndDetails_ReturnSafeDtos()
    {
        for (var i = 0; i < 3; i++) { using var response = await Upload($"key-{i}"); response.EnsureSuccessStatusCode(); }
        await using var db = fixture.CreateContext();
        var jobs = await db.ImportJobs.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
        jobs[0].Fail("test", "test", false, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var url = $"/api/v1/data-sources/{_sourceId}/jobs";
        var page = await _client.GetFromJsonAsync<PagedResult<JobDto>>($"{url}?page=2&pageSize=1");
        Assert.Equal(3, page!.Total);
        Assert.Equal(jobs[1].Id, Assert.Single(page.Items).Id);
        var filtered = await _client.GetFromJsonAsync<PagedResult<JobDto>>($"{url}?status=Failed");
        Assert.Equal(jobs[0].Id, Assert.Single(filtered!.Items).Id);
        using var details = await _client.GetAsync($"/api/v1/jobs/{jobs[0].Id}");
        var body = await details.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", body);
        Assert.DoesNotContain("schemaJson", body);
        Assert.Equal("Failed", (await details.Content.ReadFromJsonAsync<JobDto>())!.Status);
        foreach (var query in new[] { "page=0", "pageSize=101", "status=1", "status=unknown" })
        {
            using var invalid = await _client.GetAsync($"{url}?{query}");
            await Problem(invalid, HttpStatusCode.BadRequest, "validation_failed");
        }
        using var missing = await _client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");
        await Problem(missing, HttpStatusCode.NotFound, "job_not_found");
    }

    [Fact]
    public async Task MalformedMultipartAndExtraFields_AreRejected()
    {
        using var malformed = new HttpRequestMessage(HttpMethod.Post, Url);
        malformed.Headers.Add("Idempotency-Key", "key");
        malformed.Content = new StringContent("broken");
        malformed.Content.Headers.ContentType = new("multipart/form-data");
        using var invalid = await _client.SendAsync(malformed);
        await Problem(invalid, HttpStatusCode.BadRequest, "invalid_multipart");
        using var truncated = new HttpRequestMessage(HttpMethod.Post, Url);
        truncated.Headers.Add("Idempotency-Key", "key");
        truncated.Content = new StringContent("--test\r\nContent-Disposition: form-data; name=\"file\"; filename=\"test.csv\"\r\n\r\ntext");
        truncated.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=test");
        using var truncatedResponse = await _client.SendAsync(truncated);
        await Problem(truncatedResponse, HttpStatusCode.BadRequest, "invalid_multipart");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("extra"), "field");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = form };
        request.Headers.Add("Idempotency-Key", "key");
        using var response = await _client.SendAsync(request);
        await Problem(response, HttpStatusCode.BadRequest, "unexpected_form_field");
        Assert.Equal(0, _storage.Uploads);
        await Counts(0);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyExpiredUnlinkedObjects()
    {
        using var response = await Upload("active");
        response.EnsureSuccessStatusCode();
        await using var db = fixture.CreateContext();
        var active = await db.ImportJobs.SingleAsync();
        var orphan = UploadRequest.Create("orphan", new string('a', 64), _sourceId, "orphan-object", new string('b', 64), DateTimeOffset.UtcNow.AddDays(-2));
        var protectedUpload = UploadRequest.Create("protected", new string('c', 64), _sourceId, active.StorageKey, new string('d', 64), DateTimeOffset.UtcNow.AddDays(-2));
        db.UploadRequests.AddRange(orphan, protectedUpload);
        _storage.Objects[orphan.StorageKey] = [];
        await db.SaveChangesAsync();
        var cleanup = new OrphanCleanupService(_factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new StorageOptions()), TimeProvider.System, NullLogger<OrphanCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);
        Assert.False(_storage.Objects.ContainsKey(orphan.StorageKey));
        Assert.True(_storage.Objects.ContainsKey(active.StorageKey));
        Assert.Equal(2, await db.UploadRequests.CountAsync());
    }

    private async Task<HttpResponseMessage> Upload(string? key, string contents = Csv, string name = "customers.csv")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(contents)), "file", name);
        request.Content = form;
        return await _client.SendAsync(request);
    }

    private async Task Counts(int expected)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(expected, await db.ImportJobs.CountAsync());
        Assert.Equal(expected, await db.OutboxMessages.CountAsync());
        Assert.Equal(expected, await db.UploadRequests.CountAsync());
        if (Directory.Exists(_temp))
        {
            Assert.Empty(Directory.GetFiles(_temp));
        }
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private sealed class FakeStorage : IFileStorage
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public int Uploads;
        public int Deletes;
        public bool FailUpload;
        public bool FailDelete;
        public TaskCompletionSource? Barrier;
        public int BarrierTarget = 5;
        public Action? AfterUpload;
        public async Task UploadAsync(string objectKey, string filePath, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Uploads);
            if (FailUpload)
            {
                throw new IOException("simulated storage failure");
            }

            Objects[objectKey] = await File.ReadAllBytesAsync(filePath, cancellationToken);
            AfterUpload?.Invoke();
            if (Barrier is not null)
            {
                if (count == BarrierTarget)
                {
                    Barrier.TrySetResult();
                }

                await Barrier.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Deletes);
            Objects.TryRemove(objectKey, out _);
            return Task.CompletedTask;
        }
        public async Task<bool> TryDeleteAsync(string objectKey, Guid dataSourceId, CancellationToken cancellationToken)
        {
            if (FailDelete)
            {
                return false;
            }

            await DeleteAsync(objectKey, cancellationToken);
            return true;
        }
    }
}
