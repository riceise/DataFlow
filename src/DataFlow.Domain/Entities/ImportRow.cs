using System.Text.Json;
using System.Text.Json.Serialization;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Entities;

public sealed class ImportRow
{
    private ImportRow()
    {
        NaturalKey = string.Empty;
    }

    private ImportRow(
        Guid importJobId,
        int rowNumber,
        ImportRowStatus status,
        string? rawData,
        string? naturalKey,
        string? errorsJson,
        DateTimeOffset createdAtUtc,
        string? rowHash)
    {
        Id = Guid.NewGuid();
        ImportJobId = importJobId;
        RowNumber = rowNumber;
        Status = status;
        RawData = rawData;
        NaturalKey = naturalKey;
        ErrorsJson = errorsJson;
        RowHash = rowHash;
        CreatedAt = createdAtUtc;
        ProcessedAt = createdAtUtc;
    }

    private static readonly JsonSerializerOptions RawSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public Guid Id { get; private set; }
    public Guid ImportJobId { get; private set; }
    public int RowNumber { get; private set; }
    public ImportRowStatus Status { get; private set; }
    public string? RawData { get; private set; }
    public string? NaturalKey { get; private set; }
    public string? ErrorsJson { get; private set; }
    public string? RowHash { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    public IReadOnlyList<ImportRowError> GetErrors()
    {
        if (string.IsNullOrWhiteSpace(ErrorsJson))
        {
            return Array.Empty<ImportRowError>();
        }

        return JsonSerializer.Deserialize<List<ImportRowError>>(ErrorsJson, RawSerializerOptions)
            ?? new List<ImportRowError>();
    }

    public static ImportRow Create(
        Guid importJobId,
        int rowNumber,
        ImportRowStatus status,
        string? rawData,
        string? naturalKey,
        IReadOnlyList<ImportRowError>? errors,
        DateTimeOffset createdAtUtc,
        string? rowHash = null)
    {
        if (importJobId == Guid.Empty || rowNumber <= 0 || !Enum.IsDefined(status))
        {
            throw new ArgumentException("Import row requires a job id, positive row number and valid status.");
        }

        if (status == ImportRowStatus.Invalid && (string.IsNullOrWhiteSpace(rawData) || errors is not { Count: > 0 }))
        {
            throw new ArgumentException("Invalid rows require raw data and errors.");
        }

        var normalizedRaw = string.IsNullOrWhiteSpace(rawData) ? null : rawData.Trim();
        var normalizedNaturalKey = string.IsNullOrWhiteSpace(naturalKey) ? null : naturalKey;

        string? errorsJson = null;
        if (errors is { Count: > 0 })
        {
            errorsJson = JsonSerializer.Serialize(errors, RawSerializerOptions);
        }

        return new ImportRow(
            importJobId,
            rowNumber,
            status,
            normalizedRaw,
            normalizedNaturalKey,
            errorsJson,
            createdAtUtc.ToUniversalTime(),
            rowHash);
    }
}
