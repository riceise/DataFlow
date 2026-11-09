using System.Text;
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
        db.ImportRows.Add(ImportRow.Create(job.Id, 2, ImportRowStatus.Invalid, "[]", "committed-key",
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

    private sealed class LegacyReader(string content) : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }
}
