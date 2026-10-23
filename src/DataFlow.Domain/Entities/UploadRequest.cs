using DataFlow.Domain.Exceptions;

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
            throw new DomainException("invalid_idempotency_key", "Idempotency key is required.");
        }

        if (idempotencyKey.Length > 128)
        {
            throw new DomainException("idempotency_key_too_long", "Idempotency key is too long.");
        }

        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
        {
            throw new DomainException("invalid_fingerprint", "Fingerprint must be a SHA-256 hex string.");
        }

        if (dataSourceId == Guid.Empty)
        {
            throw new DomainException("invalid_data_source_id", "Data source id is required.");
        }

        if (string.IsNullOrWhiteSpace(storageKey) || string.IsNullOrWhiteSpace(fileSha256))
        {
            throw new DomainException("invalid_upload_request_data", "Storage key and file hash are required.");
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
            throw new DomainException("invalid_job_id", "Job id is required.");
        }

        JobId = jobId;
    }
}
