using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Tests;

public sealed class DataSourceSchemaTests
{
    [Fact]
    public void IdenticalContent_ProducesEqualSchemas()
    {
        var first = NewSchema();
        var second = NewSchema();

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void DifferentContent_ProducesUnequalSchemas()
    {
        var baseline = NewSchema();

        Assert.NotEqual(baseline, NewSchema(version: 2));
        Assert.NotEqual(baseline, NewSchema(delimiter: ";"));
        Assert.NotEqual(baseline, NewSchema(fields: [new DataSourceFieldDefinition("externalId", "string", true, 200)]));
        Assert.NotEqual(baseline, NewSchema(fields: [new DataSourceFieldDefinition("externalId", "string", false, 100)]));
        Assert.NotEqual(baseline, NewSchema(fields:
        [
            new DataSourceFieldDefinition("externalId", "string", true, 100),
            new DataSourceFieldDefinition("customerId", "string", true, 100)
        ]));
    }

    [Fact]
    public void DifferentKeyFields_ProduceUnequalSchemas()
    {
        var fields = new[]
        {
            new DataSourceFieldDefinition("externalId", "string", true, 100),
            new DataSourceFieldDefinition("customerId", "string", true, 100)
        };

        var byExternalId = NewSchema(keyFields: ["externalId"], fields: fields);
        var byCustomerId = NewSchema(keyFields: ["customerId"], fields: fields);

        Assert.NotEqual(byExternalId, byCustomerId);
    }

    [Fact]
    public void FieldOrder_IsSignificant()
    {
        var ordered = NewSchema(fields:
        [
            new DataSourceFieldDefinition("externalId", "string", true, 100),
            new DataSourceFieldDefinition("email", "email", true)
        ]);

        var reordered = NewSchema(fields:
        [
            new DataSourceFieldDefinition("email", "email", true),
            new DataSourceFieldDefinition("externalId", "string", true, 100)
        ]);

        Assert.NotEqual(ordered, reordered);
    }

    [Fact]
    public void JsonRoundTrip_PreservesEquality()
    {
        var schema = NewSchema();

        Assert.Equal(schema, DataSourceSchema.FromJson(schema.ToJson()));
    }

    [Fact]
    public void NullFieldDefinition_IsRejected()
    {
        var code = Assert.Throws<DomainException>(() => new DataSourceSchema(
            1,
            ["externalId"],
            [null!])).Code;

        Assert.Equal("invalid_schema_field", code);
    }

    [Fact]
    public void Version_IsReadOnly()
    {
        var property = typeof(DataSourceSchema).GetProperty(nameof(DataSourceSchema.Version));

        Assert.NotNull(property);
        Assert.Null(property.SetMethod);
    }

    [Fact]
    public void WithVersion_ReturnsCopyWithNewVersionAndKeepsOriginalUntouched()
    {
        var schema = NewSchema();

        var updated = schema.WithVersion(2);

        Assert.Equal(1, schema.Version);
        Assert.Equal(2, updated.Version);
        Assert.Equal(schema.KeyFields, updated.KeyFields);
        Assert.Equal(schema.Fields, updated.Fields);
        Assert.Equal(schema.CsvDelimiter, updated.CsvDelimiter);
        Assert.NotEqual(schema, updated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithVersion_RejectsNonPositiveVersion(int version)
    {
        var code = Assert.Throws<DomainException>(() => NewSchema().WithVersion(version)).Code;

        Assert.Equal("invalid_schema_version", code);
    }

    [Fact]
    public void WithVersion_KeepsSchemaEqualityForSameVersion()
    {
        var schema = NewSchema();

        Assert.Equal(schema, schema.WithVersion(1));
    }

    private static DataSourceSchema NewSchema(
        int version = 1,
        IReadOnlyList<string>? keyFields = null,
        IReadOnlyList<DataSourceFieldDefinition>? fields = null,
        string delimiter = ",") =>
        new(
            version,
            keyFields ?? ["externalId"],
            fields ?? [new DataSourceFieldDefinition("externalId", "string", true, 100)],
            delimiter);
}
