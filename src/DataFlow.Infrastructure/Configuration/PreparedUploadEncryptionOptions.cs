namespace DataFlow.Infrastructure.Configuration;

public sealed class PreparedUploadEncryptionOptions
{
    public const string SectionName = "PreparedUploadEncryption";
    public string ActiveKeyId { get; init; } = string.Empty;
    public Dictionary<string, string> Keys { get; init; } = new(StringComparer.Ordinal);
}
