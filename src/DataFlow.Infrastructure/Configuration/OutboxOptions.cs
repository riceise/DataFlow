using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    [Range(1, 100)]
    public int BatchSize { get; init; } = 32;

    [Range(10, 300)]
    public int LeaseSeconds { get; init; } = 30;

    [Range(1, 60)]
    public int PollIntervalSeconds { get; init; } = 2;
}
