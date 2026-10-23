namespace DataFlow.Application.Abstractions;

public interface IFileStorage
{
    Task UploadAsync(string objectKey, string filePath, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
    Task<bool> TryDeleteAsync(string objectKey, Guid dataSourceId, CancellationToken cancellationToken);
}
