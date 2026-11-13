using System.Diagnostics.Metrics;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Observability;

// Collect bounded snapshots asynchronously; exporter callbacks never perform network IO.
public sealed class OperationalMetricsService(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> rabbit,
    ILogger<OperationalMetricsService> logger) : BackgroundService
{
    private Measurement<long>[] _statuses = [];
    private long _backlog;
    private double _oldestAge;
    private long _dead;
    private long _lastDbSuccess;
    private long _lastBrokerSuccess;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.jobs", () => Volatile.Read(ref _statuses), "{job}");
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.outbox.pending", () => Interlocked.Read(ref _backlog), "{message}");
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.outbox.oldest_age", () => Volatile.Read(ref _oldestAge), "s");
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.dlq.ready", () => Interlocked.Read(ref _dead), "{message}");
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.metrics.db.last_success", () => Interlocked.Read(ref _lastDbSuccess), "s");
        DataFlowTelemetry.Meter.CreateObservableGauge("dataflow.metrics.broker.last_success", () => Interlocked.Read(ref _lastBrokerSuccess), "s");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DataFlowDbContext>();
                var counts = await db.ImportJobs.AsNoTracking().GroupBy(j => j.Status)
                    .Select(g => new { Status = g.Key, Count = g.LongCount() }).ToListAsync(timeout.Token);
                Volatile.Write(ref _statuses, Enum.GetValues<Domain.Enums.ImportJobStatus>().Select(s =>
                    new Measurement<long>(counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0,
                        new KeyValuePair<string, object?>("status", s.ToString()))).ToArray());
                var pending = db.OutboxMessages.Where(m => m.PublishedAt == null);
                Interlocked.Exchange(ref _backlog, await pending.LongCountAsync(timeout.Token));
                var oldest = await pending.MinAsync(m => (DateTimeOffset?)m.OccurredAt, timeout.Token);
                Volatile.Write(ref _oldestAge, oldest is null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds));
                Interlocked.Exchange(ref _lastDbSuccess, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Operational DB metrics unavailable ({ErrorType})", ex.GetType().Name); }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var connection = await BrokerConnection.CreateAsync(rabbit.Value, timeout.Token);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
                var queue = await channel.QueueDeclarePassiveAsync(ImportRecoveryMessage.Dead, timeout.Token);
                Interlocked.Exchange(ref _dead, queue.MessageCount);
                Interlocked.Exchange(ref _lastBrokerSuccess, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Operational broker metrics unavailable ({ErrorType})", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
