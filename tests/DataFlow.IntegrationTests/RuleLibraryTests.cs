using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Rules;
using DataFlow.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class RuleLibraryTests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Theory]
    [InlineData("validate")]
    [InlineData("create")]
    [InlineData("version")]
    public async Task InvalidDefinitionReturnsActionableIssuesForValidationAndSaving(string action)
    {
        using var client = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        var definition = JsonSerializer.Deserialize<JsonElement>("""
            {"fields":[{"name":"code"},{"name":"amount","type":"decimal","minimum":0},
            {"name":"description","type":"string","minimum":1}]}
            """);
        var path = action switch
        {
            "validate" => "/api/v1/rule-definitions/validate",
            "create" => "/api/v1/rule-sets",
            _ => $"/api/v1/rule-sets/{Guid.NewGuid()}/versions"
        };
        using var response = action == "validate"
            ? await client.PostAsJsonAsync(path, definition)
            : await client.PostAsJsonAsync(path, new { name = "Invalid rules", definition });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("invalid_rule_definition", problem.GetProperty("code").GetString());
        var issue = Assert.Single(problem.GetProperty("issues").EnumerateArray());
        Assert.Equal("fields[2].minimum", issue.GetProperty("path").GetString());
        Assert.Equal("description", issue.GetProperty("fieldName").GetString());
        Assert.Contains("minLength", issue.GetProperty("hint").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("fields[2].minimum", out _));
    }

    private static JsonElement Definition(int maximum = 10) => JsonSerializer.SerializeToElement(new
    {
        definitionFormatVersion = 1,
        dataType = "Tabular",
        keyFields = new[] { "code" },
        fields = new object[] { new { name = "code", header = "Код", type = "string", required = true },
            new { name = "amount", header = "Сумма", type = "integer", maximum, required = true } }
    });

    [Fact]
    public async Task SharingPinsOnlySelectedVersionAndForkSurvivesRevocation()
    {
        using var owner = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var created = await owner.PostAsJsonAsync("/api/v1/rule-sets", new SaveRuleSetRequest("Shared", null, Definition()));
        created.EnsureSuccessStatusCode();
        var v1 = (await created.Content.ReadFromJsonAsync<RuleVersionDto>())!;
        var v2 = await Version(owner, v1.RuleSetId, 20);
        using var other = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        other.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/rule-set-versions/{v1.Id}")).StatusCode);
        (await owner.PutAsync($"/api/v1/rule-set-versions/{v1.Id}/publication", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/v1/rule-set-versions/{v1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/rule-set-versions/{v2.Id}")).StatusCode);
        (await other.PutAsync($"/api/v1/rule-library/{v1.Id}", null)).EnsureSuccessStatusCode();
        using var forked = await other.PostAsJsonAsync($"/api/v1/rule-set-versions/{v1.Id}/fork", new { name = "Independent" });
        forked.EnsureSuccessStatusCode();
        var copy = (await forked.Content.ReadFromJsonAsync<RuleVersionDto>())!;
        Assert.Equal(v1.ContentHash, copy.ContentHash);
        (await owner.DeleteAsync($"/api/v1/rule-set-versions/{v1.Id}/publication")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/rule-set-versions/{v1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/v1/rule-set-versions/{copy.Id}")).StatusCode);
        var library = await other.GetFromJsonAsync<JsonElement>("/api/v1/rule-library");
        Assert.False(library.GetProperty("items")[0].GetProperty("isAvailable").GetBoolean());
        await using var db = fixture.CreateContext();
        Assert.Equal((await db.RuleSetVersions.SingleAsync(v => v.Id == v1.Id)).RuleDocumentId,
            (await db.RuleSetVersions.SingleAsync(v => v.Id == copy.Id)).RuleDocumentId);
    }

    [Fact]
    public async Task SameFileDifferentVersionsExecutesDifferentRulesAndRetryKeepsDocument()
    {
        var storage = new FileUploadsStage5Tests.FakeStorage();
        await using var factory = fixture.WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IFileStorage>(storage))));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var created = await client.PostAsJsonAsync("/api/v1/rule-sets", new SaveRuleSetRequest("Limits", null, Definition()));
        created.EnsureSuccessStatusCode(); var v1 = (await created.Content.ReadFromJsonAsync<RuleVersionDto>())!;
        var v2 = await Version(client, v1.RuleSetId, 20);
        using var sourceResponse = await client.PostAsJsonAsync("/api/v1/rule-sources", new { name = "Table", ruleSetVersionId = v1.Id });
        sourceResponse.EnsureSuccessStatusCode();
        var sourceId = (await sourceResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await Upload(client, sourceId, v1.Id, "first");
        var repeat = await Upload(client, sourceId, v1.Id, "second");
        Assert.Equal(first.JobId, repeat.JobId);
        var second = await Upload(client, sourceId, v2.Id, "third");
        Assert.NotEqual(first.JobId, second.JobId);
        await using var db = fixture.CreateContext();
        var options = Options.Create(new ImportProcessingOptions());
        var reader = new BytesReader(Encoding.UTF8.GetBytes("Код,Сумма\na,15\nb,5\n"));
        var processor = new CsvImportProcessor(db, new ImportJobStore(db), reader, new CsvRowParser(options), options, NullLogger<CsvImportProcessor>.Instance);
        await processor.ProcessAsync(first.JobId, 1, Guid.NewGuid(), "rules-test", CancellationToken.None);
        await processor.ProcessAsync(second.JobId, 1, Guid.NewGuid(), "rules-test", CancellationToken.None);
        db.ChangeTracker.Clear();
        var a = await db.ImportJobs.SingleAsync(j => j.Id == first.JobId);
        var b = await db.ImportJobs.SingleAsync(j => j.Id == second.JobId);
        Assert.Equal(1, a.ValidRows); Assert.Equal(1, a.InvalidRows);
        Assert.Equal(2, b.ValidRows); Assert.Equal(0, b.InvalidRows);
        Assert.NotEqual(a.RuleDocumentId, b.RuleDocumentId);
        Assert.Equal(0, await db.Customers.CountAsync(c => c.DataSourceId == sourceId));
    }

    private static async Task<RuleVersionDto> Version(HttpClient client, Guid id, int maximum)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/rule-sets/{id}/versions", new SaveRuleVersionRequest(Definition(maximum)));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<RuleVersionDto>())!;
    }
    private static async Task<JobAcceptedDto> Upload(HttpClient client, Guid source, Guid version, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/data-sources/{source}/files?ruleSetVersionId={version}");
        request.Headers.Add("Idempotency-Key", key);
        var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Код,Сумма\na,15\nb,5\n")), "file", "data.csv"); request.Content = form;
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobAcceptedDto>())!;
    }
    private sealed class BytesReader(byte[] bytes) : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
