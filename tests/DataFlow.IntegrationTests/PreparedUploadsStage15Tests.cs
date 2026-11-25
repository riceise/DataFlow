using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Jobs;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Rules;
using DataFlow.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataFlow.IntegrationTests;

public sealed class PreparedUploadsStage15Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>, IAsyncLifetime
{
    private readonly PreparedStorage _storage = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dataflow-stage15-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _sourceId;
    private PreparedUploadIntegrityMode _integrity = PreparedUploadIntegrityMode.Metadata;

    public async Task InitializeAsync()
    {
        await using (var db = fixture.CreateContext())
        {
            await db.PreparedUploads.ExecuteDeleteAsync();
            await db.UploadRequests.ExecuteDeleteAsync();
            await db.OutboxMessages.ExecuteDeleteAsync();
            await db.ImportRows.ExecuteDeleteAsync();
            await db.ImportParts.ExecuteDeleteAsync();
            await db.ImportJobs.ExecuteDeleteAsync();
            await db.DataSources.ExecuteDeleteAsync();
            if (!await db.Users.AnyAsync(x => x.Id == TestAuthentication.UserId))
            {
                db.Users.Add(new ApplicationUser
                {
                    Id = TestAuthentication.UserId,
                    UserName = "stage15@example.test",
                    NormalizedUserName = "STAGE15@EXAMPLE.TEST",
                    Email = "stage15@example.test",
                    NormalizedEmail = "STAGE15@EXAMPLE.TEST",
                    SecurityStamp = Guid.NewGuid().ToString()
                });
            }

            var source = DataSource.Create("Prepared", DataSourceDataType.Tabular,
                AcceptedFileFormat.Csv | AcceptedFileFormat.Xlsx | AcceptedFileFormat.Zip,
                new DataSourceSchema(1, ["code"], [new DataSourceFieldDefinition("code", "string", true)]),
                DateTimeOffset.UtcNow);
            source.AssignOwner(TestAuthentication.UserId);
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            _sourceId = source.Id;
        }

        BuildFactory();
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

    private void BuildFactory()
    {
        _factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TempDirectory"] = _temp,
                ["Import:PreparedUploadIntegrity"] = _integrity.ToString()
            }));
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IPreparedFileStorage>(_storage)));
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
    }

    [Fact]
    public async Task RuleBackedSources_RequireExplicitZipOptIn()
    {
        var version = await TabularVersion();
        using (var response = await _client.PostAsJsonAsync("/api/v1/rule-sources",
                   new { name = "No ZIP", ruleSetVersionId = version.Id }))
        {
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var source = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/data-sources/{id}");
            Assert.DoesNotContain("Zip", source.GetProperty("acceptedFormats").EnumerateArray().Select(x => x.GetString()));
            using var enabled = await _client.PutAsJsonAsync($"/api/v1/rule-sources/{id}/zip-csv", new { enabled = true });
            enabled.EnsureSuccessStatusCode();
            source = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/data-sources/{id}");
            Assert.Contains("Zip", source.GetProperty("acceptedFormats").EnumerateArray().Select(x => x.GetString()));
        }

        using var created = await _client.PostAsJsonAsync("/api/v1/rule-sources",
            new { name = "ZIP", ruleSetVersionId = version.Id, allowZipCsv = true });
        created.EnsureSuccessStatusCode();
        var createdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var createdSource = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/data-sources/{createdId}");
        Assert.Contains("Zip", createdSource.GetProperty("acceptedFormats").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task OtherUser_CannotPrepareInspectOrCommitAnotherOwnersUpload()
    {
        var version = await TabularVersion();
        var prepared = await Prepare("owner", "code\nx\n");
        _client.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        await ProblemRaw(await PrepareResponse("other", "code\nx\n"), HttpStatusCode.NotFound, "source_not_found");
        using (var inspect = await _client.PostAsJsonAsync($"/api/v1/data-sources/{_sourceId}/file-uploads/inspect",
                   new InspectPreparedPartRequest { UploadToken = prepared.UploadToken, PartIndex = 0, RuleSetVersionId = version.Id }))
        {
            await ProblemRaw(inspect, HttpStatusCode.NotFound, "source_not_found");
        }

        await Problem(await Commit("other", CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id))),
            HttpStatusCode.NotFound, "source_not_found");
        Assert.Equal(1, _storage.Uploads);
        Assert.Equal(0, _storage.Heads);
    }

    [Fact]
    public async Task PrepareReplayConflictAndConcurrency_ReturnSameEncryptedResponseWithoutExtraS3()
    {
        var first = await Prepare("same", "code\n1\n");
        var replay = await Prepare("same", "code\n1\n");
        Assert.Equal(first.UploadToken, replay.UploadToken);
        Assert.Equal(first.ExpiresAt, replay.ExpiresAt);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay));
        Assert.Equal(1, _storage.Uploads);
        await using (var db = fixture.CreateContext())
        {
            var prepared = await db.PreparedUploads.SingleAsync();
            Assert.Equal(PreparedUploadStatus.Ready, prepared.Status);
            Assert.NotEqual(first.UploadToken, prepared.TokenHash);
            Assert.False(ContainsBytes(prepared.ReplayResponseCiphertext!, Encoding.UTF8.GetBytes(first.UploadToken)));
            Assert.DoesNotContain(first.UploadToken, prepared.ManifestJson);
        }

        await ProblemRaw(await PrepareResponse("same", "code\n2\n"), HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(1, _storage.Uploads);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Prepare("concurrent", "code\nx\n")));
        Assert.Single(responses.Select(x => x.UploadToken).Distinct());
        Assert.Equal(2, _storage.Uploads);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(2, await db.PreparedUploads.CountAsync());
            Assert.Equal(0, await db.ImportJobs.CountAsync());
            Assert.Equal(0, await db.OutboxMessages.CountAsync());
        }
    }

    [Fact]
    public async Task CommitReplayRunsBeforeHead_AndV3NeverReusesFileAlias()
    {
        var version = await TabularVersion();
        var prepared = await Prepare("prepare", "code\nx\n");
        var body = CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id));
        var first = await ReadCommit(Commit("commit", body));
        var replay = await ReadCommit(Commit("commit", body));
        Assert.Equal(first, replay);
        Assert.Equal(1, _storage.Heads);
        Assert.Equal(0, _storage.Reads);
        await Problem(await Commit("other-key", body), HttpStatusCode.Conflict, "upload_token_consumed");

        await using (var db = fixture.CreateContext())
        {
            var job = await db.ImportJobs.SingleAsync(x => x.Id == first.JobId);
            var part = Assert.Single(job.Parts);
            Assert.Equal(version.Id, job.RuleSetVersionId);
            Assert.Equal(version.Id, part.RuleSetVersionId);
            Assert.Equal("part:0", part.DeduplicationScope);
            Assert.Equal(PreparedUploadStatus.Consumed, (await db.PreparedUploads.SingleAsync()).Status);
            Assert.Equal(1, await db.OutboxMessages.CountAsync());
        }

        var secondPrepare = await Prepare("new-prepare", "code\nx\n");
        var second = await ReadCommit(Commit("new-commit", CommitBody(secondPrepare.UploadToken, new CommitPart(0, true, version.Id))));
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(2, _storage.Heads);
        Assert.Equal(2, await JobsCount());
    }

    [Fact]
    public async Task InspectAndHeadOnlyCommit_ReparseDifferentDelimiterWithoutGet()
    {
        var version = await TabularVersion(";", withAmount: true);
        var prepared = await Prepare("prepare", "code;amount\nx;1\n");
        Assert.Equal(new[] { "code;amount" }, prepared.Parts[0].Preview.Headers);
        var before = (_storage.Heads, _storage.Reads);
        var inspected = await _client.PostAsJsonAsync($"/api/v1/data-sources/{_sourceId}/file-uploads/inspect",
            new InspectPreparedPartRequest { UploadToken = prepared.UploadToken, PartIndex = 0, RuleSetVersionId = version.Id });
        Assert.True(inspected.IsSuccessStatusCode, await inspected.Content.ReadAsStringAsync());
        var part = await inspected.Content.ReadFromJsonAsync<PreparedPartDto>();
        Assert.Equal(new[] { "code", "amount" }, part!.Preview.Headers);
        Assert.Equal(new[] { "x", "1" }, part.Preview.Rows[0]);
        Assert.Equal(before, (_storage.Heads, _storage.Reads));

        var accepted = await Commit("commit", CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id)));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, _storage.Heads);
        Assert.Equal(0, _storage.Reads);

        var wrong = await TabularVersion(",", "other");
        var second = await Prepare("wrong", "code\nx\n");
        await Problem(await Commit("wrong-commit", CommitBody(second.UploadToken, new CommitPart(0, true, wrong.Id))),
            HttpStatusCode.BadRequest, "unknown_csv_header");
        Assert.Equal(1, _storage.Heads);
        Assert.Equal(0, _storage.Reads);
    }

    [Fact]
    public async Task ManifestOrderSkippedPartsAndMixedRules_AreCopiedExactly()
    {
        var customer = await CustomerVersion();
        var tabular = await TabularVersion();
        var workbook = XlsxTestBuilder.BuildWorkbook([
            Sheet("Zulu", ["externalId", "name", "email", "isActive"], "xl/custom/z.xml", "hidden"),
            Sheet("Alpha", ["code"], "xl/custom/a.xml")
        ]);
        var prepared = await Prepare("workbook", workbook, "book.xlsx");
        Assert.Equal(new[] { "Zulu", "Alpha" }, prepared.Parts.Select(x => x.SheetName));
        Assert.Equal("hidden", prepared.Parts[0].SheetVisibility);
        Assert.Equal(0, prepared.Parts[0].PartIndex);
        Assert.Equal(1, prepared.Parts[1].PartIndex);

        var accepted = await Commit("mixed", CommitBody(prepared.UploadToken,
            new CommitPart(0, true, customer.Id), new CommitPart(1, true, tabular.Id)));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var jobId = (await accepted.Content.ReadFromJsonAsync<JobAcceptedDto>())!.JobId;
        await using var db = fixture.CreateContext();
        var job = await db.ImportJobs.SingleAsync(x => x.Id == jobId);
        Assert.Null(job.RuleSetVersionId);
        Assert.Null(job.RuleDocumentId);
        var parts = job.Parts.OrderBy(x => x.PartIndex).ToArray();
        Assert.Equal("customer:job", parts[0].DeduplicationScope);
        Assert.Equal("part:1", parts[1].DeduplicationScope);
        Assert.Equal("xl/custom/z.xml", parts[0].SourceLocator);
        Assert.Equal("xl/custom/a.xml", parts[1].SourceLocator);
        var manifest = await db.PreparedUploads.Select(x => x.ManifestJson).SingleAsync();
        Assert.Equal("hidden", JsonDocument.Parse(manifest!).RootElement.GetProperty("Parts")[0]
            .GetProperty("SheetVisibility").GetString());
    }

    [Fact]
    public async Task ZipCentralOrderAndSkippedPart_AreImmutableAndReorderIsRejected()
    {
        var version = await TabularVersion();
        var zip = Zip(("z.csv", "code\nz\n"), ("a.csv", "code\na\n"));
        var prepared = await Prepare("zip", zip, "parts.zip");
        Assert.Equal(new[] { "z.csv", "a.csv" }, prepared.Parts.Select(x => x.SourceName));
        var valid = new[]
        {
            new CommitPart(0, true, version.Id), new CommitPart(1, false, null)
        };
        var malformed = new[]
        {
            new[] { valid[1], valid[0] },
            new[] { valid[0] },
            new[] { valid[0], new CommitPart(1, true, version.Id), new CommitPart(2, false, null) }
        };
        for (var index = 0; index < malformed.Length; index++)
        {
            await Problem(await Commit($"bad-{index}", CommitBody(prepared.UploadToken, malformed[index])),
                HttpStatusCode.BadRequest, index == 2 ? "invalid_manifest" : "invalid_manifest");
        }

        Assert.Equal(0, _storage.Heads);
        var accepted = await Commit("good", CommitBody(prepared.UploadToken, valid));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var jobId = (await accepted.Content.ReadFromJsonAsync<JobAcceptedDto>())!.JobId;
        await using var db = fixture.CreateContext();
        var job = await db.ImportJobs.SingleAsync(x => x.Id == jobId);
        var parts = job.Parts.OrderBy(x => x.PartIndex).ToArray();
        Assert.Equal(new[] { "z.csv", "a.csv" }, parts.Select(x => x.SourceName));
        Assert.Equal(ImportPartStatus.Skipped, parts[1].Status);
        Assert.Null(parts[1].RuleSetVersionId);
        Assert.Null(parts[1].DeduplicationScope);
        Assert.Equal("zip:central:0", parts[0].SourceLocator);
        Assert.Equal("zip:central:1", parts[1].SourceLocator);
    }

    [Fact]
    public async Task NormalizedTabularGroup_IsCanonicalForCommitReplay()
    {
        var version = await TabularVersion();
        var prepared = await Prepare("prepare", "code\nx\n");
        var first = await ReadCommit(Commit("commit", CommitBody(prepared.UploadToken,
            new CommitPart(0, true, version.Id, " Sales.Q1 "))));
        var replay = await ReadCommit(Commit("commit", CommitBody(prepared.UploadToken,
            new CommitPart(0, true, version.Id, "SALES.Q1"))));
        Assert.Equal(first, replay);
        await Problem(await Commit("commit", CommitBody(prepared.UploadToken,
            new CommitPart(0, true, version.Id, "other"))), HttpStatusCode.Conflict, "idempotency_conflict");
        await Problem(await Commit("commit", CommitBody(prepared.UploadToken,
            new CommitPart(0, true, version.Id, "bad group"))), HttpStatusCode.BadRequest, "invalid_deduplication_group");
        Assert.Equal(1, _storage.Heads);
        await using var db = fixture.CreateContext();
        Assert.Equal("tabular-group:sales.q1", (await db.ImportJobs.SingleAsync()).Parts[0].DeduplicationScope);
    }

    [Fact]
    public async Task ReplayWithMalformedShape_ReturnsConflict()
    {
        var version = await TabularVersion();
        var prepared = await Prepare("prepare", "code\nx\n");
        await ReadCommit(Commit("commit", CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id))));

        await Problem(await Commit("commit", CommitBody(prepared.UploadToken)), HttpStatusCode.Conflict, "idempotency_conflict");
        await Problem(await Commit("commit", CommitBody(prepared.UploadToken, new CommitPart(0, false, null))),
            HttpStatusCode.Conflict, "idempotency_conflict");
    }

    [Fact]
    public async Task Expiry_ReturnsConflictWithoutStorageOrNewToken()
    {
        var version = await TabularVersion();
        var prepared = await Prepare("prepare", "code\nx\n");
        await using (var db = fixture.CreateContext())
        {
            await db.PreparedUploads.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2))
                .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        await ProblemRaw(await PrepareResponse("prepare", "code\nx\n"), HttpStatusCode.Conflict, "prepared_upload_expired");
        await Problem(await Commit("commit", CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id))),
            HttpStatusCode.Conflict, "prepared_upload_expired");
        Assert.Equal(1, _storage.Uploads);
        Assert.Equal(0, _storage.Heads);
    }

    [Theory]
    [InlineData(PreparedUploadIntegrityMode.Metadata)]
    [InlineData(PreparedUploadIntegrityMode.Native)]
    [InlineData(PreparedUploadIntegrityMode.Strict)]
    public async Task IntegrityModes_DetectMetadataNativeAndActualByteMismatch(PreparedUploadIntegrityMode mode)
    {
        _integrity = mode;
        _client.Dispose();
        await _factory.DisposeAsync();
        BuildFactory();
        var version = await TabularVersion();
        var prepared = await Prepare("prepare", "code\nx\n");
        var key = _storage.Objects.Keys.Single();
        switch (mode)
        {
            case PreparedUploadIntegrityMode.Metadata:
                _storage.MetadataSha[key] = new string('0', 64);
                break;
            case PreparedUploadIntegrityMode.Native:
                _storage.NativeSha[key] = Convert.ToBase64String(new byte[32]);
                break;
            case PreparedUploadIntegrityMode.Strict:
                _storage.Objects[key][^1] ^= 1;
                break;
        }

        await Problem(await Commit("commit", CommitBody(prepared.UploadToken, new CommitPart(0, true, version.Id))),
            HttpStatusCode.Conflict, "prepared_upload_integrity_mismatch");
        Assert.Equal(0, await JobsCount());
        Assert.Equal(mode == PreparedUploadIntegrityMode.Strict ? 1 : 0, _storage.Reads);
        Assert.Equal(mode == PreparedUploadIntegrityMode.Strict ? 0 : 1, _storage.Heads);
    }

    private Task<PreparedUploadResponse> Prepare(string key, string csv) =>
        ReadPrepare(PrepareResponse(key, Encoding.UTF8.GetBytes(csv), "input.csv"));

    private Task<PreparedUploadResponse> Prepare(string key, byte[] bytes, string name) =>
        ReadPrepare(PrepareResponse(key, bytes, name));

    private async Task<HttpResponseMessage> PrepareResponse(string key, string csv) =>
        await PrepareResponse(key, Encoding.UTF8.GetBytes(csv), "input.csv");

    private async Task<HttpResponseMessage> PrepareResponse(string key, byte[] bytes, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{_sourceId}/file-uploads/prepare");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        request.Content = form;
        return await _client.SendAsync(request);
    }

    private static async Task<PreparedUploadResponse> ReadPrepare(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PreparedUploadResponse>())!;
    }

    private async Task<HttpResponseMessage> Commit(string key, CommitPreparedUploadRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{_sourceId}/file-uploads/commit")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private async Task<JobAcceptedDto> ReadCommit(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
    }

    private static CommitPreparedUploadRequest CommitBody(string token, params CommitPart[] parts) => new()
    {
        UploadToken = token,
        Parts = parts.Select(x => new CommitPreparedPartRequest
        {
            PartIndex = x.Index,
            Enabled = x.Enabled,
            RuleSetVersionId = x.Version,
            DeduplicationGroup = x.Group
        }).ToArray()
    };

    private async Task<RuleVersionDto> TabularVersion(string delimiter = ",", string header = "code", bool withAmount = false)
    {
        var fields = withAmount
            ? new object[]
            {
                new { name = "code", header, type = "string", required = true },
                new { name = "amount", header = "amount", type = "integer", required = true }
            }
            : [new { name = "code", header, type = "string", required = true }];
        using var response = await _client.PostAsJsonAsync("/api/v1/rule-sets", new SaveRuleSetRequest("Tabular rules", null,
            JsonSerializer.SerializeToElement(new
            {
                definitionFormatVersion = 1,
                dataType = "Tabular",
                csv = new { delimiter },
                keyFields = new[] { "code" },
                fields
            })));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RuleVersionDto>())!;
    }

    private async Task<RuleVersionDto> CustomerVersion()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/rule-sets", new SaveRuleSetRequest("Customer rules", null,
            JsonSerializer.SerializeToElement(new
            {
                definitionFormatVersion = 1,
                dataType = "Customer",
                keyFields = new[] { "externalId" },
                fields = new object[]
                {
                    new { name = "externalId", type = "string", required = true, maxLength = 80 },
                    new { name = "name", type = "string", required = true, maxLength = 200 },
                    new { name = "email", type = "email", required = true, maxLength = 254 },
                    new { name = "isActive", type = "boolean", required = true }
                }
            })));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RuleVersionDto>())!;
    }

    private static XlsxSheetSpec Sheet(string name, string[] headers, string path, string? visibility = null) => new(name,
        [new XlsxRowSpec(1, headers.Select(header => XlsxCellSpec.SharedString(header)).ToArray())], path, visibility);

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }

        return memory.ToArray();
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<int> JobsCount()
    {
        await using var db = fixture.CreateContext();
        return await db.ImportJobs.CountAsync();
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code) =>
        await ProblemRaw(response, status, code);

    private static async Task ProblemRaw(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(status == response.StatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        }
    }

    private sealed record CommitPart(int Index, bool Enabled, Guid? Version, string? Group = null);

    internal sealed class PreparedStorage : IPreparedFileStorage
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public ConcurrentDictionary<string, string> MetadataSha { get; } = new();
        public ConcurrentDictionary<string, string> NativeSha { get; } = new();
        public int Uploads;
        public int Heads;
        public int Reads;
        public int Deletes;

        public async Task UploadAsync(string objectKey, string filePath, string fileSha256, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Uploads);
            if (!Objects.TryAdd(objectKey, await File.ReadAllBytesAsync(filePath, cancellationToken)))
            {
                throw new InvalidOperationException("overwrite");
            }

            MetadataSha[objectKey] = fileSha256;
            NativeSha[objectKey] = Convert.ToBase64String(Convert.FromHexString(fileSha256));
        }

        public Task<PreparedStorageMetadata> HeadAsync(string objectKey, bool includeNativeChecksum, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Heads);
            if (!Objects.TryGetValue(objectKey, out var bytes))
            {
                throw new FileNotFoundException();
            }

            return Task.FromResult(new PreparedStorageMetadata(bytes.LongLength, MetadataSha[objectKey], NativeSha[objectKey]));
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<Stream>(new MemoryStream(Objects[objectKey], writable: false));
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Deletes);
            Objects.TryRemove(objectKey, out _);
            MetadataSha.TryRemove(objectKey, out _);
            NativeSha.TryRemove(objectKey, out _);
            return Task.CompletedTask;
        }

        public async Task<bool> TryDeleteAsync(string objectKey, Guid dataSourceId, CancellationToken cancellationToken)
        {
            await DeleteAsync(objectKey, cancellationToken);
            return true;
        }
    }
}
