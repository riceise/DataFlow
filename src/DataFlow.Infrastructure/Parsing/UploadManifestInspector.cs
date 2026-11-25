using DataFlow.Application.Uploads;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class UploadManifestInspector(CsvUploadInspector csv, XlsxUploadInspector xlsx, ZipCsvUploadInspector zip,
    IOptions<ImportProcessingOptions> options)
{
    public async Task<UploadManifest> InspectAsync(string filePath, string sourceName, AcceptedFileFormat format,
        DataSourceSchema preliminarySchema, CancellationToken cancellationToken)
    {
        var budget = new PreviewBudget(options.Value.MaxPreviewRawBytesPerJob);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        UploadManifestInspection inspection;
        if (format == AcceptedFileFormat.Csv)
        {
            var part = await csv.InspectAsync(stream, sourceName, 0, preliminarySchema, budget,
                cancellationToken: cancellationToken);
            inspection = new UploadManifestInspection([part], new UploadContainerMetrics(stream.Length, 1, stream.Length, stream.Length));
        }
        else if (format == AcceptedFileFormat.Xlsx)
        {
            inspection = await xlsx.InspectAsync(stream, sourceName, budget, cancellationToken);
        }
        else if (format == AcceptedFileFormat.Zip)
        {
            inspection = await zip.InspectAsync(stream, preliminarySchema, budget, cancellationToken);
        }
        else
        {
            throw new Domain.Exceptions.DomainException("file_format_not_allowed", "Only CSV, XLSX and ZIP CSV are supported.");
        }

        var parts = inspection.Parts;
        if (parts.Count == 0 || parts.Count > options.Value.MaxPartsPerJob)
        {
            throw new Application.Imports.ImportFileException("parts_limit_exceeded", "The upload does not contain a supported number of parts.", 0);
        }

        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index].PartIndex != index)
            {
                throw new Application.Imports.ImportFileException("invalid_manifest", "The server manifest has an invalid part order.", 0);
            }
        }

        return UploadManifest.Create(parts, inspection.Metrics);
    }
}
