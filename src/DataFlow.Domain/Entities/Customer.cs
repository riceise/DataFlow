using DataFlow.Domain.Exceptions;

namespace DataFlow.Domain.Entities;

public sealed class Customer
{
    private Customer()
    {
    }

    private Customer(
        Guid dataSourceId,
        string externalId,
        string name,
        string email,
        DateOnly? registeredAt,
        bool isActive,
        string naturalKey,
        string rowHash,
        DateTimeOffset nowUtc)
    {
        Id = Guid.NewGuid();
        DataSourceId = dataSourceId;
        ExternalId = externalId;
        Name = name;
        Email = email;
        RegisteredAt = registeredAt;
        IsActive = isActive;
        NaturalKey = naturalKey;
        RowHash = rowHash;
        CreatedAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    public Guid Id { get; private set; }
    public Guid DataSourceId { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public DateOnly? RegisteredAt { get; private set; }
    public bool IsActive { get; private set; }
    public string NaturalKey { get; private set; } = string.Empty;
    public string RowHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Customer Create(
        Guid dataSourceId,
        string externalId,
        string name,
        string email,
        DateOnly? registeredAt,
        bool isActive,
        string rowHash,
        DateTimeOffset nowUtc)
    {
        if (dataSourceId == Guid.Empty)
        {
            throw new DomainException("invalid_customer_data_source_id", "Customer requires a data source id.");
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new DomainException("invalid_customer_external_id", "Customer external id is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("invalid_customer_name", "Customer name is required.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DomainException("invalid_customer_email", "Customer email is required.");
        }

        if (string.IsNullOrWhiteSpace(rowHash))
        {
            throw new DomainException("invalid_customer_row_hash", "Customer row hash is required.");
        }

        var utcNow = nowUtc.ToUniversalTime();

        return new Customer(
            dataSourceId,
            externalId.Trim(),
            name.Trim(),
            email.Trim(),
            registeredAt,
            isActive,
            CustomerNaturalKey.Build(dataSourceId, externalId),
            rowHash,
            utcNow);
    }

    public void ApplyNormalized(string name, string email, DateOnly? registeredAt, bool isActive, string rowHash, DateTimeOffset nowUtc)
    {
        Name = name.Trim();
        Email = email.Trim();
        RegisteredAt = registeredAt;
        IsActive = isActive;
        RowHash = rowHash;
        ExternalId = ExternalId.Trim();
        UpdatedAt = nowUtc.ToUniversalTime();
    }
}

public static class CustomerNaturalKey
{
    public const string Separator = ":";

    public static string Build(Guid dataSourceId, string normalizedExternalId)
    {
        if (dataSourceId == Guid.Empty)
        {
            throw new DomainException("invalid_customer_data_source_id", "Data source id is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            throw new DomainException("invalid_customer_external_id", "External id is required.");
        }

        return string.Concat(dataSourceId.ToString("N"), Separator, normalizedExternalId.Trim());
    }
}
