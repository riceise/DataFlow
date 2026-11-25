using DataFlow.Application.Imports;
using DataFlow.Application.Uploads;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Parsing;
using Microsoft.Extensions.Options;
using System.Text;

namespace DataFlow.IntegrationTests;

public sealed class CsvUploadInspectorStage15Tests
{
    [Fact]
    public async Task MaxRows_DoesNotBlockInspection()
    {
        var options = Options.Create(new ImportProcessingOptions { MaxRows = 1 });
        var inspector = new CsvUploadInspector(options);
        var schema = new DataSourceSchema(1, ["code"], [new DataSourceFieldDefinition("code", "string", true)]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("code\na\nb\n"));
        var part = await inspector.InspectAsync(stream, "a.csv", 0, schema, new PreviewBudget(1024 * 1024));
        Assert.Equal("a.csv", part.SourceName);
        Assert.Equal(new[] { "code" }, part.Headers);
    }
}
