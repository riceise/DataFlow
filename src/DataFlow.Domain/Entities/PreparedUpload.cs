using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.Entities;

public enum PreparedUploadStatus
{
    Preparing = 1,
    Ready = 2,
    Consumed = 3,
    Expired = 4,
    Aborted = 5
}

public sealed class PreparedUpload
{
    private PreparedUpload()
    {
        IdempotencyKey = string.Empty;
        RequestFingerprint = string.Empty;
        OriginalFileName = string.Empty;
        FileSha256 = string.Empty;
        TokenHash = string.Empty;
        StorageChecksumMetadata = string.Empty;
        ReplayKeyId = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public Guid DataSourceId { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string RequestFingerprint { get; private set; }
    public string? StorageKey { get; private set; }
    public string OriginalFileName { get; private set; }
    public string FileSha256 { get; private set; }
    public AcceptedFileFormat Format { get; private set; }
    public long ContentLength { get; private set; }
    public string StorageChecksumMetadata { get; private set; }
    public string TokenHash { get; private set; }
    public byte[]? ReplayResponseCiphertext { get; private set; }
    public string ReplayKeyId { get; private set; }
    public PreparedUploadStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public Guid? JobId { get; private set; }
    public string? ManifestJson { get; private set; }

    public static PreparedUpload CreateReady(
        Guid ownerUserId,
        Guid dataSourceId,
        string idempotencyKey,
        string requestFingerprint,
        string storageKey,
        string originalFileName,
        string fileSha256,
        AcceptedFileFormat format,
        long contentLength,
        string tokenHash,
        byte[] replayResponseCiphertext,
        string replayKeyId,
        string manifestJson,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        Guid? id = null)
    {
        if (ownerUserId == Guid.Empty || dataSourceId == Guid.Empty)
        {
            throw new DomainException("invalid_prepared_upload", "Prepared upload requires an owner and data source.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            requestFingerprint.Length != 64 || fileSha256.Length != 64 || tokenHash.Length != 64 ||
            string.IsNullOrWhiteSpace(storageKey) || string.IsNullOrWhiteSpace(originalFileName) ||
            contentLength <= 0 || replayResponseCiphertext.Length == 0 || string.IsNullOrWhiteSpace(replayKeyId) ||
            string.IsNullOrWhiteSpace(manifestJson) || expiresAt <= createdAt)
        {
            throw new DomainException("invalid_prepared_upload", "Prepared upload data is invalid.");
        }

        if (format is not (AcceptedFileFormat.Csv or AcceptedFileFormat.Xlsx or AcceptedFileFormat.Zip))
        {
            throw new DomainException("file_format_not_allowed", "Only CSV, XLSX and ZIP CSV can be prepared.");
        }

        return new PreparedUpload
        {
            Id = id ?? Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            DataSourceId = dataSourceId,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            StorageKey = storageKey,
            OriginalFileName = originalFileName,
            FileSha256 = fileSha256.ToLowerInvariant(),
            Format = format,
            ContentLength = contentLength,
            StorageChecksumMetadata = fileSha256.ToLowerInvariant(),
            TokenHash = tokenHash,
            ReplayResponseCiphertext = replayResponseCiphertext,
            ReplayKeyId = replayKeyId,
            ManifestJson = manifestJson,
            Status = PreparedUploadStatus.Ready,
            CreatedAt = createdAt.ToUniversalTime(),
            ExpiresAt = expiresAt.ToUniversalTime()
        };
    }

    public void Consume(Guid jobId, DateTimeOffset nowUtc)
    {
        if (Status != PreparedUploadStatus.Ready || StorageKey is null || ManifestJson is null)
        {
            throw new DomainException("upload_token_consumed", "Prepared upload token has already been consumed.");
        }

        if (jobId == Guid.Empty)
        {
            throw new DomainException("invalid_job_id", "Job id is required.");
        }

        Status = PreparedUploadStatus.Consumed;
        ConsumedAt = nowUtc.ToUniversalTime();
        JobId = jobId;
    }

    public void BeginExpiryCleanup()
    {
        if (Status != PreparedUploadStatus.Ready || StorageKey is null)
        {
            throw new DomainException("prepared_upload_expired", "Only a ready prepared upload can begin expiry cleanup.");
        }

        Status = PreparedUploadStatus.Aborted;
        ManifestJson = null;
        ReplayResponseCiphertext = null;
        ReplayKeyId = string.Empty;
        StorageChecksumMetadata = string.Empty;
    }

    public void ExpireAndClearSensitiveData()
    {
        if (Status != PreparedUploadStatus.Aborted)
        {
            throw new DomainException("prepared_upload_expired", "Prepared upload expiry cleanup has not started.");
        }

        Status = PreparedUploadStatus.Expired;
        StorageKey = null;
        ManifestJson = null;
        ReplayResponseCiphertext = null;
        ReplayKeyId = string.Empty;
        StorageChecksumMetadata = string.Empty;
    }
}
