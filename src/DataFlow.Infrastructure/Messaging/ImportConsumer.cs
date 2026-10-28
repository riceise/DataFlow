using System.Text.Json;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DataFlow.Infrastructure.Messaging;

public sealed class ImportConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<ImportConsumer> logger) : BackgroundService
{
    private readonly string _owner = $"consumer-{Environment.ProcessId}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        using var ownerScope = logger.BeginScope(new Dictionary<string, object> { ["Owner"] = _owner });
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var session = await RabbitMqSession.ConnectAsync(options.Value, publisher: false, stoppingToken);
                var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Channel.ChannelShutdownAsync += (_, _) =>
                {
                    interrupted.TrySetResult();
                    return Task.CompletedTask;
                };
                await session.Channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(session.Channel);
                consumer.UnregisteredAsync += (_, _) =>
                {
                    interrupted.TrySetResult();
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        await GuardAsync(delivery.Body, stoppingToken);
                        await session.Channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                        logger.LogInformation("Import delivery acknowledged");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { interrupted.TrySetResult(); }
                    catch (Exception exception) { interrupted.TrySetException(exception); }
                };
                await session.Channel.BasicConsumeAsync(RabbitMqSession.Queue, autoAck: false, consumer, stoppingToken);
                failures = 0;
                await interrupted.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Import consumer unavailable ({ErrorType}); reconnecting with backoff", exception.GetType().Name);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task GuardAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ImportMessageEnvelope? envelope;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("contractVersion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != ImportMessageEnvelope.SupportedVersion)
            {
                logger.LogError("Unsupported import contract version; acknowledging delivery");
                return;
            }

            envelope = root.Deserialize<ImportMessageEnvelope>(ImportMessageEnvelope.JsonOptions);
            if (envelope is null || envelope.MessageId == Guid.Empty)
            {
                logger.LogError("Invalid import envelope; acknowledging delivery");
                return;
            }
        }
        catch (JsonException)
        {
            logger.LogError("Malformed import envelope; acknowledging delivery");
            return;
        }

        using var messageScope = logger.BeginScope(new Dictionary<string, object> { ["MessageId"] = envelope.MessageId });
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DataFlowDbContext>();
        if (await database.ProcessedMessages.AnyAsync(x => x.MessageId == envelope.MessageId.ToString(), cancellationToken))
        {
            logger.LogInformation("Previously processed import message ignored");
            return;
        }

        ImportJobCreatedPayload? payload;
        try
        {
            payload = envelope.Payload.ValueKind == JsonValueKind.Object
                ? envelope.Payload.Deserialize<ImportJobCreatedPayload>(ImportMessageEnvelope.JsonOptions)
                : null;
            if (envelope.Type != RabbitMqSession.RoutingKey || payload is null || payload.JobId == Guid.Empty || payload.RunVersion <= 0)
            {
                logger.LogError("Invalid import message type or payload; acknowledging delivery");
                return;
            }
        }
        catch (JsonException)
        {
            logger.LogError("Malformed import payload; acknowledging delivery");
            return;
        }

        using var jobScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = payload.JobId });
        if (!await database.ImportJobs.AnyAsync(x => x.Id == payload.JobId && x.RunVersion == payload.RunVersion, cancellationToken))
        {
            logger.LogInformation("Missing job or stale import run ignored");
            return;
        }

        // Этап 7: process batches and atomically finalize job + ProcessedMessage before acknowledging.
        logger.LogInformation("Import message accepted; row processing starts at stage 7");
    }
}
