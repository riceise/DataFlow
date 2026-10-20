using System.Net;
using DataFlow.Api.Middleware;
using DataFlow.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class InfrastructureStage2Tests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InfrastructureStage2Tests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthLive_ReturnsOkAndCorrelationHeader()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        Assert.False(string.IsNullOrWhiteSpace(values.Single()));
    }

    [Fact]
    public async Task CorrelationMiddleware_PreservesValidHeaderAndReplacesInvalidHeader()
    {
        using var client = _factory.CreateClient();

        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        validRequest.Headers.Add(CorrelationIdMiddleware.HeaderName, "corr-stage2-12345");

        using var validResponse = await client.SendAsync(validRequest);
        Assert.Equal(
            "corr-stage2-12345",
            validResponse.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        invalidRequest.Headers.Add(CorrelationIdMiddleware.HeaderName, "unsafe value with spaces!");

        using var invalidResponse = await client.SendAsync(invalidRequest);
        var generatedCorrelationId = invalidResponse.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.NotEqual("unsafe value with spaces!", generatedCorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(generatedCorrelationId));
    }

    [Fact]
    public void ConfigurationValidation_FailsWhenRequiredSettingsAreInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataFlow"] = "",
                ["RabbitMq:Host"] = "127.0.0.1",
                ["RabbitMq:Port"] = "70000",
                ["RabbitMq:VirtualHost"] = "/",
                ["RabbitMq:User"] = "dataflow",
                ["RabbitMq:Password"] = "secret",
                ["Storage:Endpoint"] = "127.0.0.1:9000",
                ["Storage:AccessKey"] = "key",
                ["Storage:SecretKey"] = "secret",
                ["Storage:Bucket"] = "INVALID_BUCKET_NAME",
                ["Import:BatchSize"] = "0"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDataFlowConfiguration(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ImportProcessingOptions>>().Value);
    }
}
