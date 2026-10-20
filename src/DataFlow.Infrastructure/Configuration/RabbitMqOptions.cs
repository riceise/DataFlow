using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required(AllowEmptyStrings = false, ErrorMessage = "RabbitMq:Host is required.")]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "RabbitMq:Port must be between 1 and 65535.")]
    public int Port { get; init; } = 5672;

    [Required(AllowEmptyStrings = false, ErrorMessage = "RabbitMq:VirtualHost is required.")]
    public string VirtualHost { get; init; } = "/";

    [Required(AllowEmptyStrings = false, ErrorMessage = "RabbitMq:User is required.")]
    public string User { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "RabbitMq:Password is required.")]
    public string Password { get; init; } = string.Empty;
}
