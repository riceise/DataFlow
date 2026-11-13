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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
