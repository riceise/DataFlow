using System.Net;
using System.Net.Http.Json;
using DataFlow.Application.DataSources;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.IntegrationTests;

public sealed class OperatorStage10Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task GlobalList_FiltersPagesAndIsolatesOwners_AndReportsCapabilities()
    {
        using var client = fixture.CreateClient(new() { BaseAddress = new("https://localhost") });
        await using var db = fixture.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = DataSource.Create("Operator source", DataSourceDataType.Customer, AcceptedFileFormat.All,
            new DataSourceSchema(1, ["externalId"], [new DataSourceFieldDefinition("externalId", "string", true)]), now);
        source.AssignOwner(TestAuthentication.UserId);
        var foreignSource = DataSource.Create("Foreign source", DataSourceDataType.Customer, AcceptedFileFormat.All, source.GetParsedSchema(), now);
        foreignSource.AssignOwner(Guid.NewGuid());
        db.DataSources.AddRange(source, foreignSource);
        var queued = ImportJob.Create(source.Id, "queued.csv", "queued", new string('a', 64), "{}", 1, now);
        var retryable = ImportJob.Create(source.Id, "retry.csv", "retry", new string('b', 64), "{}", 1, now.AddSeconds(-1));
        retryable.Fail("storage_unavailable", "Storage unavailable", true, now);
        var invalid = ImportJob.Create(source.Id, "invalid.csv", "invalid", new string('c', 64), "{}", 1, now.AddSeconds(-2));
        invalid.Fail("invalid_csv_header", "Invalid header", false, now);
        var foreign = ImportJob.Create(foreignSource.Id, "private.csv", "private", new string('d', 64), "{}", 1, now);
        db.ImportJobs.AddRange(queued, retryable, invalid, foreign);
        await db.SaveChangesAsync();

        var all = (await client.GetFromJsonAsync<PagedResult<JobDto>>("/api/v1/import-jobs?pageSize=100"))!;
        Assert.DoesNotContain(all.Items, j => j.Id == foreign.Id);
        var first = (await client.GetFromJsonAsync<PagedResult<JobDto>>($"/api/v1/import-jobs?dataSourceId={source.Id}&pageSize=1"))!;
        Assert.Equal(3, first.Total);
        Assert.Equal(queued.Id, Assert.Single(first.Items).Id);
        Assert.True(first.Items[0].CanCancel);
        Assert.False(first.Items[0].CanRetry);
        Assert.Equal("queued.csv", first.Items[0].OriginalFileName);
        Assert.Null(first.Items[0].TotalRows);

        var failed = (await client.GetFromJsonAsync<PagedResult<JobDto>>($"/api/v1/import-jobs?dataSourceId={source.Id}&status=Failed"))!;
        Assert.Equal(2, failed.Total);
        Assert.True(failed.Items.Single(j => j.Id == retryable.Id).CanRetry);
        Assert.False(failed.Items.Single(j => j.Id == invalid.Id).CanRetry);
        Assert.All(failed.Items, j => Assert.False(j.CanCancel));
        using var missing = await client.GetAsync($"/api/v1/import-jobs?dataSourceId={foreignSource.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var badPage = await client.GetAsync("/api/v1/import-jobs?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        using var badStatus = await client.GetAsync("/api/v1/import-jobs?status=Unknown");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
    }
}
