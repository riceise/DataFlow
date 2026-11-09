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
}
