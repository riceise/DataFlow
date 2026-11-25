using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class PreparedStorageMinioStage15Tests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    [Fact]
    public async Task MetadataNativeChecksumAndNoOverwrite_WorkAgainstPinnedMinio()
    {
        var bucket = $"prepared-{Guid.NewGuid():N}";
        using var client = Client();
        await client.PutBucketAsync(bucket);
        var options = Options.Create(new StorageOptions
        {
            Endpoint = minio.Endpoint,
            Bucket = bucket,
            AccessKey = "dataflow_test",
            SecretKey = "dataflow_test_password",
            Region = "us-east-1"
        });
        var storage = new S3FileStorage(client, options, NullLogger<S3FileStorage>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"stage15-{Guid.NewGuid():N}");
        var bytes = RandomNumberGenerator.GetBytes(128 * 1024);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            await ((IPreparedFileStorage)storage).UploadAsync("sources/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/2026/11/25/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                path, hash, CancellationToken.None);
            const string key = "sources/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/2026/11/25/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var metadata = await storage.HeadAsync(key, false, CancellationToken.None);
            Assert.Equal(bytes.LongLength, metadata.ContentLength);
            Assert.Equal(hash, metadata.Sha256Metadata);
            Assert.Null(metadata.NativeSha256);
            var native = await storage.HeadAsync(key, true, CancellationToken.None);
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), native.NativeSha256);
            await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => ((IPreparedFileStorage)storage).UploadAsync(key, path, hash,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
            await client.DeleteObjectAsync(bucket, "sources/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/2026/11/25/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            await client.DeleteBucketAsync(bucket);
        }
    }

    [Theory]
    [InlineData(PreparedUploadIntegrityMode.Metadata)]
    [InlineData(PreparedUploadIntegrityMode.Native)]
    [InlineData(PreparedUploadIntegrityMode.Strict)]
    public async Task IntegrityVerifier_AcceptsValidObjectAndRejectsChangedBytes(PreparedUploadIntegrityMode mode)
    {
        var bucket = $"prepared-{Guid.NewGuid():N}";
        using var client = Client();
        await client.PutBucketAsync(bucket);
        var storageOptions = Options.Create(new StorageOptions
        {
            Endpoint = minio.Endpoint,
            Bucket = bucket,
            AccessKey = "dataflow_test",
            SecretKey = "dataflow_test_password",
            Region = "us-east-1"
        });
        var storage = new S3FileStorage(client, storageOptions, NullLogger<S3FileStorage>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"stage15-{Guid.NewGuid():N}");
        var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        const string key = "sources/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/2026/11/25/cccccccccccccccccccccccccccccccc";
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            await ((IPreparedFileStorage)storage).UploadAsync(key, path, hash, CancellationToken.None);
            var prepared = PreparedUpload.CreateReady(Guid.NewGuid(), Guid.NewGuid(), "key", new string('a', 64), key,
                "input.csv", hash, AcceptedFileFormat.Csv, bytes.LongLength, new string('b', 64), [1], "v1", "{}", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(30));
            var verifier = new PreparedUploadIntegrityVerifier(storage, Options.Create(new ImportProcessingOptions
            {
                PreparedUploadIntegrity = mode
            }));
            await verifier.VerifyAsync(prepared, CancellationToken.None);
            bytes[0] ^= 1;
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = new MemoryStream(bytes),
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = Convert.ToBase64String(SHA256.HashData(bytes))
            });
            await Assert.ThrowsAsync<DataFlow.Domain.Exceptions.DomainException>(() => verifier.VerifyAsync(prepared,
                CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
            await client.DeleteObjectAsync(bucket, key);
            await client.DeleteBucketAsync(bucket);
        }
    }

    private AmazonS3Client Client() => new(new BasicAWSCredentials("dataflow_test", "dataflow_test_password"),
        new AmazonS3Config { ServiceURL = minio.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
}
