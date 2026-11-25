using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Application.Uploads;

public static partial class DeduplicationScopes
{
    public static string Create(DataSourceDataType dataType, int partIndex, string? explicitGroup)
    {
        if (dataType == DataSourceDataType.Customer)
        {
            if (explicitGroup is not null)
            {
                throw new DomainException("deduplication_group_not_allowed", "Customer parts always use the job-wide deduplication scope.");
            }

            return "customer:job";
        }

        if (explicitGroup is null)
        {
            return $"part:{partIndex}";
        }

        var normalized = explicitGroup.Trim();
        if (!GroupRegex().IsMatch(normalized))
        {
            throw new DomainException("invalid_deduplication_group",
                "Deduplication group must contain 1-64 ASCII letters, digits, dots, underscores or hyphens and start with a letter or digit.");
        }

        return "tabular-group:" + normalized.ToLowerInvariant();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupRegex();
}

public static class UploadFingerprints
{
    public static string Prepare(Guid userId, Guid sourceId, string fileSha256, AcceptedFileFormat format)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 3);
            writer.WriteString("operation", "prepare");
            writer.WriteString("userId", userId.ToString("N"));
            writer.WriteString("sourceId", sourceId.ToString("N"));
            writer.WriteString("fileSha256", fileSha256.ToLowerInvariant());
            writer.WriteString("format", PublicFormat(format));
            writer.WriteEndObject();
        }

        return Hash(stream);
    }

    public static string Commit(Guid userId, Guid sourceId, Guid preparedUploadId, string fileSha256,
        AcceptedFileFormat format, IReadOnlyList<CommitFingerprintPart> parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 3);
            writer.WriteString("operation", "commit");
            writer.WriteString("userId", userId.ToString("N"));
            writer.WriteString("sourceId", sourceId.ToString("N"));
            writer.WriteString("preparedUploadId", preparedUploadId.ToString("N"));
            writer.WriteString("fileSha256", fileSha256.ToLowerInvariant());
            writer.WriteString("format", PublicFormat(format));
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            foreach (var part in parts.OrderBy(part => part.PartIndex))
            {
                writer.WriteStartObject();
                writer.WriteNumber("partIndex", part.PartIndex);
                writer.WriteBoolean("enabled", part.Enabled);
                if (part.RuleSetVersionId is { } versionId)
                {
                    writer.WriteString("ruleSetVersionId", versionId.ToString("N"));
                }
                else
                {
                    writer.WriteNull("ruleSetVersionId");
                }

                if (part.DeduplicationScope is { } scope)
                {
                    writer.WriteString("deduplicationScope", scope);
                }
                else
                {
                    writer.WriteNull("deduplicationScope");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Hash(stream);
    }

    public static string PublicFormat(AcceptedFileFormat format) => format switch
    {
        AcceptedFileFormat.Csv => "Csv",
        AcceptedFileFormat.Xlsx => "Xlsx",
        AcceptedFileFormat.Zip => "ZipCsv",
        _ => throw new DomainException("file_format_not_allowed", "Only CSV, XLSX and ZIP CSV are supported.")
    };

    private static string Hash(MemoryStream stream) => Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
}

public sealed record CommitFingerprintPart(int PartIndex, bool Enabled, Guid? RuleSetVersionId, string? DeduplicationScope);
