using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Normalization;

public static class RowCanonicalizer
{
    // Canon v1: all five Customer fields, explicit JSON null, invariant ISO date,
    // fixed property order and JavaScriptEncoder.Default (including Unicode/HTML escaping).
    // Neither schema order/version nor a source id participates in the content hash.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null
    };

    public static string Canonicalize(NormalizedCustomerData data) => JsonSerializer.Serialize(new CanonicalCustomer(
        data.ExternalId, data.Name, data.Email,
        data.RegisteredAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data.IsActive), JsonOptions);

    public static string ComputeSha256(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public static string BuildNaturalKey(Guid dataSourceId, IReadOnlyList<string> keyFields, NormalizedCustomerData data)
    {
        if (dataSourceId == Guid.Empty || keyFields.Count == 0)
        {
            throw new DomainException("invalid_customer_key", "A source and key fields are required.");
        }

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in keyFields)
        {
            var value = field switch
            {
                "externalId" => data.ExternalId,
                "name" => data.Name,
                "email" => data.Email,
                "registeredAt" => data.RegisteredAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "isActive" => data.IsActive ? "true" : "false",
                _ => throw new DomainException("invalid_schema", "Unknown Customer key field.")
            };
            if (string.IsNullOrEmpty(value))
            {
                throw new DomainException("missing_key_field", "Key fields must have a value.");
            }

            values.Add(field, value);
        }

        // Preserve the existing MVP key for externalId. Other/oversized keys use an
        // unambiguous, bounded representation compatible with the 256-character index.
        if (values.Count == 1 && values.TryGetValue("externalId", out var externalId) && externalId.Length <= 80)
        {
            return CustomerNaturalKey.Build(dataSourceId, externalId);
        }

        return $"sha256:{dataSourceId:N}:{ComputeSha256(JsonSerializer.Serialize(values, JsonOptions))}";
    }

    private sealed record CanonicalCustomer(
        [property: JsonPropertyName("externalId"), JsonPropertyOrder(0)] string ExternalId,
        [property: JsonPropertyName("name"), JsonPropertyOrder(1)] string Name,
        [property: JsonPropertyName("email"), JsonPropertyOrder(2)] string Email,
        [property: JsonPropertyName("registeredAt"), JsonPropertyOrder(3)] string? RegisteredAt,
        [property: JsonPropertyName("isActive"), JsonPropertyOrder(4)] bool IsActive);
}
