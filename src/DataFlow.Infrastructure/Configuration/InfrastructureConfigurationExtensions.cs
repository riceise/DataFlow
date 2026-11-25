using System.ComponentModel.DataAnnotations;
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
            .AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
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
            .Validate(options => options.HeartbeatSeconds < options.LeaseSeconds, "Heartbeat must be shorter than the lease.")
            .Validate(options => options.MaxPreviewRawBytesPerJob >= options.MaxPreviewRawBytesPerPart,
                "Import:MaxPreviewRawBytesPerJob must not be smaller than the per-part preview budget.")
            .Validate(options => Enum.IsDefined(options.PreparedUploadIntegrity), "Import:PreparedUploadIntegrity is invalid.")
            .Validate(options => ValidNestedOptions(options), "Import ZIP/XLSX nested options are invalid.")
            .ValidateOnStart();

        services
            .AddOptions<PreparedUploadEncryptionOptions>()
            .Bind(configuration.GetSection(PreparedUploadEncryptionOptions.SectionName));

        return services;
    }

    private static bool ValidNestedOptions(ImportProcessingOptions options) =>
        Validator.TryValidateObject(options.Zip, new ValidationContext(options.Zip), null, true) &&
        Validator.TryValidateObject(options.Xlsx, new ValidationContext(options.Xlsx), null, true);
}
