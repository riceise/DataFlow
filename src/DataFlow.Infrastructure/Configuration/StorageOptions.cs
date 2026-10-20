using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required(AllowEmptyStrings = false, ErrorMessage = "Storage:Endpoint is required.")]
    public string Endpoint { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Storage:AccessKey is required.")]
    public string AccessKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Storage:SecretKey is required.")]
    public string SecretKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Storage:Bucket is required.")]
    [RegularExpression(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", ErrorMessage = "Storage:Bucket must be a valid S3 bucket name.")]
    public string Bucket { get; init; } = string.Empty;

    public bool UseSsl { get; init; }
}
