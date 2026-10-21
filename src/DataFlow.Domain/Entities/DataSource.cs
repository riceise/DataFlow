using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.Time;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Entities;

public sealed class DataSource
{
    private DataSource()
    {
        Name = string.Empty;
        SchemaJson = string.Empty;
    }

    private DataSource(
        Guid id,
        string name,
        DataSourceDataType dataType,
        AcceptedFileFormat acceptedFormats,
        string schemaJson,
        int schemaVersion,
        bool isActive,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        DataType = dataType;
        AcceptedFormats = acceptedFormats;
        SchemaJson = schemaJson;
        SchemaVersion = schemaVersion;
        IsActive = isActive;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public DataSourceDataType DataType { get; private set; }
    public AcceptedFileFormat AcceptedFormats { get; private set; }
    public string SchemaJson { get; private set; }
    public int SchemaVersion { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static DataSource Create(
        string name,
        DataSourceDataType dataType,
        AcceptedFileFormat acceptedFormats,
        DataSourceSchema schema,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("invalid_source_name", "DataSource name is required.");
        }

        if (acceptedFormats == AcceptedFileFormat.None)
        {
            throw new DomainException("invalid_accepted_formats", "At least one accepted file format must be specified.");
        }

        ArgumentNullException.ThrowIfNull(schema);

        var normalizedSchema = schema.WithVersion(1);

        var utcNow = PersistenceTime.ToStoragePrecision(nowUtc);

        return new DataSource(
            Guid.NewGuid(),
            name.Trim(),
            dataType,
            acceptedFormats,
            normalizedSchema.ToJson(),
            schemaVersion: 1,
            isActive: true,
            utcNow);
    }

    public void Update(
        string name,
        AcceptedFileFormat acceptedFormats,
        DataSourceSchema newSchema,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("invalid_source_name", "DataSource name is required.");
        }

        if (acceptedFormats == AcceptedFileFormat.None)
        {
            throw new DomainException("invalid_accepted_formats", "At least one accepted file format must be specified.");
        }

        ArgumentNullException.ThrowIfNull(newSchema);

        Name = name.Trim();
        AcceptedFormats = acceptedFormats;

        var currentCanonical = DataSourceSchema.FromJson(SchemaJson).WithVersion(1);
        var candidateCanonical = newSchema.WithVersion(1);

        if (currentCanonical != candidateCanonical)
        {
            SchemaVersion += 1;
        }

        SchemaJson = newSchema.WithVersion(SchemaVersion).ToJson();

        UpdatedAt = PersistenceTime.ToStoragePrecision(nowUtc);
    }

    public void Activate(DateTimeOffset nowUtc)
    {
        IsActive = true;
        UpdatedAt = PersistenceTime.ToStoragePrecision(nowUtc);
    }

    public void Deactivate(DateTimeOffset nowUtc)
    {
        IsActive = false;
        UpdatedAt = PersistenceTime.ToStoragePrecision(nowUtc);
    }

    public DataSourceSchema GetParsedSchema() => DataSourceSchema.FromJson(SchemaJson);
}
