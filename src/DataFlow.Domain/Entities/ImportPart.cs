using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.Entities;

public sealed class ImportPart
{
    private ImportPart()
    {
        SourceName = string.Empty;
        SourceLocator = string.Empty;
    }

    private ImportPart(ImportJob job, DateTimeOffset nowUtc, DataSourceDataType dataType)
    {
        Id = Guid.NewGuid();
        ImportJobId = job.Id;
        PartIndex = 0;
        PartType = Path.GetExtension(job.OriginalFileName).ToLowerInvariant() switch
        {
            ".xlsx" => ImportPartType.XlsxWorksheet,
            ".csv" => ImportPartType.Csv,
            _ => ImportPartType.Other
        };
        SourceName = job.OriginalFileName;
        SourceLocator = PartType == ImportPartType.XlsxWorksheet ? "xlsx:first-sheet" : "file:single-file";
        Status = ImportPartStatus.Pending;
        DeduplicationScope = dataType == DataSourceDataType.Customer ? "customer:job" : $"part:{PartIndex}";
        CreatedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public Guid ImportJobId { get; private set; }
    public int PartIndex { get; private set; }
    public ImportPartType PartType { get; private set; }
    public string SourceName { get; private set; }
    public string? SheetName { get; private set; }
    public int? SheetIndex { get; private set; }
    public string SourceLocator { get; private set; }
    public ImportPartStatus Status { get; private set; }
    public long LastCommittedPhysicalRowNumber { get; private set; }
    public int? TotalRows { get; private set; }
    public long ProcessedRows { get; private set; }
    public long ValidRows { get; private set; }
    public long InsertedRows { get; private set; }
    public long UpdatedRows { get; private set; }
    public long InvalidRows { get; private set; }
    public long DuplicateRows { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public int? LastErrorRowNumber { get; private set; }
    public Guid? RuleSetVersionId { get; private set; }
    public Guid? RuleDocumentId { get; private set; }
    public string? RuleSnapshotHash { get; private set; }
    public string? RuleSetNameAtSubmission { get; private set; }
    public int? RuleVersionNumberAtSubmission { get; private set; }
    public string? DeduplicationScope { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static ImportPart CreateSingle(ImportJob job, DateTimeOffset nowUtc, DataSourceDataType dataType) => new(job, nowUtc, dataType);

    internal void Start(DateTimeOffset nowUtc)
    {
        if (Status == ImportPartStatus.Pending)
        {
            Status = ImportPartStatus.Processing;
            StartedAt ??= nowUtc.ToUniversalTime();
            UpdatedAt = nowUtc.ToUniversalTime();
        }
        else if (Status != ImportPartStatus.Processing)
        {
            throw new DomainException("import_part_not_processable", $"Part in status {Status} cannot be processed.");
        }
    }

    internal void PinRules(Guid version, Guid document, string snapshotHash, string name, int number,
        DataSourceDataType dataType, bool migration)
    {
        if (version == Guid.Empty || document == Guid.Empty || string.IsNullOrWhiteSpace(snapshotHash) ||
            snapshotHash.Length != 64 || string.IsNullOrWhiteSpace(name) || number <= 0)
        {
            throw new DomainException("invalid_part_rules", "Part rules require a version, document, snapshot hash and submission metadata.");
        }

        if (!migration && (RuleDocumentId is not null || RuleSetVersionId is not null || ProcessedRows != 0))
        {
            throw new DomainException("rules_already_pinned", "Part rules are immutable.");
        }

        EnsureCompatibleRules(version, document, snapshotHash);
        RuleSetVersionId ??= version;
        RuleDocumentId ??= document;
        RuleSnapshotHash ??= snapshotHash;
        RuleSetNameAtSubmission ??= name;
        RuleVersionNumberAtSubmission ??= number;
        DeduplicationScope = dataType == DataSourceDataType.Customer ? "customer:job" : $"part:{PartIndex}";
    }

    internal void MigrateRules(Guid version, Guid document, string snapshotHash, string name, int number,
        DataSourceDataType dataType)
    {
        if ((RuleVersionNumberAtSubmission is not null && RuleVersionNumberAtSubmission != number) ||
            (RuleSetNameAtSubmission is not null && RuleSetNameAtSubmission != name))
        {
            throw new DomainException("rule_reference_conflict", "Existing part rule metadata conflicts with the migrated version.");
        }

        PinRules(version, document, snapshotHash, name, number, dataType, migration: true);
    }

    internal void RecordBatchCompletion(int checkpoint, int inserted, int updated, int invalid, int duplicate, int valid,
        DateTimeOffset nowUtc)
    {
        if (Status != ImportPartStatus.Processing)
        {
            throw new DomainException("import_part_not_processing", $"Part in status {Status} cannot record a batch.");
        }

        if (checkpoint <= LastCommittedPhysicalRowNumber)
        {
            throw new DomainException("non_monotonic_row_number", "Part checkpoints must increase.");
        }

        var batchRows = (long)inserted + updated + invalid + duplicate + valid;
        if (TotalRows is not null && ProcessedRows + batchRows > TotalRows)
        {
            throw new DomainException("row_count_exceeds_total", "Processed rows cannot exceed total rows.");
        }

        LastCommittedPhysicalRowNumber = checkpoint;
        InsertedRows += inserted;
        UpdatedRows += updated;
        InvalidRows += invalid;
        DuplicateRows += duplicate;
        ValidRows += valid;
        ProcessedRows = ValidRows + InsertedRows + UpdatedRows + InvalidRows + DuplicateRows;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    internal void SetTotalRows(int totalRows, DateTimeOffset nowUtc)
    {
        if (Status != ImportPartStatus.Processing || totalRows < 0 || totalRows < ProcessedRows)
        {
            throw new DomainException("invalid_total_rows", "Part total rows cannot be negative or below processed rows.");
        }

        TotalRows = totalRows;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    internal void Complete(DateTimeOffset nowUtc)
    {
        if (Status != ImportPartStatus.Processing || TotalRows is null || ProcessedRows != TotalRows)
        {
            throw new DomainException("import_part_incomplete", "Only a fully processed part can be completed.");
        }

        Status = InvalidRows > 0 ? ImportPartStatus.CompletedWithErrors : ImportPartStatus.Completed;
        CompletedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CompletedAt.Value;
        LastErrorCode = null;
        LastErrorRowNumber = null;
    }

    internal void RecordError(string errorCode, int? rowNumber, DateTimeOffset nowUtc)
    {
        LastErrorCode = errorCode;
        LastErrorRowNumber = rowNumber;
        UpdatedAt = nowUtc.ToUniversalTime();
    }

    internal void Fail(string errorCode, int? rowNumber, DateTimeOffset nowUtc)
    {
        if (Status is not (ImportPartStatus.Pending or ImportPartStatus.Processing))
        {
            throw new DomainException("import_part_not_processing", "Only a pending or processing part can fail.");
        }

        RecordError(errorCode, rowNumber, nowUtc);
        Status = ImportPartStatus.Failed;
        CompletedAt = nowUtc.ToUniversalTime();
    }

    internal void Cancel(DateTimeOffset nowUtc)
    {
        if (Status is not (ImportPartStatus.Pending or ImportPartStatus.Processing))
        {
            throw new DomainException("import_part_not_cancellable", $"Part in status {Status} cannot be cancelled.");
        }

        Status = ImportPartStatus.Cancelled;
        CompletedAt = nowUtc.ToUniversalTime();
        UpdatedAt = CompletedAt.Value;
        LastErrorCode = null;
        LastErrorRowNumber = null;
    }

    internal void ValidateFor(ImportJob job)
    {
        if (ImportJobId != job.Id)
        {
            throw new DomainException("import_part_job_mismatch", "Part and job identifiers do not match.");
        }

        var active = job.Status is ImportJobStatus.Queued or ImportJobStatus.Processing or ImportJobStatus.RetryScheduled;
        if ((active && Status == ImportPartStatus.Failed) ||
            (job.Status == ImportJobStatus.Failed && job.Retryable == 0 && Status != ImportPartStatus.Failed) ||
            (job.Status == ImportJobStatus.Failed && job.Retryable != 0 && Status != ImportPartStatus.Processing))
        {
            throw new DomainException("invalid_job_part_state", $"Job status {job.Status} is incompatible with part status {Status}.");
        }
    }

    private void EnsureCompatibleRules(Guid version, Guid document, string snapshotHash)
    {
        if ((RuleSetVersionId is not null && RuleSetVersionId != version) ||
            (RuleDocumentId is not null && RuleDocumentId != document) ||
            (RuleSnapshotHash is not null && !string.Equals(RuleSnapshotHash, snapshotHash, StringComparison.Ordinal)))
        {
            throw new DomainException("rule_reference_conflict", "Existing part rule references conflict with the migrated version.");
        }
    }
}
