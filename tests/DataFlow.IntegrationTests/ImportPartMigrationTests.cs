using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DataFlow.IntegrationTests;

public sealed class ImportPartMigrationTests
{
    private const string LastLegacyMigration = "20261113074104_ObservabilityTraceContext";

    [Fact]
    public async Task PopulatedLegacyDatabase_MigratesEveryStatusAndRepeatedBackfillHasNoEffects()
    {
        var container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await container.StartAsync();
        try
        {
            var options = new DbContextOptionsBuilder<DataFlowDbContext>()
                .UseNpgsql(container.GetConnectionString()).Options;
            await using var db = new DataFlowDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync(LastLegacyMigration);
            var seeded = await SeedLegacyDataAsync(db);

            await db.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();
            var jobs = await db.ImportJobs.AsNoTracking().ToDictionaryAsync(j => j.Id);
            Assert.Equal(seeded.Jobs.Length, jobs.Count);
            foreach (var expected in seeded.Jobs)
            {
                var job = jobs[expected.Id];
                var part = Assert.Single(job.Parts);
                Assert.Equal(expected.PartStatus, part.Status);
                Assert.Equal(0, part.PartIndex);
                Assert.Equal(job.LastCommittedRowNumber, part.LastCommittedPhysicalRowNumber);
                Assert.Equal(job.TotalRows, part.TotalRows);
                Assert.Equal(job.ProcessedRows, part.ProcessedRows);
                Assert.Equal(job.ValidRows, part.ValidRows);
                Assert.Equal(job.InsertedRows, part.InsertedRows);
                Assert.Equal(job.UpdatedRows, part.UpdatedRows);
                Assert.Equal(job.InvalidRows, part.InvalidRows);
                Assert.Equal(job.DuplicateRows, part.DuplicateRows);
                Assert.Equal(job.StartedAt, part.StartedAt);
                Assert.Equal(job.CompletedAt, part.CompletedAt);
                Assert.Equal(job.LastErrorCode, part.LastErrorCode);
                Assert.Null(part.LastErrorRowNumber);
                Assert.Equal(job.CreatedAt, part.CreatedAt);
                Assert.Equal(job.UpdatedAt, part.UpdatedAt);
                Assert.Equal(expected.SourceId == seeded.TabularSourceId ? "part:0" : "customer:job", part.DeduplicationScope);
                if (expected.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(ImportPartType.XlsxWorksheet, part.PartType);
                    Assert.Equal("xlsx:first-sheet", part.SourceLocator);
                }
                else
                {
                    Assert.Equal(ImportPartType.Csv, part.PartType);
                    Assert.Equal("file:single-file", part.SourceLocator);
                }
                Assert.Null(part.SheetName);
                Assert.Null(part.SheetIndex);
                if (expected.Pinned)
                {
                    Assert.Equal(seeded.VersionId, part.RuleSetVersionId);
                    Assert.Equal(seeded.DocumentId, part.RuleDocumentId);
                    Assert.Equal(seeded.DocumentHash, part.RuleSnapshotHash);
                    Assert.Equal("Pinned legacy rules", part.RuleSetNameAtSubmission);
                    Assert.Equal(1, part.RuleVersionNumberAtSubmission);
                }
                else
                {
                    Assert.Null(part.RuleSetVersionId);
                    Assert.Null(part.RuleDocumentId);
                    Assert.Null(part.RuleSnapshotHash);
                    Assert.Null(part.RuleSetNameAtSubmission);
                    Assert.Null(part.RuleVersionNumberAtSubmission);
                }
            }

            var rows = await db.ImportRows.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.Equal(jobs[row.ImportJobId].Parts[0].Id, row.ImportPartId));
            var invalid = Assert.Single(rows, r => r.Status == ImportRowStatus.Invalid);
            Assert.Equal("reserved-key", invalid.NaturalKey);
            Assert.Equal(new string('d', 64), invalid.RowHash);
            Assert.Equal("bad_email", Assert.Single(invalid.GetErrors()).Code);
            var partIds = jobs.ToDictionary(pair => pair.Key, pair => pair.Value.Parts[0].Id);
            var rowLinks = rows.ToDictionary(row => row.Id, row => row.ImportPartId);

            await db.Database.ExecuteSqlRawAsync(ImportPartBackfill.Sql);
            db.ChangeTracker.Clear();
            var repeatedJobs = await db.ImportJobs.AsNoTracking().ToDictionaryAsync(j => j.Id);
            Assert.All(repeatedJobs, pair => Assert.Equal(partIds[pair.Key], Assert.Single(pair.Value.Parts).Id));
            Assert.Equal(rowLinks, await db.ImportRows.AsNoTracking().ToDictionaryAsync(row => row.Id, row => row.ImportPartId));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            await AssertEnforcedSchemaAsync(db);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    private static async Task<SeededData> SeedLegacyDataAsync(DataFlowDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var ownerId = Guid.NewGuid();
        const string documentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var owner = new ApplicationUser { Id = ownerId, UserName = "migration@test" };
        var document = new RuleDocument(documentHash, "{}", "legacy-customer-v1");
        var set = new RuleSet(ownerId, "Pinned legacy rules", null, "Customer");
        set.RegisterHistoricalVersion(1);
        var version = new RuleSetVersion(set.Id, 1, document.Id, null, null, null);
        var documentId = document.Id;
        var versionId = version.Id;
        db.AddRange(owner, document, set, version);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var customerSourceId = Guid.NewGuid();
        var tabularSourceId = Guid.NewGuid();
        await InsertSourceAsync(db, customerSourceId, 1, versionId, now);
        await InsertSourceAsync(db, tabularSourceId, 2, null, now);
        var jobs = new[]
        {
            new LegacyJob(Guid.NewGuid(), customerSourceId, "initial.csv", ImportJobStatus.Queued, ImportPartStatus.Pending, 0, 0),
            new LegacyJob(Guid.NewGuid(), tabularSourceId, "manual.csv", ImportJobStatus.Queued, ImportPartStatus.Processing, 1, 0),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "processing.csv", ImportJobStatus.Processing, ImportPartStatus.Processing, 1, 0,
                Processed: 1, Inserted: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-8)),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "retry.csv", ImportJobStatus.RetryScheduled, ImportPartStatus.Processing, 1, 0,
                Processed: 1, Valid: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-7), NextAttemptAt: now.AddMinutes(5)),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "completed.xlsx", ImportJobStatus.Completed, ImportPartStatus.Completed, 1, 0,
                Processed: 1, Inserted: 1, TotalRows: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-6), CompletedAt: now.AddMinutes(-5), Pinned: true),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "errors.csv", ImportJobStatus.CompletedWithErrors, ImportPartStatus.CompletedWithErrors, 1, 0,
                Processed: 1, Invalid: 1, TotalRows: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-5), CompletedAt: now.AddMinutes(-4)),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "exhausted.csv", ImportJobStatus.Failed, ImportPartStatus.Processing, 4, 1,
                Processed: 1, Inserted: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-4), CompletedAt: now.AddMinutes(-3), ErrorCode: "import_retry_exhausted"),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "invalid.csv", ImportJobStatus.Failed, ImportPartStatus.Failed, 1, 0,
                StartedAt: now.AddMinutes(-3), CompletedAt: now.AddMinutes(-2), ErrorCode: "invalid_csv"),
            new LegacyJob(Guid.NewGuid(), customerSourceId, "cancelled.csv", ImportJobStatus.Cancelled, ImportPartStatus.Cancelled, 1, 0,
                Processed: 1, Inserted: 1, Checkpoint: 2, StartedAt: now.AddMinutes(-2), CompletedAt: now.AddMinutes(-1), CancelRequested: true)
        };
        foreach (var job in jobs)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO import_jobs (
                    id, data_source_id, original_file_name, storage_key, file_sha256, schema_json, schema_version,
                    status, total_rows, processed_rows, inserted_rows, updated_rows, invalid_rows, duplicate_rows, valid_rows,
                    last_committed_row_number, started_at, completed_at, last_error, last_error_code, retryable, attempt_count,
                    lease_owner, lease_until, lease_version, cancel_requested, created_at, updated_at, run_version, next_attempt_at,
                    rule_set_version_id, rule_document_id, execution_fingerprint, rule_set_name_at_submission, rule_version_number_at_submission)
                VALUES (
                    {job.Id}, {job.SourceId}, {job.FileName}, {"legacy/" + job.Id}, {new string('b', 64)}, CAST({"{}"} AS jsonb), 1,
                    {(int)job.Status}, {job.TotalRows}, {job.Processed}, {job.Inserted}, {job.Updated}, {job.Invalid}, {job.Duplicate}, {job.Valid},
                    {job.Checkpoint}, {job.StartedAt}, {job.CompletedAt}, {(job.ErrorCode == null ? null : "Legacy failure")}, {job.ErrorCode}, {job.Retryable}, {job.AttemptCount},
                    {null}, {null}, {job.AttemptCount}, {job.CancelRequested}, {now.AddMinutes(-10)}, {now}, 1, {job.NextAttemptAt},
                    {(job.Pinned ? versionId : null)}, {(job.Pinned ? documentId : null)}, {(job.Pinned ? new string('c', 64) : null)},
                    {(job.Pinned ? "Pinned legacy rules" : null)}, {(job.Pinned ? 1 : null)})
                """);
        }

        await InsertRowAsync(db, Guid.NewGuid(), jobs[2].Id, ImportRowStatus.Inserted, "CaseSensitive", new string('c', 64), now);
        await InsertRowAsync(db, Guid.NewGuid(), jobs[3].Id, ImportRowStatus.Valid, "CaseSensitive", new string('e', 64), now);
        await InsertRowAsync(db, Guid.NewGuid(), jobs[5].Id, ImportRowStatus.Invalid, "reserved-key", new string('d', 64), now);
        return new(jobs, tabularSourceId, versionId, documentId, documentHash);
    }

    private static Task<int> InsertSourceAsync(DataFlowDbContext db, Guid id, int dataType, Guid? defaultVersionId, DateTimeOffset now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO data_sources (id, name, data_type, accepted_formats, schema_version, schema_json, is_active,
                owner_user_id, default_rule_set_version_id, created_at, updated_at)
            VALUES ({id}, {"Legacy " + id}, {dataType}, 3, 1, CAST({"{}"} AS jsonb), true, null, {defaultVersionId}, {now}, {now})
            """);

    private static Task<int> InsertRowAsync(DataFlowDbContext db, Guid id, Guid jobId, ImportRowStatus status,
        string naturalKey, string rowHash, DateTimeOffset now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO import_rows (id, import_job_id, row_number, status, raw_data, errors_json, natural_key, row_hash, created_at, processed_at)
            VALUES ({id}, {jobId}, 2, {(int)status},
                CAST({(status == ImportRowStatus.Invalid ? "{\"email\":\"bad\"}" : null)} AS jsonb),
                CAST({(status == ImportRowStatus.Invalid ? "[{\"field\":\"email\",\"code\":\"bad_email\",\"message\":\"Invalid email\"}]" : null)} AS jsonb),
                {naturalKey}, {rowHash}, {now}, {now})
            """);

    private static async Task AssertEnforcedSchemaAsync(DataFlowDbContext db)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT is_nullable FROM information_schema.columns
                 WHERE table_name = 'import_rows' AND column_name = 'import_part_id'),
                (SELECT count(*) FROM pg_constraint WHERE conname = 'fk_import_rows_import_part'),
                (SELECT count(*) FROM pg_indexes WHERE indexname = 'ux_import_rows_part_row_number'),
                (SELECT count(*) FROM pg_indexes WHERE indexname IN ('ux_import_rows_job_row_number', 'ix_import_rows_job_natural_key')),
                (SELECT collation_name FROM information_schema.columns WHERE table_name = 'import_rows' AND column_name = 'natural_key'),
                (SELECT collation_name FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'natural_key')
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("NO", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal("C", reader.GetString(4));
        Assert.Equal("C", reader.GetString(5));
    }

    private sealed record LegacyJob(Guid Id, Guid SourceId, string FileName, ImportJobStatus Status,
        ImportPartStatus PartStatus, int AttemptCount, int Retryable, int Processed = 0, int Valid = 0,
        int Inserted = 0, int Updated = 0, int Invalid = 0, int Duplicate = 0, int? TotalRows = null,
        long Checkpoint = 0, DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null,
        DateTimeOffset? NextAttemptAt = null, string? ErrorCode = null, bool CancelRequested = false, bool Pinned = false);

    private sealed record SeededData(LegacyJob[] Jobs, Guid TabularSourceId, Guid VersionId, Guid DocumentId, string DocumentHash);
}
