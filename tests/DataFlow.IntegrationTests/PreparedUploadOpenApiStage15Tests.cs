using System.Net;
using System.Text.Json;

namespace DataFlow.IntegrationTests;

public sealed class PreparedUploadOpenApiStage15Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task PrepareInspectCommitAndZipOptIn_ArePublishedWithoutRemovingLegacyFiles()
    {
        using var client = fixture.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[]
                 {
                     "/api/v1/data-sources/{id}/file-uploads/prepare",
                     "/api/v1/data-sources/{id}/file-uploads/inspect",
                     "/api/v1/data-sources/{id}/file-uploads/commit",
                     "/api/v1/data-sources/{id}/files",
                     "/api/v1/rule-sources/{id}/zip-csv"
                 })
        {
            Assert.True(paths.TryGetProperty(path, out var operation), $"Missing OpenAPI path {path}");
            Assert.True(operation.EnumerateObject().Any());
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schema in new[]
                 {
                     "PreparedUploadResponse", "PreparedPartDto", "PartPreviewDto", "InspectPreparedPartRequest",
                     "CommitPreparedUploadRequest", "CommitPreparedPartRequest", "JobAcceptedDto"
                 })
        {
            Assert.True(schemas.TryGetProperty(schema, out _), $"Missing OpenAPI schema {schema}");
        }
    }
}
