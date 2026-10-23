using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Storage:Bucket is required.")]
    [RegularExpression(@"^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", ErrorMessage = "Storage:Bucket must be a valid S3 bucket name.")]
    public string Bucket { get; init; } = string.Empty;

    public bool UseSsl { get; init; }
    public string Region { get; init; } = "us-east-1";
    // Deployment must provision a private bucket, with public policies/access disabled.
    [Range(typeof(bool), "true", "true")]
    public bool RequirePrivateBucket { get; init; } = true;
    [Range(1, long.MaxValue)]
    public long MaxFileBytes { get; init; } = 50 * 1024 * 1024;
    [Required]
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "dataflow-uploads");
    [Range(1, 31536000)]
    public int OrphanAgeSeconds { get; init; } = 86400;
    [Range(1, 86400)]
    public int CleanupIntervalSeconds { get; init; } = 300;
}
