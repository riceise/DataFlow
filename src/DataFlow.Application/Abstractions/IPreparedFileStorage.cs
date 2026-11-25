namespace DataFlow.Application.Abstractions;

public sealed record PreparedStorageMetadata(long ContentLength, string? Sha256Metadata, string? NativeSha256);

public interface IPreparedFileStorage
{
    Task UploadAsync(string objectKey, string filePath, string fileSha256, CancellationToken cancellationToken);
    Task<PreparedStorageMetadata> HeadAsync(string objectKey, bool includeNativeChecksum, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
    Task<bool> TryDeleteAsync(string objectKey, Guid dataSourceId, CancellationToken cancellationToken);
}
