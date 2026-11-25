using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Storage;

public sealed class PreparedUploadIntegrityVerifier(IPreparedFileStorage storage, IOptions<ImportProcessingOptions> options)
{
    public async Task VerifyAsync(PreparedUpload upload, CancellationToken cancellationToken)
    {
        if (upload.StorageKey is not { } key)
        {
            throw Error("prepared_upload_object_missing");
        }

        try
        {
            if (options.Value.PreparedUploadIntegrity == PreparedUploadIntegrityMode.Strict)
            {
                await VerifyFullStreamAsync(upload, key, cancellationToken);
                return;
            }

            var metadata = await storage.HeadAsync(key,
                options.Value.PreparedUploadIntegrity == PreparedUploadIntegrityMode.Native, cancellationToken);
            if (metadata.ContentLength != upload.ContentLength)
            {
                throw Error("prepared_upload_integrity_mismatch");
            }

            if (options.Value.PreparedUploadIntegrity == PreparedUploadIntegrityMode.Native)
            {
                var expected = Convert.ToBase64String(Convert.FromHexString(upload.FileSha256));
                if (!string.Equals(metadata.NativeSha256, expected, StringComparison.Ordinal))
                {
                    throw Error("prepared_upload_integrity_mismatch");
                }
            }
            else if (!string.Equals(metadata.Sha256Metadata, upload.StorageChecksumMetadata, StringComparison.Ordinal))
            {
                throw Error("prepared_upload_integrity_mismatch");
            }
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw Error("prepared_upload_object_missing");
        }
    }

    private async Task VerifyFullStreamAsync(PreparedUpload upload, string key, CancellationToken cancellationToken)
    {
        await using var stream = await storage.OpenReadAsync(key, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            length = checked(length + read);
            if (length > upload.ContentLength)
            {
                throw Error("prepared_upload_integrity_mismatch");
            }

            hash.AppendData(buffer, 0, read);
        }

        if (length != upload.ContentLength ||
            !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), upload.FileSha256, StringComparison.Ordinal))
        {
            throw Error("prepared_upload_integrity_mismatch");
        }
    }

    private static DomainException Error(string code) => new(code, "Prepared upload object failed its integrity check.");
}
