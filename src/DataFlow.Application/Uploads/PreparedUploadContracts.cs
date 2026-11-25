using System.Text.Json.Serialization;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Enums;

namespace DataFlow.Application.Uploads;

public static class PreparedUploadNotice
{
    public const string SingleTable = "Один лист — одна таблица; первая строка — заголовки. Несколько таблиц на одном листе не поддерживаются.";
}

public sealed record PrepareUploadCommand(
    Guid DataSourceId,
    string IdempotencyKey,
    string FilePath,
    string FileName,
    string FileSha256,
    AcceptedFileFormat Format,
    long ContentLength) : ICommand<PreparedUploadResponse>;

public sealed record PreparedUploadResponse(
    string UploadToken,
    DateTimeOffset ExpiresAt,
    string OriginalFileName,
    string Format,
    IReadOnlyList<PreparedPartDto> Parts,
    string Notice = PreparedUploadNotice.SingleTable);

public sealed record PreparedPartDto(
    int PartIndex,
    string PartType,
    string SourceName,
    string? SheetName,
    int? SheetIndex,
    string? SheetVisibility,
    PartPreviewDto Preview);

public sealed record PartPreviewDto(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    int HeaderStartRow,
    int HeaderEndRow,
    bool PreviewTruncated);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InspectPreparedPartRequest
{
    [JsonRequired] public string UploadToken { get; init; } = string.Empty;
    [JsonRequired] public int PartIndex { get; init; }
    [JsonRequired] public Guid RuleSetVersionId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CommitPreparedUploadRequest
{
    [JsonRequired] public string UploadToken { get; init; } = string.Empty;
    [JsonRequired] public IReadOnlyList<CommitPreparedPartRequest> Parts { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CommitPreparedPartRequest
{
    [JsonRequired] public int PartIndex { get; init; }
    [JsonRequired] public bool Enabled { get; init; }
    public Guid? RuleSetVersionId { get; init; }
    public string? DeduplicationGroup { get; init; }
}

public sealed record InspectPreparedPartCommand(Guid DataSourceId, InspectPreparedPartRequest Request)
    : ICommand<PreparedPartDto>;

public sealed record CommitPreparedUploadCommand(Guid DataSourceId, string IdempotencyKey,
    CommitPreparedUploadRequest Request) : ICommand<JobAcceptedDto>;

public sealed record UploadContainerMetrics(long ArchiveBytes, int EntryCount, long TotalUncompressedBytes,
    long MaxEntryUncompressedBytes);

public sealed record UploadManifestInspection(IReadOnlyList<UploadManifestPart> Parts, UploadContainerMetrics Metrics);

public sealed record UploadManifest(int Version, IReadOnlyList<UploadManifestPart> Parts, UploadContainerMetrics? Container = null)
{
    public static UploadManifest Create(IReadOnlyList<UploadManifestPart> parts, UploadContainerMetrics? container = null) =>
        new(1, parts, container);
}

public sealed record UploadManifestCell(string Value, int RowNumber, int ColumnNumber);

public sealed record UploadManifestPart(
    int PartIndex,
    ImportPartType PartType,
    string SourceName,
    string? SheetName,
    int? SheetIndex,
    string? SheetVisibility,
    string SourceLocator,
    string? RawHeaderRecord,
    IReadOnlyList<string> RawPreviewRecords,
    IReadOnlyList<string> Headers,
    IReadOnlyList<UploadManifestCell> HeaderCells,
    IReadOnlyList<IReadOnlyList<string>> PreviewRows,
    int HeaderStartRow,
    int HeaderEndRow,
    bool PreviewTruncated)
{
    public PreparedPartDto ToDto() => new(PartIndex, PartType.ToString(), SourceName, SheetName, SheetIndex,
        SheetVisibility, new PartPreviewDto(Headers, PreviewRows, HeaderStartRow, HeaderEndRow, PreviewTruncated));
}
