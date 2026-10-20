namespace DataFlow.Domain.Entities;

public sealed class ProcessedMessage
{
    private ProcessedMessage()
    {
    }

    private ProcessedMessage(string messageId, Guid jobId, int runVersion, DateTimeOffset processedAtUtc)
    {
        MessageId = messageId;
        JobId = jobId;
        RunVersion = runVersion;
        ProcessedAt = processedAtUtc.ToUniversalTime();
    }

    public string MessageId { get; private set; } = string.Empty;
    public Guid JobId { get; private set; }
    public int RunVersion { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedMessage Create(string messageId, Guid jobId, int runVersion, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("Processed message id is required.", nameof(messageId));
        }

        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Processed message job id is required.", nameof(jobId));
        }

        if (runVersion <= 0)
        {
            throw new ArgumentException("Processed message run version must be positive.", nameof(runVersion));
        }

        return new ProcessedMessage(messageId.Trim(), jobId, runVersion, nowUtc);
    }
}
