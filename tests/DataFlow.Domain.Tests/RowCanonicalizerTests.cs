using System.Globalization;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.Normalization;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Tests;

public sealed class RowCanonicalizerTests
{
    private static readonly Guid Source = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    [Fact]
    public void Canon_WithDateAndBoolean_MatchesGoldenJsonAndSha256()
    {
        var data = new NormalizedCustomerData("ID-1", "Alice", "Alice@example.com", new(2024, 2, 29), true);
        const string expected = """{"externalId":"ID-1","name":"Alice","email":"Alice@example.com","registeredAt":"2024-02-29","isActive":true}""";
        Assert.Equal(expected, RowCanonicalizer.Canonicalize(data));
        Assert.Equal("d233322d815b2da26858b88942edd83bb9a792706e80a2bfb20e23c2917ebf40", RowCanonicalizer.ComputeSha256(expected));
    }

    [Fact]
    public void Canon_WithUnicodeHtmlAndNull_MatchesGoldenJsonAndSha256()
    {
        var data = new NormalizedCustomerData("Case:42", "Анна <&>", "A@example.com", null, false);
        const string expected = """{"externalId":"Case:42","name":"\u0410\u043D\u043D\u0430 \u003C\u0026\u003E","email":"A@example.com","registeredAt":null,"isActive":false}""";
        Assert.Equal(expected, RowCanonicalizer.Canonicalize(data));
        Assert.Equal("1d39077cddcf3a274fddce146caca6d594909db7dfb3172b0a0e7c6d04f99af8", RowCanonicalizer.ComputeSha256(expected));
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void CanonAndKey_AreCultureIndependent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var data = new NormalizedCustomerData("ID-1", "Alice", "Alice@example.com", new(2024, 2, 29), true);
            Assert.Equal("d233322d815b2da26858b88942edd83bb9a792706e80a2bfb20e23c2917ebf40",
                RowCanonicalizer.ComputeSha256(RowCanonicalizer.Canonicalize(data)));
            Assert.Equal($"{Source:N}:ID-1", RowCanonicalizer.BuildNaturalKey(Source, ["externalId"], data));
            Assert.Equal(RowCanonicalizer.BuildNaturalKey(Source, ["registeredAt", "isActive"], data),
                RowCanonicalizer.BuildNaturalKey(Source, ["isActive", "registeredAt"], data));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void EveryContentField_ParticipatesInHash()
    {
        var data = new NormalizedCustomerData("ID", "Alice", "A@example.com", null, false);
        var variants = new[]
        {
            data, data with { ExternalId = "id" }, data with { Name = "alice" },
            data with { Email = "a@example.com" }, data with { RegisteredAt = new(2024, 2, 29) },
            data with { IsActive = true }
        };
        Assert.Equal(variants.Length, variants.Select(value =>
            RowCanonicalizer.ComputeSha256(RowCanonicalizer.Canonicalize(value))).Distinct().Count());
    }

    [Fact]
    public void DefaultKey_PreservesExistingDomainFormatAndCaseAndScopesSource()
    {
        var data = new NormalizedCustomerData("ID:1", "Alice", "A@example.com", null, false);
        var key = RowCanonicalizer.BuildNaturalKey(Source, ["externalId"], data);
        Assert.Equal(CustomerNaturalKey.Build(Source, data.ExternalId), key);
        Assert.NotEqual(key, RowCanonicalizer.BuildNaturalKey(Guid.NewGuid(), ["externalId"], data));
        Assert.NotEqual(key, RowCanonicalizer.BuildNaturalKey(Source, ["externalId"], data with { ExternalId = "id:1" }));
        Assert.Equal(key, RowCanonicalizer.BuildNaturalKey(Source, ["externalId"], data with { Email = "other@example.com" }));
    }

    [Fact]
    public void CompositeKeys_AreUnambiguousOrderIndependentAndBounded()
    {
        var first = new NormalizedCustomerData("a:b", "c", "A@example.com", null, false);
        var second = first with { ExternalId = "a", Name = "b:c" };
        var key = RowCanonicalizer.BuildNaturalKey(Source, ["externalId", "name"], first);
        Assert.NotEqual(key, RowCanonicalizer.BuildNaturalKey(Source, ["externalId", "name"], second));
        Assert.Equal(key, RowCanonicalizer.BuildNaturalKey(Source, ["name", "externalId"], first));
        Assert.StartsWith($"sha256:{Source:N}:", key);
        Assert.True(key.Length <= 256);
        var oversized = RowCanonicalizer.BuildNaturalKey(Source, ["externalId"], first with { ExternalId = new string('a', 500) });
        Assert.True(oversized.Length <= 256);
        Assert.DoesNotContain(new string('a', 500), oversized);
    }

    [Fact]
    public void Key_RejectsMissingAndUnknownFields()
    {
        var data = new NormalizedCustomerData("ID", "Alice", "A@example.com", null, false);
        Assert.Equal("missing_key_field", Assert.Throws<DomainException>(() =>
            RowCanonicalizer.BuildNaturalKey(Source, ["registeredAt"], data)).Code);
        Assert.Equal("invalid_schema", Assert.Throws<DomainException>(() =>
            RowCanonicalizer.BuildNaturalKey(Source, ["unknown"], data)).Code);
        Assert.Equal("invalid_customer_key", Assert.Throws<DomainException>(() =>
            RowCanonicalizer.BuildNaturalKey(Guid.Empty, ["externalId"], data)).Code);
    }
}
