namespace DataFlow.Application.Abstractions;

public interface IImportFileReader
{
    // Caller owns and asynchronously disposes the returned streaming body.
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
}
