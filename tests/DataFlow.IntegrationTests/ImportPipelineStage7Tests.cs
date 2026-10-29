using System.Runtime.CompilerServices;
using System.Text;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Imports;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class ImportPipelineStage7Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    private const string Header = "externalId,name,email,registeredAt,isActive\n";
    private static readonly DataSourceSchema Schema = new(1, ["externalId"],
        [new("externalId", "string", true, 80), new("name", "string", true, 200),
         new("email", "email", true, 254), new("registeredAt", "date", false), new("isActive", "boolean", true)]);

    [Fact]
    public async Task StreamingBatches_KeepUnknownTotalUntilEof_AndCommitAllEffects()
    {
        var job = await SeedAsync();
        var messageId = Guid.NewGuid();
        var checkpoints = new List<long>();
        var parser = new ObservingParser(new CsvRowParser(Options.Create(new ImportProcessingOptions())), async index =>
        {
            if (index is not (501 or 1001)) { return; }
            await using var observed = fixture.CreateContext();
            var current = await observed.ImportJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Null(current.TotalRows);
            Assert.Equal(ImportJobStatus.Processing, current.Status);
            Assert.Equal(index - 1, current.ProcessedRows);
            Assert.Equal(index, current.LastCommittedRowNumber);
            Assert.Empty(await observed.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
            checkpoints.Add(current.LastCommittedRowNumber);
        });
        await RunAsync(job, messageId, Header + string.Concat(Enumerable.Range(1, 1001).Select(Line)), parser);
        await using var database = fixture.CreateContext();
        var completed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(new long[] { 501, 1001 }, checkpoints);
        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(1001, completed.TotalRows);
        Assert.Equal(1001, completed.InsertedRows);
        Assert.Equal(1002, completed.LastCommittedRowNumber);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseUntil);
        Assert.Equal(1001, await database.Customers.CountAsync(x => x.DataSourceId == job.DataSourceId));
        Assert.Equal(1001, await database.ImportRows.CountAsync(x => x.ImportJobId == job.Id));
        Assert.Single(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
        Assert.Single(await database.OutboxMessages.Where(x => x.Type == "import.job.finished").ToListAsync(),
            x => x.Payload.Contains(job.Id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_DifferentiatesInsertUpdateDuplicate_AndRejectsRepeatedFileKeys()
    {
        var first = await SeedAsync();
        await RunAsync(first, Guid.NewGuid(), Header + Line(1) + Line(2));
        await using var database = fixture.CreateContext();
        var original = await database.Customers.AsNoTracking().SingleAsync(x => x.DataSourceId == first.DataSourceId && x.ExternalId == "1");
        var second = await SeedAsync(first.DataSourceId);
        await RunAsync(second, Guid.NewGuid(), Header + Line(1) + Line(2).Replace("Customer", "Changed", StringComparison.Ordinal) +
            Line(3) + Line(3).Replace("Customer", "Repeated", StringComparison.Ordinal) + "4,,invalid,not-a-date,no\n" +
            "4,Valid,valid@example.test,,true\n");
        var job = await database.ImportJobs.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(ImportJobStatus.CompletedWithErrors, job.Status);
        Assert.Equal(6, job.TotalRows);
        Assert.Equal(1, job.InsertedRows);
        Assert.Equal(1, job.UpdatedRows);
        Assert.Equal(1, job.DuplicateRows);
        Assert.Equal(3, job.InvalidRows);
        var rows = await database.ImportRows.Where(x => x.ImportJobId == job.Id).OrderBy(x => x.RowNumber).ToListAsync();
        Assert.Equal(new[] { ImportRowStatus.Duplicate, ImportRowStatus.Updated, ImportRowStatus.Inserted,
            ImportRowStatus.Invalid, ImportRowStatus.Invalid, ImportRowStatus.Invalid }, rows.Select(x => x.Status));
        Assert.All(rows.Where(x => x.Status == ImportRowStatus.Invalid), row => Assert.NotNull(row.RawData));
        Assert.Contains(rows[3].GetErrors(), x => x.Code == "duplicate_key_in_file");
        Assert.Contains(rows[5].GetErrors(), x => x.Code == "duplicate_key_in_file");
        var customers = await database.Customers.AsNoTracking().Where(x => x.DataSourceId == first.DataSourceId).ToListAsync();
        Assert.Equal(3, customers.Count);
        Assert.Equal(original.UpdatedAt, customers.Single(x => x.ExternalId == "1").UpdatedAt);
        Assert.Equal("Changed 2", customers.Single(x => x.ExternalId == "2").Name);
    }

    [Fact]
    public async Task EmptyAndMultilineRecords_AdvancePhysicalCheckpointWithoutCountingBlanks()
    {
        var job = await SeedAsync();
        await RunAsync(job, Guid.NewGuid(), Header + "\n1,\"First\nSecond\",one@example.test,,true\n\n2,Two,two@example.test,,false\n\n");
        await using var database = fixture.CreateContext();
        var completed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(2, completed.TotalRows);
        Assert.Equal(7, completed.LastCommittedRowNumber);
        Assert.Equal(new[] { 3, 6 }, await database.ImportRows.Where(x => x.ImportJobId == job.Id).OrderBy(x => x.RowNumber).Select(x => x.RowNumber).ToArrayAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public async Task HeaderOnlyOrBlankData_CompletesWithZeroRows(string data)
    {
        var job = await SeedAsync();
        await RunAsync(job, Guid.NewGuid(), Header + data);
        await using var database = fixture.CreateContext();
        var completed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(0, completed.TotalRows);
        Assert.Equal(0, completed.ProcessedRows);
    }

    [Fact]
    public async Task DatabaseError_RollsBackEntireUnfinishedBatch_AndPersistsRetryableFailure()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_stage7_row() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.row_number = 503 THEN RAISE EXCEPTION 'injected batch failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_stage7_row BEFORE INSERT ON import_rows FOR EACH ROW EXECUTE FUNCTION reject_stage7_row();
            """);
        try
        {
            await RunAsync(job, Guid.NewGuid(), Header + string.Concat(Enumerable.Range(1, 600).Select(Line)));
            var failed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(ImportJobStatus.Failed, failed.Status);
            Assert.Equal("import_infrastructure_error", failed.LastErrorCode);
            Assert.Equal(1, failed.Retryable);
            Assert.Null(failed.TotalRows);
            Assert.Equal(500, failed.ProcessedRows);
            Assert.Equal(501, failed.LastCommittedRowNumber);
            Assert.Equal(500, await database.Customers.CountAsync(x => x.DataSourceId == job.DataSourceId));
            Assert.Equal(500, await database.ImportRows.CountAsync(x => x.ImportJobId == job.Id));
            Assert.Single(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_stage7_row ON import_rows; DROP FUNCTION reject_stage7_row()");
        }
    }

    [Fact]
    public async Task FinalizationOutboxFailure_RollsBackTerminalStatusAndInbox()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "owner", 60, default))!;
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_stage7_final() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.type = 'import.job.finished' THEN RAISE EXCEPTION 'injected finalization failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_stage7_final BEFORE INSERT ON outbox_messages FOR EACH ROW EXECUTE FUNCTION reject_stage7_final();
            """);
        var messageId = Guid.NewGuid();
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => store.FinalizeAsync(lease, messageId, 0, null, false, default));
            await using var observed = fixture.CreateContext();
            var unchanged = await observed.ImportJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(ImportJobStatus.Processing, unchanged.Status);
            Assert.Null(unchanged.TotalRows);
            Assert.Null(unchanged.CompletedAt);
            Assert.False(await observed.ProcessedMessages.AnyAsync(x => x.MessageId == messageId.ToString()));
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_stage7_final ON outbox_messages; DROP FUNCTION reject_stage7_final()");
        }
        await store.FinalizeAsync(lease, messageId, 0, null, false, default);
    }

    [Fact]
    public async Task ConcurrentAcquire_OnlyOneOwnerWins_AndRunVersionIsGuarded()
    {
        var job = await SeedAsync();
        await using var one = fixture.CreateContext();
        await using var two = fixture.CreateContext();
        var leases = await Task.WhenAll(new ImportJobStore(one).AcquireAsync(job.Id, 1, "one", 60, default),
            new ImportJobStore(two).AcquireAsync(job.Id, 1, "two", 60, default));
        Assert.Single(leases, x => x is not null);
        Assert.Null(await new ImportJobStore(two).AcquireAsync(job.Id, 2, "stale", 60, default));
        var stored = await one.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(1, stored.LeaseVersion);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("version")]
    [InlineData("run")]
    [InlineData("expired")]
    public async Task Fencing_RejectsStaleBatchAndFinalization(string variant)
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "owner", 60, default))!;
        lease = variant switch
        {
            "owner" => lease with { Owner = "other" },
            "version" => lease with { Version = lease.Version + 1 },
            "run" => lease with { RunVersion = 2 },
            _ => lease
        };
        if (variant == "expired")
        {
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
        }
        await Assert.ThrowsAsync<ImportLeaseUnavailableException>(() => store.CommitBatchAsync(lease, [BatchRow(job.DataSourceId, 2, "1")], 60, default));
        await Assert.ThrowsAsync<ImportLeaseUnavailableException>(() => store.FinalizeAsync(lease, Guid.NewGuid(), 0, null, false, default));
        await using var observed = fixture.CreateContext();
        Assert.False(await observed.Customers.AnyAsync(x => x.DataSourceId == job.DataSourceId));
        Assert.False(await observed.ImportRows.AnyAsync(x => x.ImportJobId == job.Id));
        Assert.False(await observed.ProcessedMessages.AnyAsync(x => x.JobId == job.Id));
        Assert.Equal(0, (await observed.ImportJobs.SingleAsync(x => x.Id == job.Id)).ProcessedRows);
    }

    [Fact]
    public async Task ExpiredLeaseTakeover_RejectsPreviousOwner_AndRenewsInBatchTransaction()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var old = (await store.AcquireAsync(job.Id, 1, "old", 60, default))!;
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
        var current = (await store.AcquireAsync(job.Id, 1, "new", 60, default))!;
        Assert.Equal(old.Version + 1, current.Version);
        await Assert.ThrowsAsync<ImportLeaseUnavailableException>(() => store.CommitBatchAsync(old, [BatchRow(job.DataSourceId, 2, "1")], 60, default));
        var before = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        await store.CommitBatchAsync(current, [BatchRow(job.DataSourceId, 2, "1")], 60, default);
        var after = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.True(after.LeaseUntil > before.LeaseUntil);
        Assert.Equal(current.Version, after.LeaseVersion);
        Assert.Equal(1, after.InsertedRows);
    }

    [Fact]
    public async Task RedeliveryAfterCommitBeforeAck_DoesNotReadFileOrRepeatEffects()
    {
        var job = await SeedAsync();
        var messageId = Guid.NewGuid();
        await RunAsync(job, messageId, Header + Line(1));
        await using var database = fixture.CreateContext();
        var processor = CreateProcessor(database, new FileReader(() => throw new InvalidOperationException("Must not read terminal job")));
        await processor.ProcessAsync(job.Id, job.RunVersion, messageId, "redelivery", default);
        await processor.ProcessAsync(job.Id, job.RunVersion, Guid.NewGuid(), "duplicate-publish", default);
        Assert.Single(await database.ImportRows.Where(x => x.ImportJobId == job.Id).ToListAsync());
        Assert.Single(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
        Assert.Equal(1, (await database.ImportJobs.SingleAsync(x => x.Id == job.Id)).AttemptCount);
    }

    [Fact]
    public async Task LeaseExpiresInsideBatch_RollsBackCustomerRowsCheckpointAndRenewal()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "worker", 60, default))!;
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET lease_until = clock_timestamp() + interval '1 second' WHERE id = {job.Id}");
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION delay_stage7_customer() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(1.2); RETURN NEW; END $$;
            CREATE TRIGGER delay_stage7_customer BEFORE INSERT ON customers FOR EACH ROW EXECUTE FUNCTION delay_stage7_customer();
            """);
        try
        {
            await Assert.ThrowsAsync<ImportLeaseUnavailableException>(() => store.CommitBatchAsync(lease, [BatchRow(job.DataSourceId, 2, "1")], 60, default));
            await using var observed = fixture.CreateContext();
            Assert.False(await observed.Customers.AnyAsync(x => x.DataSourceId == job.DataSourceId));
            Assert.False(await observed.ImportRows.AnyAsync(x => x.ImportJobId == job.Id));
            var unchanged = await observed.ImportJobs.SingleAsync(x => x.Id == job.Id);
            Assert.Equal(0, unchanged.ProcessedRows);
            Assert.Equal(0, unchanged.LastCommittedRowNumber);
        }
        finally
        {
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER delay_stage7_customer ON customers; DROP FUNCTION delay_stage7_customer()");
        }
    }

    [Fact]
    public async Task ConcurrentCustomerUpserts_AreAtomicAndReturnOneInsertOneDuplicate()
    {
        var first = await SeedAsync();
        var second = await SeedAsync(first.DataSourceId);
        await using var one = fixture.CreateContext();
        await using var two = fixture.CreateContext();
        var firstStore = new ImportJobStore(one);
        var secondStore = new ImportJobStore(two);
        var firstLease = (await firstStore.AcquireAsync(first.Id, 1, "one", 60, default))!;
        var secondLease = (await secondStore.AcquireAsync(second.Id, 1, "two", 60, default))!;
        await Task.WhenAll(firstStore.CommitBatchAsync(firstLease, [BatchRow(first.DataSourceId, 2, "1")], 60, default),
            secondStore.CommitBatchAsync(secondLease, [BatchRow(second.DataSourceId, 2, "1")], 60, default));
        await using var observed = fixture.CreateContext();
        Assert.Single(await observed.Customers.Where(x => x.DataSourceId == first.DataSourceId).ToListAsync());
        var results = await observed.ImportRows.Where(x => x.ImportJobId == first.Id || x.ImportJobId == second.Id).ToListAsync();
        Assert.Single(results, x => x.Status == ImportRowStatus.Inserted);
        Assert.Single(results, x => x.Status == ImportRowStatus.Duplicate);
    }

    [Fact]
    public async Task NotDueRetry_CannotAcquireLease()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        Assert.NotNull(await store.AcquireAsync(job.Id, 1, "worker", 60, default));
        var tracked = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        tracked.ScheduleRetry(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        await database.SaveChangesAsync();
        Assert.Null(await store.AcquireAsync(job.Id, 1, "early", 60, default));
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET next_attempt_at = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
        Assert.NotNull(await store.AcquireAsync(job.Id, 1, "due", 60, default));
    }

    [Fact]
    public async Task Resume_RestoresKeysIncludingInvalidRows_AndSkipsCommittedPhysicalPrefix()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "dead-worker", 60, default))!;
        var invalid = new ParsedRow(3, new Dictionary<string, string?> { ["externalId"] = "2" }, "{}");
        await store.CommitBatchAsync(lease, [BatchRow(job.DataSourceId, 2, "1"), new(invalid, new CustomerFileRowProcessor(job.DataSourceId, Schema).Process(invalid))], 60, default);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
        await RunAsync(job, Guid.NewGuid(), Header + Line(1) + "2,,,,\n" + Line(2) + Line(3));
        var completed = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(4, completed.TotalRows);
        Assert.Equal(2, completed.InsertedRows);
        Assert.Equal(2, completed.InvalidRows);
        Assert.Equal(5, completed.LastCommittedRowNumber);
        Assert.Equal(2, await database.Customers.CountAsync(x => x.DataSourceId == job.DataSourceId));
        var repeated = await database.ImportRows.SingleAsync(x => x.ImportJobId == job.Id && x.RowNumber == 4);
        Assert.Contains(repeated.GetErrors(), x => x.Code == "duplicate_key_in_file");
    }

    [Theory]
    [InlineData("unknown,name,email,registeredAt,isActive\n", "unknown_csv_header")]
    [InlineData("externalId,externalId\n", "duplicate_csv_header")]
    public async Task InvalidFile_IsDurablyFailedWithoutRetry(string csv, string expectedCode)
    {
        var job = await SeedAsync();
        await RunAsync(job, Guid.NewGuid(), csv);
        await using var database = fixture.CreateContext();
        var failed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ImportJobStatus.Failed, failed.Status);
        Assert.Equal(expectedCode, failed.LastErrorCode);
        Assert.Equal(0, failed.Retryable);
        Assert.Null(failed.TotalRows);
        Assert.Single(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
    }

    [Fact]
    public async Task StorageFailure_IsDurablyFailedWithSafeRetryableCode()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        await CreateProcessor(database, new FileReader(() => throw new IOException("secret@example.test")))
            .ProcessAsync(job.Id, 1, Guid.NewGuid(), "worker", default);
        var failed = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ImportJobStatus.Failed, failed.Status);
        Assert.Equal("import_infrastructure_error", failed.LastErrorCode);
        Assert.Equal(1, failed.Retryable);
        Assert.DoesNotContain("secret", failed.LastError);
        Assert.Single(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
    }

    [Fact]
    public async Task Cancellation_DoesNotPersistFailureOrAcknowledgeUnfinishedWork()
    {
        var job = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var reader = new FileReader(() => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); });
        await using var database = fixture.CreateContext();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateProcessor(database, reader)
            .ProcessAsync(job.Id, 1, Guid.NewGuid(), "worker", cancellation.Token));
        Assert.Empty(await database.ProcessedMessages.Where(x => x.JobId == job.Id).ToListAsync());
        Assert.Equal(ImportJobStatus.Processing, (await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status);
    }

    private async Task<ImportJob> SeedAsync(Guid? sourceId = null)
    {
        await using var database = fixture.CreateContext();
        if (sourceId is null)
        {
            var source = DataSource.Create("Pipeline", DataSourceDataType.Customer, AcceptedFileFormat.Csv, Schema, DateTimeOffset.UtcNow);
            database.DataSources.Add(source);
            sourceId = source.Id;
        }
        var job = ImportJob.Create(sourceId.Value, "test.csv", Guid.NewGuid().ToString("N"), new string('a', 64), Schema.ToJson(), 1, DateTimeOffset.UtcNow);
        database.ImportJobs.Add(job);
        await database.SaveChangesAsync();
        return job;
    }

    private async Task RunAsync(ImportJob job, Guid messageId, string csv, ICsvRowParser? parser = null)
    {
        await using var database = fixture.CreateContext();
        await CreateProcessor(database, new FileReader(() => new MemoryStream(Encoding.UTF8.GetBytes(csv))), parser)
            .ProcessAsync(job.Id, job.RunVersion, messageId, "worker", default);
    }

    private static CsvImportProcessor CreateProcessor(DataFlowDbContext database, IImportFileReader reader, ICsvRowParser? parser = null)
    {
        var options = Options.Create(new ImportProcessingOptions());
        return new(database, new ImportJobStore(database), reader, parser ?? new CsvRowParser(options), options, NullLogger<CsvImportProcessor>.Instance);
    }

    private static string Line(int number) => $"{number},Customer {number},customer{number}@example.test,2026-01-01,true\n";

    private static ImportBatchRow BatchRow(Guid sourceId, int rowNumber, string externalId)
    {
        var row = new ParsedRow(rowNumber, new Dictionary<string, string?>
        {
            ["externalId"] = externalId,
            ["name"] = "Customer",
            ["email"] = "customer@example.test",
            ["isActive"] = "true"
        }, "{}");
        return new(row, new CustomerFileRowProcessor(sourceId, Schema).Process(row));
    }

    private sealed class FileReader(Func<Stream> open) : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult(open());
    }

    private sealed class ObservingParser(ICsvRowParser inner, Func<int, Task> observe) : ICsvRowParser
    {
        public async IAsyncEnumerable<ParsedRow> ParseAsync(Stream stream, DataSourceSchema schema, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = 0;
            await foreach (var row in inner.ParseAsync(stream, schema, cancellationToken))
            {
                await observe(++index);
                yield return row;
            }
        }
    }
}
