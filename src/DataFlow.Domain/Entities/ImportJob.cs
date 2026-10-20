using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.Entities;

public sealed class ImportJob
{
    private ImportJob()
    {
        OriginalFileName = string.Empty;
        StorageKey = string.Empty;
        FileSha256 = string.Empty;
        SchemaJson = string.Empty;
    }

    private ImportJob(
        Guid id,
        Guid dataSourceId,
        string originalFileName,
        string storageKey,
        string fileSha256,
        string schemaJson,
        int schemaVersion,
        DateTimeOffset nowUtc)
    {
        Id = id;
        DataSourceId = dataSourceId;
        OriginalFileName = originalFileName;
        StorageKey = storageKey;
        FileSha256 = fileSha256;
        SchemaJson = schemaJson;
        SchemaVersion = schemaVersion;
        Status = ImportJobStatus.Queued;
        RunVersion = 1;
        CreatedAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    public Guid Id { get; private set; }
    public Guid DataSourceId { get; private set; }
    public string OriginalFileName { get; private set; }
    public string StorageKey { get; private set; }
    public string FileSha256 { get; private set; }
    public string SchemaJson { get; private set; }
    public int SchemaVersion { get; private set; }
    public ImportJobStatus Status { get; private set; }
    public int? TotalRows { get; private set; }
    public long ProcessedRows { get; private set; }
    public long InsertedRows { get; private set; }
    public long UpdatedRows { get; private set; }
    public long InvalidRows { get; private set; }
    public long DuplicateRows { get; private set; }
    public long LastCommittedRowNumber { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? LastError { get; private set; }
    public string? LastErrorCode { get; private set; }
    public int Retryable { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public int LeaseVersion { get; private set; }
    public bool CancelRequested { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int RunVersion { get; private set; }

    public static ImportJob Create(
        Guid dataSourceId,
        string originalFileName,
        string storageKey,
        string fileSha256,
        string schemaJson,
        int schemaVersion,
        DateTimeOffset nowUtc)
    {
        if (dataSourceId == Guid.Empty)
        {
            throw new DomainException("invalid_data_source_id", "Job requires a data source id.");
        }

        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new DomainException("invalid_original_file_name", "Job requires an original file name.");
        }

        if (string.IsNullOrWhiteSpace(storageKey) || string.IsNullOrWhiteSpace(fileSha256) || string.IsNullOrWhiteSpace(schemaJson))
        {
            throw new DomainException("invalid_job_upload_data", "Job requires storage key, file hash and schema snapshot.");
        }

        if (fileSha256.Length != 64)
        {
            throw new DomainException("invalid_file_hash", "File SHA-256 must be 64 characters.");
        }

        if (schemaVersion <= 0)
        {
            throw new DomainException("invalid_schema_version", "Schema version must be positive.");
        }

        return new ImportJob(
            Guid.NewGuid(),
            dataSourceId,
            originalFileName.Trim(),
            storageKey,
            fileSha256,
            schemaJson,
            schemaVersion,
            nowUtc.ToUniversalTime());
    }

    public void AcquireLease(string owner, DateTimeOffset nowUtc, int leaseSeconds)
    {
        ValidateLeaseArguments(owner, leaseSeconds);
        nowUtc = nowUtc.ToUniversalTime();

        if (Status != ImportJobStatus.Queued &&
            Status != ImportJobStatus.RetryScheduled &&
            !(Status == ImportJobStatus.Processing && LeaseUntil is not null && LeaseUntil <= nowUtc))
        {
            throw new DomainException("job_not_acquirable", $"Job in status {Status} cannot acquire a lease.");
        }

        if (Status == ImportJobStatus.RetryScheduled && NextAttemptAt > nowUtc)
        {
            throw new DomainException("retry_not_due", "Scheduled retry is not due yet.");
        }

        LeaseOwner = owner;
        LeaseUntil = nowUtc.AddSeconds(leaseSeconds);
        LeaseVersion += 1;
        Status = ImportJobStatus.Processing;
        NextAttemptAt = null;
        StartedAt ??= nowUtc;
        AttemptCount += 1;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public void RenewLease(string owner, DateTimeOffset nowUtc, int leaseSeconds, int expectedVersion)
    {
        ValidateLeaseArguments(owner, leaseSeconds);
        VerifyLease(owner, expectedVersion, nowUtc);

        LeaseUntil = nowUtc.ToUniversalTime().AddSeconds(leaseSeconds);
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public void VerifyLease(string owner, int expectedVersion, DateTimeOffset nowUtc)
    {
        if (Status != ImportJobStatus.Processing)
        {
            throw new DomainException("job_not_processing", $"Job cannot verify lease in status {Status}.");
        }

        if (LeaseOwner != owner)
        {
            throw new DomainException("lease_owner_mismatch", "Lease owner mismatch.");
        }

        if (LeaseVersion != expectedVersion)
        {
            throw new DomainException("lease_version_mismatch", "Lease version mismatch.");
        }

        if (LeaseUntil is null || LeaseUntil <= nowUtc)
        {
            throw new DomainException("lease_expired", "Lease has expired.");
        }
    }

    public void RecordBatchCompletion(
        int lastRowNumberInBatch,
        int inserted,
        int updated,
        int invalid,
        int duplicate,
        DateTimeOffset nowUtc)
    {
        if (lastRowNumberInBatch <= 0)
        {
            throw new DomainException("invalid_row_number", "Batch row number must be positive.");
        }

        if (lastRowNumberInBatch <= LastCommittedRowNumber)
        {
            throw new DomainException("non_monotonic_row_number", "Batch row numbers must increase.");
        }

        if (inserted < 0 || updated < 0 || invalid < 0 || duplicate < 0)
        {
            throw new DomainException("invalid_row_counts", "Batch row counts cannot be negative.");
        }

        if (Status != ImportJobStatus.Processing)
        {
            throw new DomainException("job_not_processing", $"Job cannot record batch in status {Status}.");
        }

        var batchRows = (long)inserted + updated + invalid + duplicate;
        if (TotalRows is not null && ProcessedRows + batchRows > TotalRows)
        {
            throw new DomainException("row_count_exceeds_total", "Processed rows cannot exceed total rows.");
        }

        LastCommittedRowNumber = lastRowNumberInBatch;
        InsertedRows += inserted;
        UpdatedRows += updated;
        InvalidRows += invalid;
        DuplicateRows += duplicate;
        ProcessedRows = InsertedRows + UpdatedRows + InvalidRows + DuplicateRows;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public void SetTotalRows(int totalRows, DateTimeOffset nowUtc)
    {
        EnsureProcessing();

        if (totalRows < 0 || totalRows < ProcessedRows)
        {
            throw new DomainException("invalid_total_rows", "Total rows cannot be negative or below processed rows.");
        }

        TotalRows = totalRows;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public void RequestCancel()
    {
        if (Status != ImportJobStatus.Queued &&
            Status != ImportJobStatus.Processing &&
            Status != ImportJobStatus.RetryScheduled)
        {
            throw new DomainException("job_not_cancellable", $"Job in status {Status} cannot be cancelled.");
        }

        CancelRequested = true;
    }

    public void ConfirmCancel(DateTimeOffset nowUtc)
    {
        if (!CancelRequested || (Status != ImportJobStatus.Queued &&
            Status != ImportJobStatus.Processing && Status != ImportJobStatus.RetryScheduled))
        {
            throw new DomainException("job_not_cancellable", "Only an active job with a cancellation request can be cancelled.");
        }

        Status = ImportJobStatus.Cancelled;
        Retryable = 0;
        NextAttemptAt = null;
        CompletedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CompletedAt.Value;
        LastError = null;
        LastErrorCode = null;
        LeaseOwner = null;
        LeaseUntil = null;
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        EnsureProcessing();

        if (TotalRows is null)
        {
            throw new DomainException("job_missing_total_rows", "Only fully parsed jobs can be completed.");
        }

        if (ProcessedRows != TotalRows)
        {
            throw new DomainException("job_incomplete_rows", "All rows must be processed before completion.");
        }

        if (CancelRequested)
        {
            throw new DomainException("job_cancellation_requested", "Confirm the cancellation before completing the job.");
        }

        if (InvalidRows > 0)
        {
            Status = ImportJobStatus.CompletedWithErrors;
        }
        else
        {
            Status = ImportJobStatus.Completed;
        }

        CompletedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CompletedAt.Value;
        LastError = null;
        LastErrorCode = null;
        Retryable = 0;
        LeaseOwner = null;
        LeaseUntil = null;
    }

    public void Fail(string errorCode, string message, bool retryable, DateTimeOffset nowUtc)
    {
        if (IsTerminal())
        {
            throw new DomainException("job_already_terminal", "A terminal job cannot fail again.");
        }

        if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(message))
        {
            throw new DomainException("invalid_failure", "Failure requires a code and a message.");
        }

        Status = ImportJobStatus.Failed;
        CompletedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CompletedAt.Value;
        LastErrorCode = errorCode.Trim();
        LastError = message.Trim();
        Retryable = retryable ? 1 : 0;
        LeaseOwner = null;
        LeaseUntil = null;
        NextAttemptAt = null;
    }

    public void ScheduleRetry(DateTimeOffset nowUtc, DateTimeOffset nextAttemptAt)
    {
        EnsureProcessing();
        if (nextAttemptAt < nowUtc)
        {
            throw new DomainException("invalid_next_attempt", "Retry cannot be scheduled in the past.");
        }

        Status = ImportJobStatus.RetryScheduled;
        NextAttemptAt = nextAttemptAt.ToUniversalTime();
        LeaseOwner = null;
        LeaseUntil = null;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public void Retry(DateTimeOffset nowUtc)
    {
        if (Status != ImportJobStatus.Failed || Retryable == 0)
        {
            throw new DomainException("job_not_retryable", "Only a retryable failed job can be scheduled for retry.");
        }

        Status = ImportJobStatus.Queued;
        RunVersion += 1;
        StartedAt = null;
        CompletedAt = null;
        NextAttemptAt = null;
        CancelRequested = false;
        Retryable = 0;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    private void EnsureProcessing()
    {
        if (Status != ImportJobStatus.Processing)
        {
            throw new DomainException("job_not_processing", $"Job in status {Status} is not processing.");
        }
    }

    private static void ValidateLeaseArguments(string owner, int leaseSeconds)
    {
        if (string.IsNullOrWhiteSpace(owner) || leaseSeconds <= 0)
        {
            throw new DomainException("invalid_lease", "Lease requires an owner and a positive duration.");
        }
    }

    public bool IsTerminal()
    {
        return Status is ImportJobStatus.Completed
            or ImportJobStatus.CompletedWithErrors
            or ImportJobStatus.Failed
            or ImportJobStatus.Cancelled;
    }
}
