using System.Text.Json;
using System.Text.Json.Serialization;

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
            throw new ArgumentException("Customer requires a data source id.", nameof(dataSourceId));
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new ArgumentException("Customer external id is required.", nameof(externalId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Customer name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Customer email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(rowHash))
        {
            throw new ArgumentException("Customer row hash is required.", nameof(rowHash));
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
            throw new ArgumentException("Data source id is required.", nameof(dataSourceId));
        }

        if (string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            throw new ArgumentException("External id is required.", nameof(normalizedExternalId));
        }

        return string.Concat(dataSourceId.ToString("N"), Separator, normalizedExternalId.Trim());
    }
}

public sealed record CustomerRowData
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("registeredAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegisteredAt { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    public string ToCanonicalJson()
    {
        return JsonSerializer.Serialize(this);
    }
}
