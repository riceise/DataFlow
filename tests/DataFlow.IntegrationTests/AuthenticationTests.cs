using System.Net;
using System.Net.Http.Json;
using DataFlow.Application.DataSources;
using DataFlow.Domain.Entities;
using DataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.IntegrationTests;

public sealed class AuthenticationTests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task OtherUserCannotAccessSourceJobsOrControls()
    {
        using var owner = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var created = await owner.PostAsJsonAsync("/api/v1/data-sources", DataSourcesStage4Tests.ValidRequest());
        created.EnsureSuccessStatusCode();
        var source = (await created.Content.ReadFromJsonAsync<DataSourceDto>())!;
        await using var database = fixture.CreateContext();
        var entity = await database.DataSources.FindAsync(source.Id);
        var job = ImportJob.Create(source.Id, "a.csv", "auth-test", new string('a', 64), entity!.SchemaJson, 1, DateTimeOffset.UtcNow);
        database.ImportJobs.Add(job);
        await database.SaveChangesAsync();
        using var other = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        other.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        other.DefaultRequestHeaders.Add("Idempotency-Key", "other-user");
        foreach (var url in new[] { $"/api/v1/data-sources/{source.Id}", $"/api/v1/data-sources/{source.Id}/jobs",
            $"/api/v1/jobs/{job.Id}", $"/api/v1/import-jobs/{job.Id}/rows" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        }

        foreach (var action in new[] { "retry", "cancel" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/import-jobs/{job.Id}/{action}", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/data-sources/{source.Id}/files", null)).StatusCode);
        using var anonymous = fixture.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/data-sources")).StatusCode);
    }
}
