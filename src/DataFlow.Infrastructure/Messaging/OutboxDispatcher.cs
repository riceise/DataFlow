using System.Text.Json;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DataFlow.Infrastructure.Messaging;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly string _owner = $"outbox-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RabbitMqSession? session = null;
        var failures = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(outboxOptions.Value.PollIntervalSeconds));
        using var ownerScope = logger.BeginScope(new Dictionary<string, object> { ["Owner"] = _owner });
        try
        {
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
                    var messages = await store.ClaimAsync(_owner, outboxOptions.Value.BatchSize, outboxOptions.Value.LeaseSeconds, stoppingToken);
                    foreach (var message in messages)
                    {
                        using var envelopeScope = logger.BeginScope(new Dictionary<string, object> { ["MessageId"] = message.Id });
                        IDisposable? jobScope = null;
                        try
                        {
                            var envelope = ImportMessageEnvelope.Create(message);
                            jobScope = logger.BeginScope(new Dictionary<string, object>
                            {
                                ["JobId"] = envelope.Payload.GetProperty("jobId").GetGuid()
                            });
                            // The claim transaction has committed before connecting or publishing.
                            session ??= await RabbitMqSession.ConnectAsync(rabbitOptions.Value, publisher: true, stoppingToken);
                            using var publishTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            publishTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await session.Channel.BasicPublishAsync(RabbitMqSession.Exchange, RabbitMqSession.RoutingKey, mandatory: true,
                                new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = message.Id.ToString(), Type = message.Type },
                                JsonSerializer.SerializeToUtf8Bytes(envelope, ImportMessageEnvelope.JsonOptions), publishTimeout.Token);
                            var updated = await store.MarkPublishedAsync(message.Id, _owner, stoppingToken);
                            logger.LogInformation("Outbox publish confirmed; claim completed: {ClaimCompleted}", updated == 1);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            logger.LogWarning("Outbox publication failed ({ErrorType})", exception.GetType().Name);
                            await store.MarkFailedAsync(message.Id, _owner, exception.GetType().Name, stoppingToken);
                            // Unattempted messages are released without incrementing their attempts.
                            await store.ReleaseAsync(_owner, stoppingToken);
                            throw;
                        }
                        finally { jobScope?.Dispose(); }
                    }
                    failures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning("Outbox dispatch unavailable ({ErrorType}); retrying with backoff", exception.GetType().Name);
                    if (session is not null)
                    {
                        await DisposeSessionAsync(session);
                        session = null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))), stoppingToken);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (session is not null)
            {
                await DisposeSessionAsync(session);
            }
        }
    }

    private async ValueTask DisposeSessionAsync(RabbitMqSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception exception)
        {
            logger.LogWarning("Outbox connection cleanup failed ({ErrorType})", exception.GetType().Name);
        }
    }
}
