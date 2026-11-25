using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class ImportProcessingOptions
{
    public const string SectionName = "Import";

    [Range(1024, 104_857_600, ErrorMessage = "Import:MaxFileBytes must be between 1 KiB and 100 MiB.")]
    public long MaxFileBytes { get; init; } = 26_214_400;

    [Range(1, 500_000, ErrorMessage = "Import:MaxRows must be between 1 and 500000.")]
    public int MaxRows { get; init; } = 50_000;

    [Range(1, 256, ErrorMessage = "Import:MaxColumns must be between 1 and 256.")]
    public int MaxColumns { get; init; } = 64;

    [Range(1, 65_536, ErrorMessage = "Import:MaxCellLength must be between 1 and 65536.")]
    public int MaxCellLength { get; init; } = 4_096;

    [Range(1, 10_000, ErrorMessage = "Import:MaxZipEntries must be between 1 and 10000.")]
    public int MaxZipEntries { get; init; } = 128;

    [Range(1024, 536_870_912, ErrorMessage = "Import:MaxUncompressedBytes must be between 1 KiB and 512 MiB.")]
    public long MaxUncompressedBytes { get; init; } = 104_857_600;

    [Range(250, 500, ErrorMessage = "Import:BatchSize must be between 250 and 500.")]
    public int BatchSize { get; init; } = 500;

    [Range(15, 600, ErrorMessage = "Import:LeaseSeconds must be between 15 and 600.")]
    public int LeaseSeconds { get; init; } = 60;

    [Range(1, 60)]
    public int HeartbeatSeconds { get; init; } = 15;

    [Range(60, 1200)]
    public int DeadlineSeconds { get; init; } = 1200;

    [Range(1, 200)]
    public int MaxPartsPerJob { get; init; } = 50;

    [Range(1, 200)]
    public int MaxSheetsPerWorkbook { get; init; } = 50;

    [Range(1, 200)]
    public int MaxCsvFilesPerArchive { get; init; } = 50;

    [Range(1, 100)]
    public int PreviewRows { get; init; } = 20;

    [Range(300, 86400)]
    public int PreparedUploadTtlSeconds { get; init; } = 1800;

    [Range(1024, 1048576)]
    public int MaxPreviewRawBytesPerPart { get; init; } = 1048576;

    [Range(1024, 8388608)]
    public int MaxPreviewRawBytesPerJob { get; init; } = 8388608;

    public PreparedUploadIntegrityMode PreparedUploadIntegrity { get; init; } = PreparedUploadIntegrityMode.Metadata;

    public ZipContainerOptions Zip { get; init; } = new();

    public XlsxContainerOptions Xlsx { get; init; } = new();
}

public enum PreparedUploadIntegrityMode
{
    Metadata = 1,
    Native = 2,
    Strict = 3
}

public sealed class ZipContainerOptions
{
    [Range(1, 10000)]
    public int MaxEntries { get; init; } = 128;
    [Range(1, 200)]
    public int MaxCsvFiles { get; init; } = 50;
    [Range(1024, 104857600)]
    public long MaxArchiveBytes { get; init; } = 50 * 1024 * 1024;
    [Range(1024, 104857600)]
    public long MaxEntryBytes { get; init; } = 50 * 1024 * 1024;
    [Range(1024, 536870912)]
    public long MaxTotalUncompressedBytes { get; init; } = 100 * 1024 * 1024;
    [Range(1, 1024)]
    public int MaxFileNameLength { get; init; } = 255;
}

public sealed class XlsxContainerOptions
{
    [Range(0, 2000000)]
    public int MaxSharedStrings { get; init; } = 250000;
    [Range(0, 67108864)]
    public int MaxSharedStringChars { get; init; } = 8388608;
}
