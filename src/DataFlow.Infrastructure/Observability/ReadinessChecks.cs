using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DataFlow.Infrastructure.Observability;

public sealed class DatabaseReadiness(DataFlowDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken) ||
                (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) { return HealthCheckResult.Unhealthy(); }
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    }
}

public sealed class StorageReadiness(IAmazonS3 s3, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = options.Value.Bucket }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    }
}

public sealed class BrokerReadiness(IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await BrokerConnection.CreateAsync(options.Value, cancellationToken);
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    }
}

public sealed class WorkerReadiness : IHealthCheck
{
    private int _connected;
    public void SetConnected(bool connected) => Interlocked.Exchange(ref _connected, connected ? 1 : 0);
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Volatile.Read(ref _connected) == 1 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
}

internal static class BrokerConnection
{
    public static Task<IConnection> CreateAsync(RabbitMqOptions options, CancellationToken ct) => new ConnectionFactory
    {
        HostName = options.Host,
        Port = options.Port,
        VirtualHost = options.VirtualHost,
        UserName = options.User,
        Password = options.Password,
        AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
        ContinuationTimeout = TimeSpan.FromSeconds(3)
    }.CreateConnectionAsync(ct);
}
