using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.Infrastructure.Configuration;

public static class InfrastructureConfigurationExtensions
{
    public static IServiceCollection AddDataFlowConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.Region), "Storage:Region is required.")
            .Validate(options => string.IsNullOrEmpty(options.AccessKey) == string.IsNullOrEmpty(options.SecretKey),
                "Storage credentials must be configured together or resolved by the AWS credential chain.")
            .ValidateOnStart();

        services
            .AddOptions<ImportProcessingOptions>()
            .Bind(configuration.GetSection(ImportProcessingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
