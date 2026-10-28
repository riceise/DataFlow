namespace DataFlow.Domain.ValueObjects;

// Values have already passed the job's schema rules; strings retain their case.
public sealed record NormalizedCustomerData(
    string ExternalId,
    string Name,
    string Email,
    DateOnly? RegisteredAt,
    bool IsActive);
