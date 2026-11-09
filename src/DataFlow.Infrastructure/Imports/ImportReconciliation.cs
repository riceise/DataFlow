using System.Text.Json;
using DataFlow.Domain.Entities;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataFlow.Infrastructure.Imports;

public sealed class ImportReconciliationStore(DataFlowDbContext database)
{
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10s'; SET LOCAL lock_timeout = '5s'", cancellationToken);
        var jobs = await database.ImportJobs.FromSqlRaw("""
            SELECT j.* FROM import_jobs j
            WHERE ((status = 1 AND updated_at < clock_timestamp() - interval '60 seconds')
                OR (status = 2 AND lease_until < clock_timestamp() - interval '15 seconds')
                OR (status = 3 AND next_attempt_at < clock_timestamp() - interval '60 seconds'))
                AND updated_at < clock_timestamp() - interval '60 seconds'
                AND NOT EXISTS (SELECT 1 FROM outbox_messages o
                    WHERE o.type = 'import.job.created' AND o.payload->>'jobId' = j.id::text
                    AND o.payload->>'runVersion' = j.run_version::text
                    AND (o.published_at IS NULL OR o.published_at > clock_timestamp() - interval '60 seconds'))
            ORDER BY updated_at LIMIT 50 FOR UPDATE OF j SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var now = await database.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var attempts = await database.OutboxMessages.FromSqlInterpolated($"""
                SELECT * FROM outbox_messages WHERE type = 'import.job.created' AND payload->>'jobId' = {job.Id.ToString()}
                AND payload->>'runVersion' = {job.RunVersion.ToString()}
                """).AsNoTracking()
                .Select(x => x.Payload).ToListAsync(cancellationToken);
            var attempt = attempts.Select(x => JsonDocument.Parse(x)).Select(document =>
            {
                using (document)
                {
                    var root = document.RootElement;
                    return root.GetProperty("runVersion").GetInt32() == job.RunVersion && root.TryGetProperty("attempt", out var value)
                        ? value.GetInt32() : 1;
                }
            }).DefaultIfEmpty(1).Max();
            database.OutboxMessages.Add(OutboxMessage.Create(RabbitMqSession.RoutingKey, JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                dataSourceId = job.DataSourceId,
                schemaVersion = job.SchemaVersion,
                runVersion = job.RunVersion,
                attempt
            }), now));
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET updated_at = {now} WHERE id = {job.Id}", cancellationToken);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return jobs.Count;
    }
}

public sealed class ImportReconciliation(IServiceScopeFactory scopeFactory, ILogger<ImportReconciliation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ImportReconciliationStore>().ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning("Reconciliation failed ({ErrorType})", exception.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
