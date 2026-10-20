namespace DataFlow.Worker;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DataFlow Worker started. Import processing is not configured yet.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
