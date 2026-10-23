using DataFlow.Domain.Exceptions;

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
            throw new DomainException("invalid_processed_message_id", "Processed message id is required.");
        }

        if (jobId == Guid.Empty)
        {
            throw new DomainException("invalid_processed_message_job_id", "Processed message job id is required.");
        }

        if (runVersion <= 0)
        {
            throw new DomainException("invalid_processed_message_run_version", "Processed message run version must be positive.");
        }

        return new ProcessedMessage(messageId.Trim(), jobId, runVersion, nowUtc);
    }
}
