using System.Security.Cryptography;
using System.Text;
using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DataFlow.Api.Endpoints;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/data-sources/{id:guid}/files", UploadAsync)
            .WithTags("Files").Produces<JobAcceptedDto>(202).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        endpoints.MapGet("/api/v1/data-sources/{id:guid}/jobs", async (Guid id, IDispatcher dispatcher,
            CancellationToken ct, int page = 1, int pageSize = 50, string? status = null) =>
            Results.Ok(await dispatcher.DispatchAsync<ListJobsQuery, PagedResult<JobDto>>(new(id, page, pageSize, status), ct)));
        endpoints.MapGet("/api/v1/jobs/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            Results.Ok(await dispatcher.DispatchAsync<GetJobQuery, JobDto>(new(id), ct)));
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpContext context, IDispatcher dispatcher, IOptions<StorageOptions> options)
    {
        var ct = context.RequestAborted;
        var header = context.Request.Headers["Idempotency-Key"];
        if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || header[0]!.Length > 128)
        {
            throw Error("invalid_idempotency_key");
        }

        var source = await dispatcher.DispatchAsync<GetDataSourceQuery, DataSourceDto>(new(id), ct);
        var settings = options.Value;
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = null;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || !media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("invalid_multipart");
        }

        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
        {
            throw Error("invalid_multipart");
        }

        Directory.CreateDirectory(settings.TempDirectory);
        var temp = Path.Combine(settings.TempDirectory, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var receiving = true;
        try
        {
            var reader = new MultipartReader(boundary, context.Request.Body);
            string? fileName = null;
            string? hash = null;
            AcceptedFileFormat format = AcceptedFileFormat.None;
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw Error("invalid_multipart");
                }
                // Exactly one file; form fields and additional files are rejected without unbounded draining.
                if (fileName is not null || (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
                {
                    throw Error("unexpected_form_field");
                }

                fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                fileName = fileName?.Replace('\\', '/').Split('/').Last();
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
                {
                    throw Error("invalid_file_name");
                }

                format = Path.GetExtension(fileName).ToLowerInvariant() switch
                {
                    ".csv" => AcceptedFileFormat.Csv,
                    ".xlsx" => AcceptedFileFormat.Xlsx,
                    ".pdf" => AcceptedFileFormat.Pdf,
                    ".zip" => AcceptedFileFormat.Zip,
                    _ => AcceptedFileFormat.None
                };
                if (format == AcceptedFileFormat.None || !source.AcceptedFormats.Contains(format.ToString(), StringComparer.Ordinal))
                {
                    throw Error("file_format_not_allowed");
                }

                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                var prefix = new byte[4096];
                var prefixLength = 0;
                long length = 0;
                int read;
                while ((read = await section.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Math.Min(buffer.Length, settings.MaxFileBytes - length) + 1)), ct)) > 0)
                {
                    length += read;
                    if (length > settings.MaxFileBytes)
                    {
                        throw Error("file_too_large");
                    }

                    var copy = Math.Min(read, prefix.Length - prefixLength);
                    buffer.AsSpan(0, copy).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += copy;
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (length == 0)
                {
                    throw Error("empty_file");
                }

                if (!ValidSignature(format, prefix.AsSpan(0, prefixLength)))
                {
                    throw Error("invalid_file_format");
                }

                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (fileName is null || hash is null)
            {
                throw Error("empty_file");
            }

            ct.ThrowIfCancellationRequested();
            receiving = false;
            var result = await dispatcher.DispatchAsync<UploadFileCommand, JobAcceptedDto>(new(id, header[0]!, temp, fileName, hash, format), ct);
            return Results.Accepted($"/api/v1/jobs/{result.JobId}", result);
        }
        catch (InvalidDataException) { throw Error("invalid_multipart"); }
        catch (IOException) when (receiving && !ct.IsCancellationRequested) { throw Error("invalid_multipart"); }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool ValidSignature(AcceptedFileFormat format, ReadOnlySpan<byte> bytes)
    {
        if (format == AcceptedFileFormat.Pdf)
        {
            return bytes.StartsWith("%PDF-"u8);
        }

        if (format is AcceptedFileFormat.Zip or AcceptedFileFormat.Xlsx)
        {
            return bytes.StartsWith("PK\x03\x04"u8) || bytes.StartsWith("PK\x05\x06"u8) || bytes.StartsWith("PK\x07\x08"u8);
        }
        // CSV sniffing only establishes non-binary UTF-8 text, not a control-character policy or CSV syntax.
        if (bytes.Contains((byte)0) || bytes.StartsWith("%PDF-"u8) || bytes.StartsWith("PK\x03\x04"u8))
        {
            return false;
        }

        try
        {
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            Span<char> chars = stackalloc char[4096];
            decoder.Convert(bytes, chars, false, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static DomainException Error(string code) => new(code, "File upload request is invalid.");
}
