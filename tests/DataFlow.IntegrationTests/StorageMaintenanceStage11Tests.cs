using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class StorageMaintenanceStage11Tests(DataSourcesApiFixture postgres, MinioFixture minio) :
    IClassFixture<DataSourcesApiFixture>, IClassFixture<MinioFixture>
{
    [Fact]
    public async Task Sweep_DeletesOldOrphansAndExpiredFinalSources_PreservesActiveRetryableAndRecent()
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("dataflow_test", "dataflow_test_password"),
            new AmazonS3Config { ServiceURL = minio.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var bucket = "maintenance-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await using var db = postgres.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var source = DataSource.Create("Maintenance", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true)]), now);
        db.DataSources.Add(source);
        var active = Job();
        var retryable = Job(); retryable.Fail("test", "test", true, now);
        var expired = Job(); expired.Fail("bad_file", "test", false, now);
        var recent = Job(); recent.Fail("bad_file", "test", false, now.AddDays(2));
        db.ImportJobs.AddRange(active, retryable, expired, recent);
        await db.SaveChangesAsync();
        var orphan = Key();
        var inFlight = Key();
        var foreign = "sources/not-an-owned-object";
        foreach (var key in new[] { active.StorageKey, retryable.StorageKey, expired.StorageKey, recent.StorageKey, orphan, inFlight, foreign })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "synthetic" });
        }
        var services = new ServiceCollection();
        services.AddScoped(_ => postgres.CreateContext());
        services.AddSingleton<IAmazonS3>(s3);
        using var provider = services.BuildServiceProvider();
        var options = new StorageOptions { Bucket = bucket, CompletedFileRetentionDays = 2, CleanupBatchSize = 2 };
        var clock = new FixedClock(now.AddDays(3));
        var cleaner = new StorageMaintenanceService(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance, clock);
        // Grace period protects even unregistered objects while they are recent.
        var graceCleaner = new StorageMaintenanceService(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance, new FixedClock(now));
        for (var i = 0; i < 5; i++) { await graceCleaner.CleanupObjectsAsync(default); }
        Assert.Equal(7, (await List()).Count);
        await using (var held = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, inFlight, true, default))
        {
            // Several bounded pages exercise continuation and per-object upload locks.
            for (var i = 0; i < 5; i++) { await cleaner.CleanupObjectsAsync(default); }
            var remaining = await List();
            Assert.DoesNotContain(orphan, remaining);
            Assert.DoesNotContain(expired.StorageKey, remaining);
            Assert.Contains(active.StorageKey, remaining);
            Assert.Contains(retryable.StorageKey, remaining);
            Assert.Contains(recent.StorageKey, remaining);
            Assert.Contains(inFlight, remaining);
            Assert.Contains(foreign, remaining);
        }
        for (var i = 0; i < 5; i++) { await cleaner.CleanupObjectsAsync(default); }
        Assert.DoesNotContain(inFlight, await List());
        Assert.Equal(4, await db.ImportJobs.CountAsync(j => j.DataSourceId == source.Id));

        string Key() => $"sources/{source.Id:N}/2026/11/13/{Guid.NewGuid():N}";
        ImportJob Job() => ImportJob.Create(source.Id, "test.csv", Key(), new string('a', 64), source.SchemaJson, 1, now);
        async Task<List<string>> List() => (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects.Select(o => o.Key).ToList();
    }

    [Fact]
    public async Task PreparedExpiry_IsRecoverableAndConsumedObjectsFollowJobRetention()
    {
        using var s3 = new AmazonS3Client(new BasicAWSCredentials("dataflow_test", "dataflow_test_password"),
            new AmazonS3Config { ServiceURL = minio.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var bucket = "maintenance-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await using var db = postgres.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = owner,
            UserName = "cleanup@example.test",
            NormalizedUserName = "CLEANUP@EXAMPLE.TEST",
            Email = "cleanup@example.test",
            NormalizedEmail = "CLEANUP@EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        var source = DataSource.Create("Prepared cleanup", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true)]), now);
        source.AssignOwner(owner);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        var expired = Prepared("expired", now.AddDays(-3), now.AddDays(-2));
        var missing = Prepared("missing", now.AddDays(-3), now.AddDays(-2));
        var active = Prepared("active", now, now.AddDays(10));
        var interrupted = Prepared("interrupted", now.AddDays(-3), now.AddDays(-2));
        interrupted.BeginExpiryCleanup();
        var consumedJob = ImportJob.Create(source.Id, "consumed.csv", Key(), new string('a', 64), source.SchemaJson, 1, now);
        consumedJob.Fail("bad_file", "test", false, now);
        var consumed = Prepared("consumed", now.AddDays(-3), now.AddHours(1));
        consumed.Consume(consumedJob.Id, now);
        db.PreparedUploads.AddRange(expired, missing, active, interrupted, consumed);
        db.ImportJobs.Add(consumedJob);
        await db.SaveChangesAsync();
        var expiredKey = expired.StorageKey!;
        foreach (var prepared in new[] { expired, active, interrupted, consumed })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = prepared.StorageKey, ContentBody = "synthetic" });
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => postgres.CreateContext());
        services.AddSingleton<IAmazonS3>(s3);
        using var provider = services.BuildServiceProvider();
        var options = new StorageOptions { Bucket = bucket, CompletedFileRetentionDays = 2, CleanupBatchSize = 10 };
        var cleaner = new StorageMaintenanceService(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<StorageMaintenanceService>.Instance, new FixedClock(now.AddDays(3)));
        await cleaner.CleanupPreparedUploadsAsync(default);
        db.ChangeTracker.Clear();
        foreach (var id in new[] { expired.Id, missing.Id, interrupted.Id })
        {
            var tombstone = await db.PreparedUploads.SingleAsync(x => x.Id == id);
            Assert.Equal(PreparedUploadStatus.Expired, tombstone.Status);
            Assert.Null(tombstone.StorageKey);
            Assert.Null(tombstone.ManifestJson);
            Assert.Null(tombstone.ReplayResponseCiphertext);
        }

        Assert.Null((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects
            .SingleOrDefault(x => x.Key == expiredKey));
        Assert.Equal(PreparedUploadStatus.Ready, (await db.PreparedUploads.SingleAsync(x => x.Id == active.Id)).Status);
        Assert.Equal(PreparedUploadStatus.Consumed, (await db.PreparedUploads.SingleAsync(x => x.Id == consumed.Id)).Status);
        await cleaner.CleanupObjectsAsync(default);
        var remaining = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects.Select(x => x.Key).ToArray();
        Assert.Contains(active.StorageKey, remaining);
        Assert.DoesNotContain(consumedJob.StorageKey, remaining);

        PreparedUpload Prepared(string suffix, DateTimeOffset created, DateTimeOffset expires) => PreparedUpload.CreateReady(owner,
            source.Id, suffix, new string('a', 64), Key(), suffix + ".csv", new string('b', 64), AcceptedFileFormat.Csv,
            10, new string('c', 64), [1, 2, 3], "v1", "{}", created, expires);
        string Key() => $"sources/{source.Id:N}/2026/11/25/{Guid.NewGuid():N}";
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
