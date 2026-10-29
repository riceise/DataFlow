using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Infrastructure.Imports;

public sealed record ImportLease(Guid JobId, int RunVersion, string Owner, int Version);
public sealed record ImportBatchRow(ParsedRow Parsed, CustomerRowValidation Validation);

public sealed class ImportJobStore(DataFlowDbContext database)
{
    public async Task<ImportLease?> AcquireAsync(Guid jobId, int runVersion, string owner, int leaseSeconds, CancellationToken cancellationToken)
    {
        var versions = await database.Database.SqlQuery<int>($"""
            UPDATE import_jobs SET lease_owner = {owner}, lease_version = lease_version + 1,
                lease_until = clock_timestamp() + make_interval(secs => {leaseSeconds}), status = 2,
                next_attempt_at = NULL, started_at = COALESCE(started_at, clock_timestamp()),
                attempt_count = attempt_count + 1, updated_at = clock_timestamp()
            WHERE id = {jobId} AND run_version = {runVersion} AND
                (status = 1 OR (status = 3 AND (next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp()))
                 OR (status = 2 AND lease_until <= clock_timestamp()))
            RETURNING lease_version AS "Value"
            """).ToListAsync(cancellationToken);
        return versions.Count == 0 ? null : new(jobId, runVersion, owner, versions[0]);
    }

    public async Task CommitBatchAsync(ImportLease lease, IReadOnlyList<ImportBatchRow> rows, int leaseSeconds, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await SetTimeoutsAsync(cancellationToken);
        var job = await LockAsync(lease, cancellationToken);
        var deadline = job.LeaseUntil!.Value;
        var processedAt = await NowAsync(cancellationToken);
        var counts = new int[5];
        foreach (var row in rows)
        {
            if (row.Validation.IsEmpty) { continue; }
            var status = row.Validation.IsValid
                ? await UpsertAsync(job.DataSourceId, row.Validation, cancellationToken)
                : ImportRowStatus.Invalid;
            counts[(int)status]++;
            database.ImportRows.Add(ImportRow.Create(job.Id, row.Parsed.RowNumber, status,
                status == ImportRowStatus.Invalid ? row.Parsed.RawData : null,
                row.Validation.NaturalKey, row.Validation.Errors, processedAt, row.Validation.RowHash));
        }
        var now = await VerifyAsync(job, lease, cancellationToken);
        job.RecordBatchCompletion(rows[^1].Parsed.CheckpointRowNumber, counts[1], counts[2], counts[3], counts[4], now);
        job.RenewLease(lease.Owner, now, leaseSeconds, lease.Version);
        await database.SaveChangesAsync(cancellationToken);
        // Check the database clock again after all writes, while retaining the job lock.
        await VerifyAsync(job, lease, cancellationToken);
        if (await NowAsync(cancellationToken) >= deadline) { throw new ImportLeaseUnavailableException(); }
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    public async Task FinalizeAsync(ImportLease lease, Guid messageId, int? totalRows,
        string? failureCode, bool retryable, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await SetTimeoutsAsync(cancellationToken);
        var job = await LockAsync(lease, cancellationToken);
        var deadline = job.LeaseUntil!.Value;
        var now = await VerifyAsync(job, lease, cancellationToken);
        if (failureCode is not null)
        {
            job.Fail(failureCode, retryable ? "Import interrupted by an infrastructure failure." : "The import file cannot be processed.", retryable, now);
        }
        else if (job.CancelRequested) { job.ConfirmCancel(now); }
        else
        {
            job.SetTotalRows(totalRows!.Value, now);
            job.Complete(now);
        }
        database.ProcessedMessages.Add(ProcessedMessage.Create(messageId.ToString(), job.Id, lease.RunVersion, now));
        database.OutboxMessages.Add(OutboxMessage.Create("import.job.finished", JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            runVersion = job.RunVersion,
            status = job.Status.ToString(),
            job.ProcessedRows,
            job.InsertedRows,
            job.UpdatedRows,
            job.InvalidRows,
            job.DuplicateRows
        }), now));
        await database.SaveChangesAsync(cancellationToken);
        // Terminal transitions clear the lease in the entity; verify the captured deadline.
        if (await NowAsync(cancellationToken) >= deadline) { throw new ImportLeaseUnavailableException(); }
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task<ImportJob> LockAsync(ImportLease lease, CancellationToken cancellationToken)
    {
        var job = await database.ImportJobs.FromSqlInterpolated($"SELECT * FROM import_jobs WHERE id = {lease.JobId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        await VerifyAsync(job, lease, cancellationToken);
        return job;
    }

    private async Task<DateTimeOffset> VerifyAsync(ImportJob job, ImportLease lease, CancellationToken cancellationToken)
    {
        var now = await NowAsync(cancellationToken);
        if (job.RunVersion != lease.RunVersion || job.Status != ImportJobStatus.Processing ||
            job.LeaseOwner != lease.Owner || job.LeaseVersion != lease.Version || job.LeaseUntil is null || job.LeaseUntil <= now)
        {
            throw new ImportLeaseUnavailableException();
        }
        return now;
    }

    private Task SetTimeoutsAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SET LOCAL transaction_timeout = '15s'; SET LOCAL statement_timeout = '10s'; SET LOCAL lock_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = '15s'", cancellationToken);

    private Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken) =>
        database.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private async Task<ImportRowStatus> UpsertAsync(Guid sourceId, CustomerRowValidation row, CancellationToken cancellationToken)
    {
        var customer = row.Customer!;
        // WHERE avoids touching timestamps for identical rows. RETURNING distinguishes
        // insert/update atomically, including a concurrent insert of the same business key.
        var outcomes = await database.Database.SqlQuery<int>($"""
            INSERT INTO customers (id, data_source_id, external_id, name, email, registered_at, is_active,
                natural_key, row_hash, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {sourceId}, {customer.ExternalId}, {customer.Name}, {customer.Email},
                {customer.RegisteredAt}, {customer.IsActive}, {row.NaturalKey}, {row.RowHash}, clock_timestamp(), clock_timestamp())
            ON CONFLICT (data_source_id, natural_key) DO UPDATE SET
                external_id = EXCLUDED.external_id, name = EXCLUDED.name, email = EXCLUDED.email,
                registered_at = EXCLUDED.registered_at, is_active = EXCLUDED.is_active,
                row_hash = EXCLUDED.row_hash, updated_at = clock_timestamp()
            WHERE customers.row_hash <> EXCLUDED.row_hash
            RETURNING CASE WHEN xmax = 0 THEN 1 ELSE 2 END AS "Value"
            """).ToListAsync(cancellationToken);
        return outcomes.Count == 0 ? ImportRowStatus.Duplicate : (ImportRowStatus)outcomes[0];
    }
}
