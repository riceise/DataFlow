using System.Text.Json;
using DataFlow.Domain.Entities;

namespace DataFlow.Infrastructure.Messaging;

public static class ImportRecoveryMessage
{
    public static readonly int[] Delays = [5, 30, 300];
    public const string Dead = "imports.dead";

    public static OutboxMessage Create(ImportJob job, int attempt, DateTimeOffset now, bool dead = false) =>
        OutboxMessage.Create(dead ? Dead : RabbitMqSession.RoutingKey,
            JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                dataSourceId = job.DataSourceId,
                schemaVersion = job.SchemaVersion,
                runVersion = job.RunVersion,
                attempt,
                retryDelay = !dead && attempt > 1 ? Delays[Math.Min(attempt - 2, 2)] : 0
            }), now);

    public static string Route(ImportMessageEnvelope envelope)
    {
        if (envelope.Type == Dead) { return Dead; }
        return envelope.Payload.TryGetProperty("retryDelay", out var delay) && delay.GetInt32() > 0
            ? $"imports.retry.{delay.GetInt32()}" : RabbitMqSession.RoutingKey;
    }
}
