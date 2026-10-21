using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Tests;

public sealed class DataSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StoresSchemaAsVersionOne()
    {
        var source = NewSource();

        Assert.Equal(1, source.SchemaVersion);
        Assert.Equal(NewSchema(), source.GetParsedSchema());
        Assert.Equal(source.SchemaVersion, source.GetParsedSchema().Version);
    }

    [Fact]
    public void Update_WithStructurallyIdenticalSchema_KeepsVersion()
    {
        var source = NewSource();

        source.Update("Customers", AcceptedFileFormat.All, NewSchema(), Now.AddMinutes(5));

        Assert.Equal(1, source.SchemaVersion);
        Assert.Equal(NewSchema(), source.GetParsedSchema());
        Assert.Equal(source.SchemaVersion, source.GetParsedSchema().Version);
    }

    [Fact]
    public void Update_WithChangedSchema_IncrementsVersion()
    {
        var source = NewSource();

        source.Update("Customers", AcceptedFileFormat.All, NewSchema(fields:
        [
            new DataSourceFieldDefinition("externalId", "string", true, 200)
        ]), Now.AddMinutes(5));

        Assert.Equal(2, source.SchemaVersion);
        Assert.Equal(2, source.GetParsedSchema().Version);
        Assert.Equal(200, source.GetParsedSchema().Fields.Single().MaxLength);
    }

    [Fact]
    public void Update_WithChangedDelimiter_IncrementsVersion()
    {
        var source = NewSource();

        source.Update("Customers", AcceptedFileFormat.All, NewSchema(delimiter: ";"), Now.AddMinutes(5));

        Assert.Equal(2, source.SchemaVersion);
        Assert.Equal(";", source.GetParsedSchema().CsvDelimiter);
        Assert.Equal(source.SchemaVersion, source.GetParsedSchema().Version);
    }

    private static DataSource NewSource() =>
        DataSource.Create("Customers", DataSourceDataType.Customer, AcceptedFileFormat.All, NewSchema(), Now);

    private static DataSourceSchema NewSchema(
        IReadOnlyList<DataSourceFieldDefinition>? fields = null,
        string delimiter = ",") =>
        new(
            1,
            ["externalId"],
            fields ?? [new DataSourceFieldDefinition("externalId", "string", true, 100)],
            delimiter);
}
