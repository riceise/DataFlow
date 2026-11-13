using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Domain.Enums;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Observability;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Storage;

public sealed class StorageMaintenanceService(IServiceScopeFactory scopes, IOptions<StorageOptions> options,
    ILogger<StorageMaintenanceService> logger, TimeProvider? clock = null) : BackgroundService
{
    private DateTimeOffset UtcNow => (clock ?? TimeProvider.System).GetUtcNow();
    private string? _continuation;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds));
        do
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                CleanupTemporaryFiles();
                await CleanupObjectsAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Storage maintenance failed ({ErrorType})", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public int CleanupTemporaryFiles()
    {
        var directory = options.Value.TempDirectory;
        if (!Directory.Exists(directory)) { return 0; }
        var count = 0;
        var cutoff = UtcNow.UtcDateTime.AddSeconds(-options.Value.TempGraceSeconds);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 64 || !name.All(char.IsAsciiHexDigit) || File.GetLastWriteTimeUtc(path) >= cutoff) { continue; }
            try
            {
                // Upload endpoint keeps an exclusive sidecar open through dispatch/compensation.
                using (var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path) < cutoff) { File.Delete(path); count++; }
                }
                File.Delete(path + ".lock");
            }
            catch (IOException) { }
            if (count >= options.Value.CleanupBatchSize) { break; }
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.lock"))
        {
            var name = Path.GetFileName(path)[..^5];
            if (name.Length != 64 || !name.All(char.IsAsciiHexDigit) ||
                File.GetLastWriteTimeUtc(path) >= cutoff || File.Exists(path[..^5])) { continue; }
            try
            {
                using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(path);
            }
            catch (IOException) { }
        }
        DataFlowTelemetry.Deleted.Add(count, new KeyValuePair<string, object?>("kind", "temp"));
        return count;
    }

    public async Task CleanupObjectsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataFlowDbContext>();
        var s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        var settings = options.Value;
        var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = settings.Bucket,
            Prefix = "sources/",
            MaxKeys = settings.CleanupBatchSize,
            ContinuationToken = _continuation
        }, ct);
        _continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
        foreach (var item in page.S3Objects ?? [])
        {
            // Never delete foreign objects, directories, or recent/in-flight uploads.
            var parts = item.Key.Split('/');
            if (parts.Length != 6 || !Guid.TryParseExact(parts[1], "N", out _) || !Guid.TryParseExact(parts[5], "N", out _) ||
                !DateOnly.TryParseExact($"{parts[2]}-{parts[3]}-{parts[4]}", "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _) ||
                item.LastModified is not { } modified || modified.ToUniversalTime() >= UtcNow.UtcDateTime.AddSeconds(-settings.OrphanAgeSeconds)) { continue; }
            await using var objectLock = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, item.Key, false, ct);
            if (objectLock is null) { continue; }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var jobs = await db.ImportJobs.FromSqlInterpolated($"SELECT * FROM import_jobs WHERE storage_key = {item.Key} FOR UPDATE").ToListAsync(ct);
            var cutoff = UtcNow.AddDays(-settings.CompletedFileRetentionDays);
            if (jobs.Count > 0 && (settings.CompletedFileRetentionDays == 0 || jobs.Any(j =>
                    !j.IsTerminal() || j.Status == ImportJobStatus.Failed && j.Retryable != 0 || j.CompletedAt is null || j.CompletedAt >= cutoff)))
            {
                await transaction.CommitAsync(ct);
                continue;
            }
            await s3.DeleteObjectAsync(settings.Bucket, item.Key, ct);
            await transaction.CommitAsync(ct);
            DataFlowTelemetry.Deleted.Add(1, new KeyValuePair<string, object?>("kind", jobs.Count == 0 ? "orphan" : "retained_source"));
            logger.LogInformation("Storage cleanup removed object; reason {Reason}", jobs.Count == 0 ? "orphan" : "retention");
        }
    }
}
