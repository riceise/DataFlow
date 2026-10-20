namespace DataFlow.Domain.Entities;

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    private OutboxMessage(string type, string payload, DateTimeOffset occurredAtUtc)
    {
        Id = Guid.NewGuid();
        Type = type;
        Payload = payload;
        OccurredAt = occurredAtUtc.ToUniversalTime();
        Attempts = 0;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public string? Owner { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Outbox message type is required.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("Outbox message payload is required.", nameof(payload));
        }

        return new OutboxMessage(type.Trim(), payload, nowUtc);
    }

    public void MarkPublished(DateTimeOffset nowUtc)
    {
        if (PublishedAt is not null)
        {
            return;
        }

        PublishedAt = nowUtc.ToUniversalTime();
    }

    public void MarkAttemptFailed(string error, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("Outbox failure error is required.", nameof(error));
        }

        Attempts += 1;
        LastError = error.Trim();
    }
}
