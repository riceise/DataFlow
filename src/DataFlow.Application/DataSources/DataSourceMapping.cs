using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.DataSources;

internal static class DataSourceMapping
{
    public static AcceptedFileFormat ToFormats(this SaveDataSourceRequest request) => request.AcceptedFormats!
        .Aggregate(AcceptedFileFormat.None, (formats, format) => formats | (format == "Csv" ? AcceptedFileFormat.Csv : AcceptedFileFormat.Xlsx));

    public static DataSourceSchema ToSchema(this SaveDataSourceRequest request)
    {
        var schema = request.Schema!;
        var fields = schema.Fields!
            .OrderBy(field => Array.IndexOf(SaveDataSourceRequestValidator.FieldOrder, field!.Name))
            .Select(field => new DataSourceFieldDefinition(field!.Name!, field.Type!, field.Required!.Value, field.MaxLength, field.Format))
            .ToArray();
        return new DataSourceSchema(1, ["externalId"], fields, schema.CsvDelimiter!);
    }

    public static DataSourceDto ToDto(this DataSource source)
    {
        var schema = source.GetParsedSchema();
        var formats = new List<string>();
        if (source.AcceptedFormats.HasFlag(AcceptedFileFormat.Csv))
        {
            formats.Add("Csv");
        }

        if (source.AcceptedFormats.HasFlag(AcceptedFileFormat.Xlsx))
        {
            formats.Add("Xlsx");
        }

        return new DataSourceDto(source.Id, source.Name, source.DataType.ToString(), formats,
            new DataSourceSchemaDto(schema.Version, schema.KeyFields, schema.CsvDelimiter,
                schema.Fields.Select(field => new DataSourceFieldDto(field.Name, field.Type, field.Required, field.MaxLength, field.Format)).ToArray()),
            source.SchemaVersion, source.IsActive, source.CreatedAt, source.UpdatedAt);
    }
}
