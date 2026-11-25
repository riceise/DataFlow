using DataFlow.Application.Abstractions;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Storage;

public sealed class OrphanCleanupService(IServiceScopeFactory scopes, IOptions<StorageOptions> options,
    TimeProvider clock, ILogger<OrphanCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Orphan cleanup failed: {ErrorType}", ex.GetType().Name); }
        }
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataFlowDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var cutoff = clock.GetUtcNow().AddSeconds(-options.Value.OrphanAgeSeconds);
        var candidates = await db.UploadRequests.AsNoTracking().Where(x => x.JobId == null && x.CreatedAt < cutoff)
            .OrderBy(x => x.CreatedAt).Take(100).Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in candidates)
        {
            var storageKey = await db.UploadRequests.AsNoTracking().Where(x => x.Id == id).Select(x => x.StorageKey)
                .SingleOrDefaultAsync(ct);
            if (storageKey is null)
            {
                continue;
            }

            await using var objectLock = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, storageKey,
                false, ct);
            if (objectLock is null)
            {
                continue;
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var upload = await db.UploadRequests.FromSqlInterpolated(
                $"SELECT * FROM upload_requests WHERE id = {id} AND job_id IS NULL FOR UPDATE").SingleOrDefaultAsync(ct);
            if (upload is null || await db.ImportJobs.AnyAsync(x => x.StorageKey == upload.StorageKey, ct) ||
                await db.PreparedUploads.AnyAsync(x => x.StorageKey == upload.StorageKey, ct))
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            if (await storage.TryDeleteAsync(upload.StorageKey, upload.DataSourceId, ct))
            {
                db.UploadRequests.Remove(upload);
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }
}
