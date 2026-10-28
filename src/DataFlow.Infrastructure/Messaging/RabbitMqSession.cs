using DataFlow.Infrastructure.Configuration;
using RabbitMQ.Client;

namespace DataFlow.Infrastructure.Messaging;

public sealed class RabbitMqSession : IAsyncDisposable
{
    public const string Exchange = "dataflow.imports";
    public const string Queue = "imports.process";
    public const string RoutingKey = "import.job.created";
    private readonly IConnection _connection;

    private RabbitMqSession(IConnection connection, IChannel channel)
    {
        _connection = connection;
        Channel = channel;
    }

    public IChannel Channel { get; }

    public static async Task<RabbitMqSession> ConnectAsync(RabbitMqOptions options, bool publisher, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.User,
            Password = options.Password,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            ContinuationTimeout = TimeSpan.FromSeconds(5),
            ConsumerDispatchConcurrency = 1
        };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        IChannel? channel = null;
        try
        {
            channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: publisher, publisherConfirmationTrackingEnabled: publisher),
                cancellationToken);
            await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: cancellationToken);
            return new RabbitMqSession(connection, channel);
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Channel.DisposeAsync(); }
        finally { await _connection.DisposeAsync(); }
    }
}
