using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.Normalization;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Tests;

public sealed class CustomerRowNormalizerTests
{
    private static readonly Guid Source = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    [Fact]
    public void Normalization_TrimToCanonToSha256_MatchesGolden()
    {
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source, Row(new()
        {
            ["externalId"] = " ID-1 ",
            ["name"] = "\tAlice\r\n",
            ["email"] = " Alice@example.com ",
            ["registeredAt"] = " 2024-02-29 ",
            ["isActive"] = " true "
        }));
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(new NormalizedCustomerData("ID-1", "Alice", "Alice@example.com", new(2024, 2, 29), true), result.Customer);
        Assert.Equal($"{Source:N}:ID-1", result.NaturalKey);
        Assert.Equal("""{"externalId":"ID-1","name":"Alice","email":"Alice@example.com","registeredAt":"2024-02-29","isActive":true}""",
            RowCanonicalizer.Canonicalize(result.Customer!));
        Assert.Equal("d233322d815b2da26858b88942edd83bb9a792706e80a2bfb20e23c2917ebf40", result.RowHash);
    }

    [Fact]
    public void Normalization_PreservesUnicodeCaseAndExplicitNull_Golden()
    {
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source, Row(new()
        {
            ["externalId"] = " Case:42 ",
            ["name"] = " Анна <&> ",
            ["email"] = " A@example.com ",
            ["registeredAt"] = "\t"
        }));
        Assert.True(result.IsValid);
        Assert.Equal("Анна <&>", result.Customer!.Name);
        Assert.Equal("A@example.com", result.Customer.Email);
        Assert.Equal($"{Source:N}:Case:42", result.NaturalKey);
        Assert.Equal("1d39077cddcf3a274fddce146caca6d594909db7dfb3172b0a0e7c6d04f99af8", result.RowHash);
    }

    [Theory]
    [InlineData("externalId", " ", "required")]
    [InlineData("name", "\t", "required")]
    [InlineData("email", null, "required")]
    [InlineData("isActive", "", "required")]
    [InlineData("externalId", "12345678901", "max_length")]
    [InlineData("name", "12345678901", "max_length")]
    [InlineData("email", "long-address@example.com", "max_length")]
    [InlineData("email", "invalid", "invalid_email")]
    [InlineData("email", "Name <a@b.com>", "invalid_email")]
    [InlineData("email", "a@b.com\r\nx", "invalid_email")]
    [InlineData("registeredAt", "2023-02-29", "invalid_date")]
    [InlineData("registeredAt", "29.02.2024", "invalid_date")]
    [InlineData("registeredAt", "2024-2-29", "invalid_date")]
    [InlineData("registeredAt", "2024-02-29T00:00:00Z", "invalid_date")]
    [InlineData("isActive", "1", "invalid_type")]
    [InlineData("isActive", "True", "invalid_type")]
    [InlineData("isActive", "FALSE", "invalid_type")]
    public void Rules_ReturnSafeStableErrors(string field, string? value, string code)
    {
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source, Row(new() { [field] = value }));
        Assert.False(result.IsValid);
        Assert.Null(result.Customer);
        Assert.Null(result.RowHash);
        var error = Assert.Single(result.Errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        if (!string.IsNullOrWhiteSpace(value) && value.Length > 3)
        {
            Assert.DoesNotContain(value, error.Message);
        }
    }

    [Fact]
    public void LengthChecks_RunAfterTrim_AndAcceptBoundary()
    {
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source,
            Row(new() { ["externalId"] = " 1234567890 ", ["name"] = " 1234567890 " }));
        Assert.True(result.IsValid);
        Assert.Equal($"{Source:N}:1234567890", result.NaturalKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void OptionalDate_EmptyAndMissingSchemaFieldHaveSameNullCanon(string? value)
    {
        var withField = new CustomerRowNormalizer(Schema()).Normalize(Source, Row(new() { ["registeredAt"] = value }));
        var withoutField = new CustomerRowNormalizer(Schema(includeDate: false)).Normalize(Source, Row());
        Assert.True(withField.IsValid);
        Assert.Null(withField.Customer!.RegisteredAt);
        Assert.Equal(withField.Customer, withoutField.Customer);
        Assert.Equal(withField.RowHash, withoutField.RowHash);
        Assert.Contains("\"registeredAt\":null", RowCanonicalizer.Canonicalize(withField.Customer));
    }

    [Fact]
    public void MissingRequiredCell_IsRowError_NotException()
    {
        var row = Row();
        var values = row.Values.Where(pair => pair.Key != "name").ToDictionary(pair => pair.Key, pair => pair.Value);
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source, row with { Values = values });
        Assert.Equal("required", Assert.Single(result.Errors).Code);
        Assert.NotNull(result.NaturalKey);
    }

    [Fact]
    public void SchemaSnapshotControlsLimits_AndFieldOrderDoesNotChangeHash()
    {
        var baseline = Schema();
        var tighter = new DataSourceSchema(2, baseline.KeyFields, baseline.Fields.Select(field => field.Name == "name"
            ? new DataSourceFieldDefinition("name", "string", true, 3) : field).ToArray());
        var reordered = new DataSourceSchema(3, baseline.KeyFields, baseline.Fields.Reverse().ToArray());
        var valid = new CustomerRowNormalizer(DataSourceSchema.FromJson(baseline.ToJson())).Normalize(Source, Row());
        Assert.True(valid.IsValid);
        Assert.Equal("max_length", Assert.Single(new CustomerRowNormalizer(tighter).Normalize(Source, Row()).Errors).Code);
        Assert.Equal(valid.RowHash, new CustomerRowNormalizer(reordered).Normalize(Source, Row()).RowHash);
    }

    [Fact]
    public void EmptyPhysicalRowIsSkipped_ButExplicitEmptyCellsAreValidated()
    {
        var normalizer = new CustomerRowNormalizer(Schema());
        var empty = normalizer.Normalize(Source, Row() with { IsEmpty = true });
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsValid);
        Assert.Null(empty.NaturalKey);
        Assert.Null(empty.RowHash);
        Assert.Empty(empty.Errors);
        var explicitCells = normalizer.Normalize(Source, new(3, new Dictionary<string, string?>(), "[]"));
        Assert.False(explicitCells.IsEmpty);
        Assert.Equal(4, explicitCells.Errors.Count);
        Assert.All(explicitCells.Errors, error => Assert.Equal("required", error.Code));
    }

    [Fact]
    public void ParserErrorsArePreservedAlongsideAllSchemaErrors_AndRawDataIsUnchanged()
    {
        var raw = Row(new() { ["name"] = "", ["email"] = "bad", ["isActive"] = "yes" }) with
        {
            Errors = [new("$row", "column_count_mismatch", "The column count does not match the header.")]
        };
        var result = new CustomerRowNormalizer(Schema()).Normalize(Source, raw);
        Assert.Equal(new[] { "column_count_mismatch", "required", "invalid_email", "invalid_type" }, result.Errors.Select(error => error.Code));
        Assert.NotNull(result.NaturalKey);
        Assert.Null(result.Customer);
        Assert.Null(result.RowHash);
        using var json = JsonDocument.Parse(raw.RawData);
        Assert.Equal("bad", json.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public void UnsupportedSnapshot_IsControlledFailure()
    {
        var baseline = Schema();
        var fields = baseline.Fields.Select(field => field.Name == "name"
            ? new DataSourceFieldDefinition("name", "integer", true) : field).ToArray();
        Assert.Equal("invalid_schema", Assert.Throws<DomainException>(() =>
            new CustomerRowNormalizer(new(1, baseline.KeyFields, fields))).Code);
    }

    [Fact]
    public void FileDuplicates_ReserveFirstKeyEvenOnInvalidRow_AndPreserveCase()
    {
        var processor = new CustomerFileRowProcessor(Source, Schema());
        var invalid = processor.Process(Row(new() { ["email"] = "bad" }));
        Assert.False(invalid.IsValid);
        Assert.NotNull(invalid.NaturalKey);
        var repeat = processor.Process(Row(new() { ["externalId"] = " ID-1 ", ["name"] = "Other" }));
        Assert.Equal("duplicate_key_in_file", Assert.Single(repeat.Errors).Code);
        Assert.Equal(invalid.NaturalKey, repeat.NaturalKey);
        Assert.Null(repeat.Customer);
        Assert.Null(repeat.RowHash);
        Assert.True(processor.Process(Row(new() { ["externalId"] = "id-1" })).IsValid);
    }

    [Fact]
    public void DuplicateWithSameContent_IsInvalid_AndInstancesDoNotShareKeys()
    {
        var processor = new CustomerFileRowProcessor(Source, Schema());
        var first = processor.Process(Row());
        Assert.True(first.IsValid);
        var repeat = processor.Process(Row());
        Assert.False(repeat.IsValid);
        Assert.Equal("duplicate_key_in_file", Assert.Single(repeat.Errors).Code);
        Assert.True(new CustomerFileRowProcessor(Source, Schema()).Process(Row()).IsValid);
    }

    [Fact]
    public void DuplicateSetCanBeRestoredFromCommittedKeys_AndMissingKeysAreNotReserved()
    {
        var processor = new CustomerFileRowProcessor(Source, Schema(), [$"{Source:N}:ID-1"]);
        Assert.Equal("duplicate_key_in_file", Assert.Single(processor.Process(Row()).Errors).Code);
        for (var i = 0; i < 2; i++)
        {
            var missing = processor.Process(Row(new() { ["externalId"] = " " }));
            Assert.Null(missing.NaturalKey);
            Assert.Equal("required", Assert.Single(missing.Errors).Code);
        }
    }

    [Fact]
    public void KeyUsesConfiguredFields_AfterNormalization()
    {
        var baseline = Schema();
        var schema = new DataSourceSchema(1, ["email", "externalId"], baseline.Fields);
        var processor = new CustomerFileRowProcessor(Source, schema);
        var first = processor.Process(Row());
        Assert.True(first.IsValid);
        Assert.Equal(RowCanonicalizer.BuildNaturalKey(Source, schema.KeyFields, first.Customer!), first.NaturalKey);
        var second = processor.Process(Row(new() { ["email"] = " a@b.com " }));
        Assert.Equal("duplicate_key_in_file", Assert.Single(second.Errors).Code);
        Assert.True(processor.Process(Row(new() { ["email"] = "A@b.com" })).IsValid);
    }

    private static DataSourceSchema Schema(bool includeDate = true)
    {
        var fields = new List<DataSourceFieldDefinition>
        {
            new("externalId", "string", true, 10), new("name", "string", true, 10),
            new("email", "email", true, 20), new("isActive", "boolean", true)
        };
        if (includeDate)
        {
            fields.Add(new("registeredAt", "date", false, format: "yyyy-MM-dd"));
        }

        return new(1, ["externalId"], fields);
    }

    private static ParsedRow Row(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["externalId"] = "ID-1",
            ["name"] = "Alice",
            ["email"] = "a@b.com",
            ["isActive"] = "false"
        };
        foreach (var pair in overrides ?? [])
        {
            values[pair.Key] = pair.Value;
        }

        return new(2, values, JsonSerializer.Serialize(values));
    }
}
