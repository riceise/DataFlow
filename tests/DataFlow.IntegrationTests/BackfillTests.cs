using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Migrations;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class BackfillTests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.UserRuleLibraryEntries.ExecuteDeleteAsync();
        await db.RuleVersionGrants.ExecuteDeleteAsync();
        await db.RuleVersionPublications.ExecuteDeleteAsync();
        await db.UploadRequests.ExecuteDeleteAsync();
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.ProcessedMessages.ExecuteDeleteAsync();
        await db.ImportRows.ExecuteDeleteAsync();
        await db.ImportParts.ExecuteDeleteAsync();
        await db.ImportJobs.ExecuteDeleteAsync();
        await db.Customers.ExecuteDeleteAsync();
        await db.DataSources.ExecuteDeleteAsync();
        await db.RuleSetVersions.ExecuteDeleteAsync();
        await db.RuleSets.ExecuteDeleteAsync();
        await db.RuleDocuments.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BackfillCreatesRuleSetsFromLegacySchemas()
    {
        await using var db = fixture.CreateContext();

        // Create legacy source and jobs with different schema versions
        var userId = Guid.NewGuid();
        var user = new DataFlow.Infrastructure.Persistence.ApplicationUser { Id = userId, UserName = "backfill@test", Email = "backfill@test", EmailConfirmed = true };
        user.PasswordHash = "dummy"; // BackfillCommand doesn't validate password
        db.Users.Add(user);

        var legacySource = DataFlow.Domain.Entities.DataSource.Create(
            "Legacy Source",
            DataFlow.Domain.Enums.DataSourceDataType.Customer,
            DataFlow.Domain.Enums.AcceptedFileFormat.Csv,
            LegacySchema(1),
            DateTimeOffset.UtcNow);

        db.DataSources.Add(legacySource);
        await db.SaveChangesAsync();

        var job1 = DataFlow.Domain.Entities.ImportJob.Create(
            legacySource.Id, "file1.csv", "key1", new string('a', 64), LegacySchema(1).ToJson(), 1, DateTimeOffset.UtcNow);
        var job2 = DataFlow.Domain.Entities.ImportJob.Create(
            legacySource.Id, "file2.csv", "key2", new string('b', 64), LegacySchema(2).ToJson(), 2, DateTimeOffset.UtcNow);

        db.ImportJobs.AddRange(job1, job2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Execute backfill
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        var result = await command.ExecuteAsync(userId, CancellationToken.None);

        Assert.Equal(1, result.SetsCreated);
        Assert.Equal(2, result.VersionsCreated);
        Assert.Equal(2, result.JobsLinked);
        Assert.Equal(2, result.DocumentsCreated);

        // Verify source is now owned and linked
        db.ChangeTracker.Clear();
        var source = await db.DataSources.SingleAsync(s => s.Id == legacySource.Id);
        Assert.Equal(userId, source.OwnerUserId);
        Assert.NotNull(source.DefaultRuleSetVersionId);

        // Verify rule set and versions exist
        var set = await db.RuleSets.SingleAsync(s => s.OwnerUserId == userId);
        Assert.Contains("Legacy rules", set.Name);
        Assert.Equal(2, set.LastVersionNumber);

        var versions = await db.RuleSetVersions.Where(v => v.RuleSetId == set.Id).OrderBy(v => v.VersionNumber).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(2, versions[1].VersionNumber);

        // Verify jobs are linked to documents
        db.ChangeTracker.Clear();
        var jobs = await db.ImportJobs.Where(j => j.DataSourceId == legacySource.Id).OrderBy(j => j.SchemaVersion).ToListAsync();
        Assert.All(jobs, j => Assert.NotNull(j.RuleDocumentId));
        Assert.All(jobs, j => Assert.NotNull(j.RuleSetVersionId));
        Assert.All(jobs, j => Assert.False(string.IsNullOrEmpty(j.ExecutionFingerprint)));
        Assert.Equal(versions[0].Id, jobs[0].RuleSetVersionId);
        Assert.Equal(versions[1].Id, jobs[1].RuleSetVersionId);

        // Verify documents are shared for identical schemas
        var doc1 = await db.RuleDocuments.SingleAsync(d => d.Id == jobs[0].RuleDocumentId!.Value);
        var doc2 = await db.RuleDocuments.SingleAsync(d => d.Id == jobs[1].RuleDocumentId!.Value);
        Assert.Equal("legacy-customer-v1", doc1.EngineVersion);
        Assert.NotEqual(doc1.Id, doc2.Id); // Different schemas
    }

    [Fact]
    public async Task BackfillMigratesSourceSchemaWithoutJobsAndPinsCompletedJobs()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new DataFlow.Infrastructure.Persistence.ApplicationUser { Id = userId, UserName = "drift@test", Email = "drift@test", EmailConfirmed = true });

        // Source schema drifted ahead of any job: version 3 has never been used by a job.
        var source = DataFlow.Domain.Entities.DataSource.Create(
            "Drifted Source", DataFlow.Domain.Enums.DataSourceDataType.Customer,
            DataFlow.Domain.Enums.AcceptedFileFormat.Csv, LegacySchema(1), DateTimeOffset.UtcNow);
        // Two distinct schema changes move the source to version 3.
        source.Update("Drifted Source", DataFlow.Domain.Enums.AcceptedFileFormat.Csv, LegacySchema(2), DateTimeOffset.UtcNow);
        source.Update("Drifted Source", DataFlow.Domain.Enums.AcceptedFileFormat.Csv, LegacySchema(3), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        // A completed job with its own older schema snapshot must keep its own document.
        var completed = DataFlow.Domain.Entities.ImportJob.Create(
            source.Id, "done.csv", "key-done", new string('c', 64), LegacySchema(1).ToJson(), 1, DateTimeOffset.UtcNow);
        completed.AcquireLease("worker", DateTimeOffset.UtcNow, 60);
        completed.SetTotalRows(1, DateTimeOffset.UtcNow);
        completed.RecordBatchCompletion(2, 1, 0, 0, 0, DateTimeOffset.UtcNow);
        completed.Complete(DateTimeOffset.UtcNow);
        db.ImportJobs.Add(completed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        var result = await command.ExecuteAsync(userId, CancellationToken.None);

        Assert.Equal(1, result.SetsCreated);
        // Source version 3 plus the job snapshot version 1: two distinct rule versions.
        Assert.Equal(2, result.VersionsCreated);
        Assert.Equal(1, result.JobsLinked);

        db.ChangeTracker.Clear();
        var job = await db.ImportJobs.SingleAsync(j => j.Id == completed.Id);
        Assert.NotNull(job.RuleDocumentId);
        Assert.Equal(1, job.ProcessedRows); // Completed job keeps its progress.
        var jobDocument = await db.RuleDocuments.SingleAsync(d => d.Id == job.RuleDocumentId!.Value);
        Assert.Equal("legacy-customer-v1", jobDocument.EngineVersion);

        var source2 = await db.DataSources.SingleAsync(s => s.Id == source.Id);
        Assert.Equal(userId, source2.OwnerUserId);
        Assert.NotNull(source2.DefaultRuleSetVersionId);
        Assert.Equal(3, source2.SchemaVersion);
        // The source points to a version for its current schema, not to the job's version.
        var version = await db.RuleSetVersions.SingleAsync(v => v.Id == source2.DefaultRuleSetVersionId);
        Assert.Equal(3, version.VersionNumber); // Historical numbering is preserved, including gaps.
        var versionDocument = await db.RuleDocuments.SingleAsync(d => d.Id == version.RuleDocumentId);
        Assert.NotEqual(jobDocument.Id, versionDocument.Id);
    }

    [Fact]
    public async Task BackfillRejectsConflictingDefinitionsForSameVersion()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new DataFlow.Infrastructure.Persistence.ApplicationUser { Id = userId, UserName = "conflict@test", Email = "conflict@test", EmailConfirmed = true });

        var source = DataFlow.Domain.Entities.DataSource.Create(
            "Conflicted", DataFlow.Domain.Enums.DataSourceDataType.Customer,
            DataFlow.Domain.Enums.AcceptedFileFormat.Csv, LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        // Two jobs claim the same schema version number with different definitions.
        var job1 = DataFlow.Domain.Entities.ImportJob.Create(
            source.Id, "one.csv", "key-1", new string('a', 64), LegacySchema(1).ToJson(), 1, DateTimeOffset.UtcNow);
        var job2 = DataFlow.Domain.Entities.ImportJob.Create(
            source.Id, "two.csv", "key-2", new string('b', 64), LegacySchema(5).ToJson(), 1, DateTimeOffset.UtcNow);
        db.ImportJobs.AddRange(job1, job2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var goodSource = DataSource.Create("Unaffected", DataSourceDataType.Customer,
            AcceptedFileFormat.Csv, LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(goodSource);
        await db.SaveChangesAsync();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(userId, CancellationToken.None));
        // The conflicting source stays unowned: the operator resolves it and reruns.
        db.ChangeTracker.Clear();
        var conflicted = await db.DataSources.SingleAsync(s => s.Name == "Conflicted");
        Assert.Null(conflicted.OwnerUserId);
        Assert.Equal(1, await db.RuleSets.CountAsync(s => s.OwnerUserId == userId));
        Assert.Equal(userId, (await db.DataSources.SingleAsync(s => s.Id == goodSource.Id)).OwnerUserId);
    }

    [Fact]
    public async Task BackfillIsIdempotent()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        var user = new DataFlow.Infrastructure.Persistence.ApplicationUser { Id = userId, UserName = "idempotent@test", Email = "idempotent@test", EmailConfirmed = true };
        user.PasswordHash = "dummy";
        db.Users.Add(user);

        var source = DataFlow.Domain.Entities.DataSource.Create(
            "Test Source", DataFlow.Domain.Enums.DataSourceDataType.Customer,
            DataFlow.Domain.Enums.AcceptedFileFormat.Csv, LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        var first = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(1, first.SetsCreated);

        db.ChangeTracker.Clear();
        var second = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(0, second.SetsCreated);
        Assert.Equal(0, second.VersionsCreated);
    }

    private static DataFlow.Domain.ValueObjects.DataSourceSchema LegacySchema(int seed)
    {
        return new DataFlow.Domain.ValueObjects.DataSourceSchema(
            seed,
            ["externalId"],
            [
                new("externalId", "string", true, 80),
                new("name", "string", true, 200 - seed),
                new("email", "email", true, 254),
                new("isActive", "boolean", true)
            ],
            ",");
    }

    [Fact]
    public async Task BackfillDeduplicatesEquivalentSnapshotsAndKeepsExistingOwner()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "dedup@test" });
        var source = DataSource.Create("Same rules", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        source.AssignOwner(userId);
        db.DataSources.Add(source);
        // Same executable definition, different revision and JSON whitespace/order after JSONB round trip.
        db.ImportJobs.Add(ImportJob.Create(source.Id, "a.csv", "dedup", new string('a', 64),
            LegacySchema(1).WithVersion(5).ToJson(), 5, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        var result = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(2, result.VersionsCreated);
        Assert.Equal(1, result.DocumentsCreated);
        Assert.Equal(1, await db.RuleDocuments.CountAsync());
        Assert.Equal(new[] { 1, 5 }, await db.RuleSetVersions.OrderBy(v => v.VersionNumber).Select(v => v.VersionNumber).ToArrayAsync());
        Assert.Equal(0, (await command.ExecuteAsync(userId, CancellationToken.None)).SetsCreated);
    }

    [Fact]
    public async Task BackfilledProcessingJobResumesWithItsOwnSnapshot()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "resume@test" });
        var source = DataSource.Create("Resume", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        var job = ImportJob.Create(source.Id, "resume.csv", "resume", new string('a', 64), source.SchemaJson, 1, DateTimeOffset.UtcNow);
        var oldTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        job.AcquireLease("crashed", oldTime, 60);
        job.RecordBatchCompletion(2, 0, 0, 1, 0, oldTime);
        db.ImportJobs.Add(job);
        db.ImportRows.Add(ImportRow.Create(job.Id, job.Parts[0].Id, 2, ImportRowStatus.Invalid, "[]", "committed-key",
            [new ImportRowError("email", "invalid_email", "A valid email is required.")], oldTime));
        await db.SaveChangesAsync();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);
        var options = Options.Create(new ImportProcessingOptions());
        var reader = new LegacyReader("externalId,name,email,isActive\nfirst,First,invalid,true\nsecond,Second,second@example.com,true\n");
        var processor = new CsvImportProcessor(db, new ImportJobStore(db), reader, new CsvRowParser(options), options,
            NullLogger<CsvImportProcessor>.Instance);
        await processor.ProcessAsync(job.Id, 1, Guid.NewGuid(), "replacement", CancellationToken.None);
        db.ChangeTracker.Clear();
        var resumed = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(ImportJobStatus.CompletedWithErrors, resumed.Status);
        Assert.Equal(2, resumed.ProcessedRows);
        Assert.Equal(1, resumed.InvalidRows);
        Assert.Equal(1, resumed.InsertedRows);
        Assert.Equal(3, resumed.LastCommittedRowNumber);
        Assert.Equal(2, await db.ImportRows.CountAsync());
        Assert.Single(await db.Customers.ToArrayAsync());
    }

    [Fact]
    public async Task BackfillRestoresEitherMissingSideWithoutOverwritingPinnedReferences()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "partial@test" });
        var source = DataSource.Create("Partial", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        var job = ImportJob.Create(source.Id, "partial.csv", "partial", new string('a', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.AddRange(source, job);
        await db.SaveChangesAsync();
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);
        db.ChangeTracker.Clear();

        var original = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        var expected = new ExpectedPins(original.RuleSetVersionId!.Value, original.RuleDocumentId!.Value,
            original.ExecutionFingerprint, original.RuleSetNameAtSubmission, original.RuleVersionNumberAtSubmission!.Value,
            original.Parts[0].RuleSnapshotHash);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE import_parts SET rule_set_version_id = NULL, rule_document_id = NULL, rule_snapshot_hash = NULL,
                rule_set_name_at_submission = NULL, rule_version_number_at_submission = NULL
            WHERE id = {original.Parts[0].Id}
            """);
        db.ChangeTracker.Clear();
        var partResult = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(1, partResult.JobsLinked);
        Assert.Equal(0, partResult.SetsCreated);
        Assert.Equal(0, partResult.VersionsCreated);
        Assert.Equal(0, partResult.DocumentsCreated);
        await AssertRestoredAsync(db, job.Id, expected);

        var staleFingerprint = ExecutionFingerprint(userId, source.Id, job.FileSha256, null);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE import_jobs SET rule_set_version_id = NULL, rule_document_id = NULL, execution_fingerprint = {staleFingerprint},
                rule_set_name_at_submission = NULL, rule_version_number_at_submission = NULL, schema_version = 2
            WHERE id = {job.Id}
            """);
        db.ChangeTracker.Clear();
        var jobResult = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(1, jobResult.JobsLinked);
        await AssertRestoredAsync(db, job.Id, expected);
        Assert.Equal(0, (await command.ExecuteAsync(userId, CancellationToken.None)).JobsLinked);
    }

    [Fact]
    public async Task BackfillUsesExistingDefaultVersionForUnpinnedJobWithoutCreatingDuplicates()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "existing-default@test" });
        var source = DataSource.Create("Existing default", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);
        var defaultVersionId = (await db.DataSources.SingleAsync(s => s.Id == source.Id)).DefaultRuleSetVersionId;
        var job = ImportJob.Create(source.Id, "late-legacy.csv", "late", new string('b', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(1, result.JobsLinked);
        Assert.Equal(0, result.SetsCreated);
        Assert.Equal(0, result.VersionsCreated);
        Assert.Equal(0, result.DocumentsCreated);
        db.ChangeTracker.Clear();
        var migrated = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(defaultVersionId, migrated.RuleSetVersionId);
        Assert.Equal(defaultVersionId, migrated.Parts[0].RuleSetVersionId);
        Assert.Equal(migrated.RuleDocumentId, migrated.Parts[0].RuleDocumentId);
        Assert.Equal(1, await db.RuleSets.CountAsync());
        Assert.Equal(1, await db.RuleSetVersions.CountAsync());
        Assert.Equal(1, await db.RuleDocuments.CountAsync());
    }

    [Fact]
    public async Task BackfillConflictRollsBackEveryJobOfTheSource()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "reference-conflict@test" });
        var source = DataSource.Create("Reference conflict", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        var pinned = ImportJob.Create(source.Id, "pinned.csv", "pinned", new string('a', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.AddRange(source, pinned);
        await db.SaveChangesAsync();
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);

        var unpinned = ImportJob.Create(source.Id, "unpinned.csv", "unpinned", new string('b', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.ImportJobs.Add(unpinned);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE import_parts SET rule_snapshot_hash = {new string('x', 64)}, rule_set_name_at_submission = NULL
            WHERE import_job_id = {pinned.Id}
            """);
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(userId, CancellationToken.None));
        db.ChangeTracker.Clear();
        var untouched = await db.ImportJobs.SingleAsync(j => j.Id == unpinned.Id);
        Assert.Null(untouched.RuleSetVersionId);
        Assert.Null(untouched.RuleDocumentId);
        Assert.Null(untouched.Parts[0].RuleSetVersionId);
        Assert.Null(untouched.Parts[0].RuleDocumentId);
        Assert.Null(untouched.Parts[0].RuleSnapshotHash);
        Assert.Equal(1, await db.RuleSets.CountAsync());
    }

    [Fact]
    public async Task BackfillTreatsUnavailableHistoricalNumberAsSourceConflict()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "historical-gap@test" });
        var source = DataSource.Create("Historical gap", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        for (var version = 2; version <= 5; version++)
        {
            source.Update("Historical gap", AcceptedFileFormat.Csv, LegacySchema(version), DateTimeOffset.UtcNow);
        }
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);

        var lateJob = ImportJob.Create(source.Id, "older.csv", "older", new string('b', 64),
            LegacySchema(1).ToJson(), 1, DateTimeOffset.UtcNow);
        db.ImportJobs.Add(lateJob);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(userId, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(5, (await db.RuleSets.SingleAsync()).LastVersionNumber);
        Assert.Equal(5, (await db.RuleSetVersions.SingleAsync()).VersionNumber);
        Assert.Equal(1, await db.RuleDocuments.CountAsync());
        var untouched = await db.ImportJobs.SingleAsync(j => j.Id == lateJob.Id);
        Assert.Null(untouched.RuleSetVersionId);
        Assert.Null(Assert.Single(untouched.Parts).RuleSetVersionId);
    }

    [Fact]
    public async Task BackfillDoesNotAppendVersionsToForeignDefaultSet()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        var foreignOwnerId = Guid.NewGuid();
        db.Users.AddRange(new ApplicationUser { Id = userId, UserName = "recipient@test" },
            new ApplicationUser { Id = foreignOwnerId, UserName = "foreign-owner@test" });
        var foreignSet = new RuleSet(foreignOwnerId, "Shared rules", null, "Customer");
        foreignSet.RegisterHistoricalVersion(5);
        var document = new RuleDocument(new string('d', 64), "{}", "legacy-customer-v1");
        var foreignVersion = new RuleSetVersion(foreignSet.Id, 5, document.Id, null, null, null);
        var source = DataSource.Create("Shared default", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        source.AssignOwner(userId);
        source.SelectRules(foreignVersion.Id, DataSourceDataType.Customer);
        var job = ImportJob.Create(source.Id, "legacy.csv", "legacy", new string('e', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.AddRange(foreignSet, document, foreignVersion, source, job);
        await db.SaveChangesAsync();
        var revision = foreignSet.Revision;
        db.ChangeTracker.Clear();

        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(userId, CancellationToken.None));
        db.ChangeTracker.Clear();
        var persistedSet = await db.RuleSets.SingleAsync();
        Assert.Equal(foreignOwnerId, persistedSet.OwnerUserId);
        Assert.Equal(revision, persistedSet.Revision);
        Assert.Equal(5, persistedSet.LastVersionNumber);
        Assert.Equal(foreignVersion.Id, (await db.RuleSetVersions.SingleAsync()).Id);
        Assert.Equal(document.Id, (await db.RuleDocuments.SingleAsync()).Id);
        var untouched = await db.ImportJobs.SingleAsync();
        Assert.Null(untouched.RuleSetVersionId);
        Assert.Null(Assert.Single(untouched.Parts).RuleSetVersionId);
    }

    [Fact]
    public async Task BackfillExcludesMultiPartJobsFromSelectionAndSchemaMigration()
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "multi-part@test" });
        var source = DataSource.Create("Multi-part exclusion", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        var command = new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance);
        await command.ExecuteAsync(userId, CancellationToken.None);

        var multi = ImportJob.Create(source.Id, "multi.zip", "multi", new string('c', 64),
            LegacySchema(5).ToJson(), 1, DateTimeOffset.UtcNow);
        db.ImportJobs.Add(multi);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO import_parts (
                id, import_job_id, part_index, part_type, source_name, source_locator,
                status, last_committed_physical_row_number, processed_rows, valid_rows,
                inserted_rows, updated_rows, invalid_rows, duplicate_rows,
                deduplication_scope, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {multi.Id}, 1, 1, 'second.csv', 'zip:second.csv',
                1, 0, 0, 0, 0, 0, 0, 0, 'customer:job', {now}, {now})
            """);
        db.ChangeTracker.Clear();

        var result = await command.ExecuteAsync(userId, CancellationToken.None);
        Assert.Equal(0, result.JobsLinked);
        Assert.Equal(0, result.SetsCreated);
        Assert.Equal(0, result.VersionsCreated);
        db.ChangeTracker.Clear();
        var persisted = await db.ImportJobs.SingleAsync(j => j.Id == multi.Id);
        Assert.Equal(2, persisted.Parts.Count);
        Assert.Null(persisted.RuleSetVersionId);
        Assert.All(persisted.Parts, p => Assert.Null(p.RuleSetVersionId));
        Assert.All(persisted.Parts, p => Assert.Null(p.RuleDocumentId));
        Assert.Equal(0, (await command.ExecuteAsync(userId, CancellationToken.None)).JobsLinked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyWorker_UsesPartThenJobDocumentFallback(bool clearPart)
    {
        await using var db = fixture.CreateContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"fallback-{clearPart}@test" });
        var source = DataSource.Create("Fallback", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            LegacySchema(1), DateTimeOffset.UtcNow);
        var job = ImportJob.Create(source.Id, "fallback.csv", "fallback", new string('a', 64),
            source.SchemaJson, 1, DateTimeOffset.UtcNow);
        db.AddRange(source, job);
        await db.SaveChangesAsync();
        await new BackfillRulesCommand(db, NullLogger<BackfillRulesCommand>.Instance).ExecuteAsync(userId, CancellationToken.None);
        if (clearPart)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE import_parts SET rule_set_version_id = NULL, rule_document_id = NULL, rule_snapshot_hash = NULL,
                    rule_set_name_at_submission = NULL, rule_version_number_at_submission = NULL
                WHERE import_job_id = {job.Id}
                """);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE import_jobs SET rule_set_version_id = NULL, rule_document_id = NULL, execution_fingerprint = NULL,
                    rule_set_name_at_submission = NULL, rule_version_number_at_submission = NULL
                WHERE id = {job.Id}
                """);
        }
        db.ChangeTracker.Clear();

        var options = Options.Create(new ImportProcessingOptions());
        var reader = new LegacyReader("externalId,name,email,isActive\nfirst,First,first@example.com,true\n");
        await new CsvImportProcessor(db, new ImportJobStore(db), reader, new CsvRowParser(options), options,
            NullLogger<CsvImportProcessor>.Instance).ProcessAsync(job.Id, 1, Guid.NewGuid(), "worker", CancellationToken.None);

        db.ChangeTracker.Clear();
        var completed = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(1, completed.ProcessedRows);
        Assert.Equal(ImportPartStatus.Completed, completed.Parts[0].Status);
        if (clearPart)
        {
            Assert.Null(completed.Parts[0].RuleDocumentId);
            Assert.NotNull(completed.RuleDocumentId);
        }
        else
        {
            Assert.NotNull(completed.Parts[0].RuleDocumentId);
            Assert.Null(completed.RuleDocumentId);
        }
    }

    private static string ExecutionFingerprint(Guid userId, Guid sourceId, string fileSha256, Guid? versionId) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { version = 2, userId, sourceId, FileSha256 = fileSha256, Format = AcceptedFileFormat.Csv, RuleSetVersionId = versionId })));

    private sealed record ExpectedPins(Guid Version, Guid Document, string? ExecutionFingerprint,
        string? RuleSetNameAtSubmission, int Number, string? Hash);

    private static async Task AssertRestoredAsync(DataFlowDbContext db, Guid jobId, ExpectedPins expected)
    {
        db.ChangeTracker.Clear();
        var restored = await db.ImportJobs.SingleAsync(j => j.Id == jobId);
        var part = Assert.Single(restored.Parts);
        Assert.Equal(expected.Version, restored.RuleSetVersionId);
        Assert.Equal(expected.Document, restored.RuleDocumentId);
        Assert.Equal(expected.ExecutionFingerprint, restored.ExecutionFingerprint);
        Assert.Equal(expected.RuleSetNameAtSubmission, restored.RuleSetNameAtSubmission);
        Assert.Equal(expected.Number, restored.RuleVersionNumberAtSubmission);
        Assert.Equal(expected.Version, part.RuleSetVersionId);
        Assert.Equal(expected.Document, part.RuleDocumentId);
        Assert.Equal(expected.Hash, part.RuleSnapshotHash);
        Assert.Equal(expected.RuleSetNameAtSubmission, part.RuleSetNameAtSubmission);
        Assert.Equal(expected.Number, part.RuleVersionNumberAtSubmission);
    }

    private sealed class LegacyReader(string content) : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }
}
