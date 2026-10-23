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
        // Row locks keep linking transactions from racing the final recheck and deletion.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.UploadRequests.FromSqlInterpolated($"SELECT * FROM upload_requests WHERE job_id IS NULL AND created_at < {cutoff} ORDER BY created_at LIMIT 100 FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
        foreach (var upload in pending)
        {
            if (await db.ImportJobs.AnyAsync(x => x.StorageKey == upload.StorageKey, ct))
            {
                continue;
            }

            if (await storage.TryDeleteAsync(upload.StorageKey, upload.DataSourceId, ct))
            {
                db.UploadRequests.Remove(upload);
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
