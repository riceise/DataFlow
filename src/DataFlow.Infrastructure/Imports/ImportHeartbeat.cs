using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Infrastructure.Imports;

// A heartbeat must never share the processing DbContext or its batch transaction.
public sealed class ImportHeartbeat(string connectionString)
{
    public async Task RunAsync(ImportLease lease, int leaseSeconds, int heartbeatSeconds,
        CancellationTokenSource processing, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(heartbeatSeconds));
            while (await timer.WaitForNextTickAsync(stop))
            {
                await using var database = new DataFlowDbContext(new DbContextOptionsBuilder<DataFlowDbContext>()
                    .UseNpgsql(connectionString, options => options.CommandTimeout(5)).Options);
                if (!await new ImportJobStore(database).RenewAsync(lease, leaseSeconds, stop))
                {
                    await processing.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch
        {
            // Uncertain ownership is treated as lost ownership; the DB remains the fence.
            await processing.CancelAsync();
        }
    }
}
