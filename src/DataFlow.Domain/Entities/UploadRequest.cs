namespace DataFlow.Domain.Entities;

public sealed class UploadRequest
{
    private UploadRequest()
    {
        Fingerprint = string.Empty;
        StorageKey = string.Empty;
        IdempotencyKey = string.Empty;
        FileSha256 = string.Empty;
    }

    private UploadRequest(
        string idempotencyKey,
        string fingerprint,
        Guid dataSourceId,
        string storageKey,
        string fileSha256,
        DateTimeOffset nowUtc)
    {
        Id = Guid.NewGuid();
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        DataSourceId = dataSourceId;
        StorageKey = storageKey;
        FileSha256 = fileSha256;
        CreatedAt = nowUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string Fingerprint { get; private set; }
    public Guid DataSourceId { get; private set; }
    public string StorageKey { get; private set; }
    public string FileSha256 { get; private set; }
    public Guid? JobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static UploadRequest Create(
        string idempotencyKey,
        string fingerprint,
        Guid dataSourceId,
        string storageKey,
        string fileSha256,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        }

        if (idempotencyKey.Length > 128)
        {
            throw new ArgumentException("Idempotency key is too long.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
        {
            throw new ArgumentException("Fingerprint must be a SHA-256 hex string.", nameof(fingerprint));
        }

        if (dataSourceId == Guid.Empty)
        {
            throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        }

        if (string.IsNullOrWhiteSpace(storageKey) || string.IsNullOrWhiteSpace(fileSha256))
        {
            throw new ArgumentException("Storage key and file hash are required.", nameof(storageKey));
        }

        return new UploadRequest(
            idempotencyKey,
            fingerprint,
            dataSourceId,
            storageKey,
            fileSha256,
            nowUtc);
    }

    public void LinkJob(Guid jobId)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        JobId = jobId;
    }
}
