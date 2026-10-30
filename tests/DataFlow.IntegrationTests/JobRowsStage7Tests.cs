using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.IntegrationTests;

public sealed class JobRowsStage7Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task Rows_AreOrderedPagedAndIsolatedToRequestedJob_WithSafeDto()
    {
        var job = await SeedAsync(205);
        await SeedAsync(2);
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var first = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(job.Id) + "?pageSize=200"))!;
        Assert.Equal(205, first.Total);
        Assert.Equal(1, first.Page);
        Assert.Equal(200, first.PageSize);
        Assert.Equal(Enumerable.Range(2, 200), first.Items.Select(x => x.RowNumber));
        var second = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(job.Id) + "?page=2&pageSize=200"))!;
        Assert.Equal(Enumerable.Range(202, 5), second.Items.Select(x => x.RowNumber));
        Assert.All(first.Items.Where(x => x.Status != "Invalid"), row =>
        {
            Assert.Null(row.RawData);
            Assert.Empty(row.Errors);
        });
        var invalid = first.Items.First(x => x.Status == "Invalid");
        Assert.NotNull(invalid.RawData);
        Assert.Equal(new ImportRowError("email", "invalid_email", "A valid email address is required."), Assert.Single(invalid.Errors));
        using var json = JsonDocument.Parse(await client.GetStringAsync(Url(job.Id)));
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(new[] { "errors", "naturalKey", "processedAt", "rawData", "rowHash", "rowNumber", "status" },
            item.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("email", json.RootElement.GetProperty("items")[2].GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Theory]
    [InlineData("Inserted")]
    [InlineData("Updated")]
    [InlineData("Invalid")]
    [InlineData("Duplicate")]
    public async Task StatusFilter_UsesRowStatusesAndCountsOnlyMatchingRows(string status)
    {
        var job = await SeedAsync(12);
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var result = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(job.Id) + $"?status={status}&pageSize=2&page=2"))!;
        Assert.Equal(3, result.Total);
        var row = Assert.Single(result.Items);
        Assert.Equal(status, row.Status);
        Assert.Equal(10 + Array.IndexOf(Enum.GetNames<ImportRowStatus>(), status), row.RowNumber);
    }

    [Fact]
    public async Task EmptyExistingJobAndOutOfRangePages_ReturnEmptyLists()
    {
        var empty = await SeedAsync(0);
        var populated = await SeedAsync(4);
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var result = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(empty.Id)))!;
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
        Assert.Equal(50, result.PageSize);
        foreach (var page in new[] { "2", int.MaxValue.ToString() })
        {
            result = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(populated.Id) + $"?page={page}&pageSize=200"))!;
            Assert.Empty(result.Items);
            Assert.Equal(4, result.Total);
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("status=Processing")]
    [InlineData("status=invalid")]
    [InlineData("status=3")]
    [InlineData("status=")]
    [InlineData("page=abc")]
    [InlineData("pageSize=2147483648")]
    public async Task InvalidQuery_ReturnsProblemDetailsWithStableCode(string query)
    {
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync(Url(Guid.NewGuid()) + "?" + query);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task MissingJob_Returns404RatherThanEmptyPage()
    {
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync(Url(Guid.NewGuid()));
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "job_not_found");
    }

    [Fact]
    public async Task ProcessingJob_ExposesCommittedRowsWithUnknownTotal_UntilEof()
    {
        var job = await SeedAsync(4);
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var progress = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{job.Id}"))!;
        Assert.Equal("Processing", progress.Status);
        Assert.Null(progress.TotalRows);
        Assert.Equal(4, progress.ProcessedRows);
        var rows = (await client.GetFromJsonAsync<PagedResult<JobRowDto>>(Url(job.Id)))!;
        Assert.Equal(4, rows.Total);
        await using (var database = fixture.CreateContext())
        {
            var tracked = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
            tracked.SetTotalRows(4, DateTimeOffset.UtcNow);
            tracked.Complete(DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }
        progress = (await client.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{job.Id}"))!;
        Assert.Equal(4, progress.TotalRows);
        Assert.Equal("CompletedWithErrors", progress.Status);
    }

    private async Task<ImportJob> SeedAsync(int rowCount)
    {
        await using var database = fixture.CreateContext();
        var source = DataSource.Create("Rows API", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true)]), DateTimeOffset.UtcNow);
        var job = ImportJob.Create(source.Id, "rows.csv", Guid.NewGuid().ToString("N"), new string('a', 64), source.SchemaJson, 1, DateTimeOffset.UtcNow);
        job.AcquireLease("test", DateTimeOffset.UtcNow, 60);
        database.DataSources.Add(source);
        database.ImportJobs.Add(job);
        // Deliberately insert in reverse order to verify SQL ordering rather than insertion order.
        foreach (var index in Enumerable.Range(0, rowCount).Reverse())
        {
            var status = (ImportRowStatus)(index % 4 + 1);
            database.ImportRows.Add(ImportRow.Create(job.Id, index + 2, status,
                status == ImportRowStatus.Invalid ? "{\"email\":\"invalid\"}" : null, $"key-{index}",
                status == ImportRowStatus.Invalid ? [new("email", "invalid_email", "A valid email address is required.")] : null,
                DateTimeOffset.UtcNow, status == ImportRowStatus.Invalid ? null : new string('b', 64)));
        }
        if (rowCount > 0)
        {
            job.RecordBatchCompletion(rowCount + 1, (rowCount + 3) / 4, (rowCount + 2) / 4,
                (rowCount + 1) / 4, rowCount / 4, DateTimeOffset.UtcNow);
        }
        await database.SaveChangesAsync();
        return job;
    }

    private static string Url(Guid id) => $"/api/v1/import-jobs/{id}/rows";

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, json.RootElement.GetProperty("status").GetInt32());
    }
}
