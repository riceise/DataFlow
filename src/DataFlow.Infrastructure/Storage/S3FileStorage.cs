using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Storage;

public sealed class S3FileStorage(IAmazonS3 client, IOptions<StorageOptions> options, ILogger<S3FileStorage> logger) : IFileStorage
{
    public async Task UploadAsync(string objectKey, string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Value.Bucket,
            Key = objectKey,
            InputStream = stream,
            // Bucket must be private. No public ACL is ever requested (also supports bucket-owner-enforced S3).
            ContentType = "application/octet-stream",
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(options.Value.Bucket, objectKey, cancellationToken);

    public async Task<bool> TryDeleteAsync(string objectKey, Guid dataSourceId, CancellationToken cancellationToken)
    {
        try { await DeleteAsync(objectKey, cancellationToken); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError("Storage compensation failed for {ObjectKey}, source {DataSourceId}, error type {ErrorType}",
                objectKey, dataSourceId, exception.GetType().Name);
            return false;
        }
    }
}

public static class StorageExtensions
{
    public static IServiceCollection AddDataFlowStorage(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            var config = new AmazonS3Config { AuthenticationRegion = options.Region };
            if (string.IsNullOrEmpty(options.Endpoint))
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }
            else
            {
                config.ServiceURL = options.Endpoint.Contains("://", StringComparison.Ordinal) ? options.Endpoint
                    : $"{(options.UseSsl ? "https" : "http")}://{options.Endpoint}";
                config.ForcePathStyle = true;
            }
            return string.IsNullOrEmpty(options.AccessKey) ? new AmazonS3Client(config)
                : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        });
        services.AddScoped<IFileStorage, S3FileStorage>();
        services.AddScoped<IRequestHandler<UploadFileCommand, JobAcceptedDto>, UploadFileHandler>();
        services.AddHostedService<OrphanCleanupService>();
        return services;
    }
}
