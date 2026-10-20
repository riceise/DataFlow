using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.Tests;

public sealed class ImportJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ImportJobStatus.Queued)]
    [InlineData(ImportJobStatus.Processing)]
    [InlineData(ImportJobStatus.RetryScheduled)]
    public void Cancellation_ReachesCancelledAndPreservesCommittedRows(ImportJobStatus status)
    {
        var job = InStatus(status);
        var processedRows = job.ProcessedRows;
        var checkpoint = job.LastCommittedRowNumber;

        job.RequestCancel();
        Assert.Equal(status, job.Status);
        job.ConfirmCancel(Now.AddSeconds(10));

        Assert.Equal(ImportJobStatus.Cancelled, job.Status);
        Assert.True(job.IsTerminal());
        Assert.Equal(processedRows, job.ProcessedRows);
        Assert.Equal(checkpoint, job.LastCommittedRowNumber);
        Assert.Equal(Now.AddSeconds(10), job.CompletedAt);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseUntil);
        Assert.Null(job.NextAttemptAt);
        Assert.Equal(0, job.Retryable);
    }

    [Fact]
    public void ConfirmCancel_RequiresCancellationRequest()
    {
        var job = InStatus(ImportJobStatus.Processing);
        AssertCode("job_not_cancellable", () => job.ConfirmCancel(Now));
        Assert.Equal(ImportJobStatus.Processing, job.Status);
    }

    [Theory]
    [InlineData(ImportJobStatus.Completed)]
    [InlineData(ImportJobStatus.CompletedWithErrors)]
    [InlineData(ImportJobStatus.Failed)]
    [InlineData(ImportJobStatus.Cancelled)]
    public void TerminalStates_RejectProcessingAndCancellationTransitions(ImportJobStatus status)
    {
        var job = InStatus(status);
        Assert.True(job.IsTerminal());
        AssertCode("job_not_acquirable", () => job.AcquireLease("worker", Now, 60));
        AssertCode("job_not_processing", () => job.Complete(Now));
        AssertCode("job_already_terminal", () => job.Fail("failure", "Failed", true, Now));
        AssertCode("job_not_cancellable", job.RequestCancel);
        AssertCode("job_not_cancellable", () => job.ConfirmCancel(Now));
        AssertCode("job_not_processing", () => job.SetTotalRows(1, Now));
        AssertCode("job_not_processing", () => job.RecordBatchCompletion(100, 1, 0, 0, 0, Now));
        AssertCode("job_not_processing", () => job.ScheduleRetry(Now, Now.AddMinutes(1)));
        Assert.Equal(status, job.Status);
    }

    [Theory]
    [InlineData(ImportJobStatus.Queued)]
    [InlineData(ImportJobStatus.RetryScheduled)]
    public void NonProcessingStates_CannotCompleteOrRecordRows(ImportJobStatus status)
    {
        var job = InStatus(status);
        AssertCode("job_not_processing", () => job.Complete(Now));
        AssertCode("job_not_processing", () => job.RecordBatchCompletion(100, 1, 0, 0, 0, Now));
        AssertCode("job_not_processing", () => job.SetTotalRows(1, Now));
    }

    [Theory]
    [InlineData(0, ImportJobStatus.Completed)]
    [InlineData(1, ImportJobStatus.CompletedWithErrors)]
    public void Complete_UsesRowResultsAndReleasesLease(int invalid, ImportJobStatus expected)
    {
        var job = NewJob();
        job.AcquireLease("worker", Now, 60);
        job.RecordBatchCompletion(8, 2, 1, invalid, 1, Now);
        job.SetTotalRows(4 + invalid, Now);

        job.Complete(Now);

        Assert.Equal(expected, job.Status);
        Assert.Equal(Now, job.CompletedAt);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseUntil);
    }

    [Fact]
    public void Complete_RejectsMissingTotalUnprocessedRowsAndPendingCancellation()
    {
        var job = NewJob();
        job.AcquireLease("worker", Now, 60);
        AssertCode("job_missing_total_rows", () => job.Complete(Now));
        job.SetTotalRows(1, Now);
        AssertCode("job_incomplete_rows", () => job.Complete(Now));
        job.RecordBatchCompletion(2, 1, 0, 0, 0, Now);
        job.RequestCancel();
        AssertCode("job_cancellation_requested", () => job.Complete(Now));
        job.ConfirmCancel(Now);
        Assert.Equal(ImportJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void EmptyFile_CanCompleteWithZeroRows()
    {
        var job = NewJob();
        job.AcquireLease("worker", Now, 60);
        job.SetTotalRows(0, Now);
        job.Complete(Now);
        Assert.Equal(ImportJobStatus.Completed, job.Status);
    }

    [Fact]
    public void Batches_AccumulateInvariantAndRejectInvalidCountsWithoutMutation()
    {
        var job = NewJob();
        job.AcquireLease("worker", Now, 60);
        job.RecordBatchCompletion(10, 3, 2, 1, 2, Now);
        job.RecordBatchCompletion(15, 1, 1, 1, 1, Now);
        Assert.Equal(12, job.ProcessedRows);
        Assert.Equal(job.ProcessedRows, job.InsertedRows + job.UpdatedRows + job.InvalidRows + job.DuplicateRows);
        Assert.Equal(15, job.LastCommittedRowNumber);

        AssertCode("non_monotonic_row_number", () => job.RecordBatchCompletion(15, 1, 0, 0, 0, Now));
        AssertCode("invalid_row_counts", () => job.RecordBatchCompletion(16, -1, 0, 0, 0, Now));
        AssertCode("invalid_total_rows", () => job.SetTotalRows(11, Now));
        job.SetTotalRows(12, Now);
        AssertCode("row_count_exceeds_total", () => job.RecordBatchCompletion(16, 1, 0, 0, 0, Now));
        Assert.Equal(12, job.ProcessedRows);
        Assert.Equal(15, job.LastCommittedRowNumber);
    }

    [Fact]
    public void Lease_RejectsConcurrentOwnerAndFencesExpiredOwnerAfterTakeover()
    {
        var job = NewJob();
        job.AcquireLease("first", Now, 60);
        var firstVersion = job.LeaseVersion;
        AssertCode("job_not_acquirable", () => job.AcquireLease("second", Now.AddSeconds(59), 60));
        AssertCode("lease_owner_mismatch", () => job.VerifyLease("second", firstVersion, Now));
        AssertCode("lease_version_mismatch", () => job.VerifyLease("first", firstVersion + 1, Now));
        AssertCode("lease_expired", () => job.RenewLease("first", Now.AddSeconds(60), 60, firstVersion));

        job.AcquireLease("second", Now.AddSeconds(60), 60);
        Assert.Equal(firstVersion + 1, job.LeaseVersion);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal(Now, job.StartedAt);
        AssertCode("lease_owner_mismatch", () => job.VerifyLease("first", firstVersion, Now.AddSeconds(61)));
        job.RenewLease("second", Now.AddSeconds(70), 60, job.LeaseVersion);
        Assert.Equal(Now.AddSeconds(130), job.LeaseUntil);
    }

    [Theory]
    [InlineData("", 60)]
    [InlineData("worker", 0)]
    [InlineData("worker", -1)]
    public void Lease_RequiresOwnerAndPositiveDuration(string owner, int seconds)
    {
        var job = NewJob();
        AssertCode("invalid_lease", () => job.AcquireLease(owner, Now, seconds));
        Assert.Equal(ImportJobStatus.Queued, job.Status);
    }

    [Fact]
    public void AutomaticRetry_WaitsUntilDueAndPreservesRunVersionAndCheckpoint()
    {
        var job = InStatus(ImportJobStatus.Processing);
        var checkpoint = job.LastCommittedRowNumber;
        AssertCode("invalid_next_attempt", () => job.ScheduleRetry(Now, Now.AddSeconds(-1)));
        job.ScheduleRetry(Now, Now.AddSeconds(30));
        Assert.Equal(ImportJobStatus.RetryScheduled, job.Status);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(1, job.RunVersion);
        AssertCode("retry_not_due", () => job.AcquireLease("worker", Now.AddSeconds(29), 60));
        job.AcquireLease("worker", Now.AddSeconds(30), 60);
        Assert.Equal(ImportJobStatus.Processing, job.Status);
        Assert.Null(job.NextAttemptAt);
        Assert.Equal(checkpoint, job.LastCommittedRowNumber);
    }

    [Fact]
    public void ManualRetry_QueuesNewRunAndPreservesCommittedData()
    {
        var job = InStatus(ImportJobStatus.Processing);
        var checkpoint = job.LastCommittedRowNumber;
        var count = job.ProcessedRows;
        job.Fail("storage_unavailable", "Storage unavailable", true, Now);
        job.Retry(Now.AddMinutes(1));
        Assert.Equal(ImportJobStatus.Queued, job.Status);
        Assert.Equal(2, job.RunVersion);
        Assert.Equal(checkpoint, job.LastCommittedRowNumber);
        Assert.Equal(count, job.ProcessedRows);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.NextAttemptAt);
        job.AcquireLease("worker", Now.AddMinutes(1), 60);
        Assert.Equal(ImportJobStatus.Processing, job.Status);
    }

    [Theory]
    [InlineData(ImportJobStatus.Queued)]
    [InlineData(ImportJobStatus.Processing)]
    [InlineData(ImportJobStatus.RetryScheduled)]
    [InlineData(ImportJobStatus.Completed)]
    [InlineData(ImportJobStatus.CompletedWithErrors)]
    [InlineData(ImportJobStatus.Cancelled)]
    public void ManualRetry_RejectsOtherStates(ImportJobStatus status)
    {
        var job = InStatus(status);
        AssertCode("job_not_retryable", () => job.Retry(Now));
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public void PermanentFailure_CannotRetry()
    {
        var job = NewJob();
        job.Fail("invalid_file", "Invalid file", false, Now);
        AssertCode("job_not_retryable", () => job.Retry(Now));
    }

    [Fact]
    public void Timestamps_AreNormalizedToUtc()
    {
        var localNow = Now.ToOffset(TimeSpan.FromHours(3));
        var job = NewJob(localNow);
        job.AcquireLease("worker", localNow, 60);
        Assert.Equal(TimeSpan.Zero, job.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, job.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, job.LeaseUntil!.Value.Offset);
        job.ScheduleRetry(localNow, localNow.AddSeconds(30));
        Assert.Equal(TimeSpan.Zero, job.NextAttemptAt!.Value.Offset);
    }

    private static ImportJob NewJob(DateTimeOffset? now = null) =>
        ImportJob.Create(Guid.NewGuid(), "customers.csv", "uploads/test", new string('a', 64), "{}", 1, now ?? Now);

    private static ImportJob InStatus(ImportJobStatus status)
    {
        var job = NewJob();
        if (status == ImportJobStatus.Queued)
        {
            return job;
        }

        job.AcquireLease("worker", Now, 60);
        job.RecordBatchCompletion(5, 1, 1, status == ImportJobStatus.CompletedWithErrors ? 1 : 0, 1, Now);
        switch (status)
        {
            case ImportJobStatus.Completed:
            case ImportJobStatus.CompletedWithErrors:
                job.SetTotalRows((int)job.ProcessedRows, Now);
                job.Complete(Now);
                break;
            case ImportJobStatus.Failed:
                job.Fail("failure", "Failed", true, Now);
                break;
            case ImportJobStatus.Cancelled:
                job.RequestCancel();
                job.ConfirmCancel(Now);
                break;
            case ImportJobStatus.RetryScheduled:
                job.ScheduleRetry(Now, Now.AddSeconds(30));
                break;
        }

        return job;
    }

    private static void AssertCode(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainException>(action).Code);
}
