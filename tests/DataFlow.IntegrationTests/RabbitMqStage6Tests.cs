using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace DataFlow.IntegrationTests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.0.5-alpine")
        .WithUsername("dataflow").WithPassword("dataflow_test_password").Build();

    public RabbitMqOptions Options => new()
    {
        Host = _rabbit.Hostname,
        Port = _rabbit.GetMappedPublicPort(5672),
        VirtualHost = "/",
        User = "dataflow",
        Password = "dataflow_test_password"
    };

    public Task InitializeAsync() => _rabbit.StartAsync();
    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();
}

public sealed class RabbitMqStage6Tests(DataSourcesApiFixture postgres, RabbitMqFixture rabbit) :
    IClassFixture<DataSourcesApiFixture>, IClassFixture<RabbitMqFixture>, IAsyncLifetime
{
    private readonly DeliveryLog _log = new();

    public async Task InitializeAsync()
    {
        await using var database = postgres.CreateContext();
        await database.ProcessedMessages.ExecuteDeleteAsync();
        await database.UploadRequests.ExecuteDeleteAsync();
        await database.OutboxMessages.ExecuteDeleteAsync();
        await database.ImportRows.ExecuteDeleteAsync();
        await database.ImportJobs.ExecuteDeleteAsync();
        await database.Customers.ExecuteDeleteAsync();
        await database.DataSources.ExecuteDeleteAsync();
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, false, CancellationToken.None);
        await session.Channel.QueuePurgeAsync(RabbitMqSession.Queue);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DispatcherPublishesPersistentMessage_DurableTopologyAndEnvelope()
    {
        var (job, message) = await SeedAsync();
        await using var observer = await RabbitMqSession.ConnectAsync(rabbit.Options, false, CancellationToken.None);
        // Passive declares check existence; AMQP declare-ok does not expose durable/arguments.
        // Equivalent active declares fail if durability or queue type differs.
        await observer.Channel.ExchangeDeclarePassiveAsync(RabbitMqSession.Exchange);
        await observer.Channel.QueueDeclarePassiveAsync(RabbitMqSession.Queue);
        await observer.Channel.ExchangeDeclareAsync(RabbitMqSession.Exchange, ExchangeType.Direct, true, false);
        await observer.Channel.QueueDeclareAsync(RabbitMqSession.Queue, true, false, false,
            new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
        var received = new ConcurrentQueue<(ImportMessageEnvelope Envelope, bool Persistent, DeliveryModes DeliveryMode)>();
        var consumer = new AsyncEventingBasicConsumer(observer.Channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            received.Enqueue((JsonSerializer.Deserialize<ImportMessageEnvelope>(delivery.Body.Span, ImportMessageEnvelope.JsonOptions)!,
                delivery.BasicProperties.Persistent, delivery.BasicProperties.DeliveryMode));
            await observer.Channel.BasicAckAsync(delivery.DeliveryTag, false);
        };
        var tag = await observer.Channel.BasicConsumeAsync(RabbitMqSession.Queue, false, consumer);
        using var host = CreateHost(dispatcher: true);
        await host.StartAsync();
        await WaitAsync(async () => !received.IsEmpty && (await ReadAsync(message.Id)).PublishedAt is not null);
        await host.StopAsync();
        await observer.Channel.BasicCancelAsync(tag);
        var delivered = Assert.Single(received);
        Assert.True(delivered.Persistent);
        Assert.Equal(DeliveryModes.Persistent, delivered.DeliveryMode);
        Assert.Equal("v1", delivered.Envelope.ContractVersion);
        Assert.Equal(message.Id, delivered.Envelope.MessageId);
        Assert.Equal("import.job.created", delivered.Envelope.Type);
        Assert.Equal(1, delivered.Envelope.Attempt);
        Assert.Equal(message.OccurredAt.ToUnixTimeMilliseconds(), delivered.Envelope.OccurredAt.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.Zero, delivered.Envelope.OccurredAt.Offset);
        Assert.NotNull(delivered.Envelope.TraceParent);
        var payload = delivered.Envelope.Payload.Deserialize<ImportJobCreatedPayload>(ImportMessageEnvelope.JsonOptions)!;
        Assert.Equal(job.Id, payload.JobId);
        Assert.Equal(job.DataSourceId, payload.DataSourceId);
        Assert.Equal(job.RunVersion, payload.RunVersion);
        Assert.Equal(job.SchemaVersion, payload.SchemaVersion);
        Assert.Equal(4, delivered.Envelope.Payload.EnumerateObject().Count());
        Assert.Null(await observer.Channel.BasicGetAsync(RabbitMqSession.Queue, true));
        var stored = await ReadAsync(message.Id);
        Assert.NotNull(stored.PublishedAt);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.Owner);
        Assert.Null(stored.LockedUntil);
    }

    [Fact]
    public async Task ExpiredClaimIsRetakenAndPublished()
    {
        var (_, message) = await SeedAsync();
        await using var database = postgres.CreateContext();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET owner = 'other-replica', locked_until = now() - interval '1 minute' WHERE id = {message.Id}
            """);
        using var host = CreateHost(dispatcher: true);
        await host.StartAsync();
        await WaitAsync(async () => (await ReadAsync(message.Id)).PublishedAt is not null);
        await host.StopAsync();
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, false, CancellationToken.None);
        Assert.NotNull(await session.Channel.BasicGetAsync(RabbitMqSession.Queue, true));
        Assert.Null((await ReadAsync(message.Id)).Owner);
    }

    [Fact]
    public async Task BrokerUnavailable_OutboxStaysUnpublishedAndAttemptsGrow()
    {
        var (_, message) = await SeedAsync();
        // Reserve an unlistened port for the duration of the outage test.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var unavailable = new RabbitMqOptions
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port,
            User = rabbit.Options.User,
            Password = rabbit.Options.Password
        };
        using var host = CreateHost(dispatcher: true, options: unavailable);
        await host.StartAsync();
        await WaitAsync(async () => (await ReadAsync(message.Id)).Attempts >= 2);
        await host.StopAsync();
        var stored = await ReadAsync(message.Id);
        Assert.Null(stored.PublishedAt);
        Assert.True(stored.Attempts >= 2);
        Assert.False(string.IsNullOrWhiteSpace(stored.LastError));
        Assert.DoesNotContain(rabbit.Options.Password, stored.LastError);
        Assert.Null(stored.Owner);
        Assert.Null(stored.LockedUntil);
        using var recovered = CreateHost(dispatcher: true);
        await recovered.StartAsync();
        await WaitAsync(async () => (await ReadAsync(message.Id)).PublishedAt is not null);
        await recovered.StopAsync();
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, false, CancellationToken.None);
        var delivery = await session.Channel.BasicGetAsync(RabbitMqSession.Queue, true);
        Assert.NotNull(delivery);
        var envelope = JsonSerializer.Deserialize<ImportMessageEnvelope>(delivery.Body.Span, ImportMessageEnvelope.JsonOptions)!;
        Assert.Equal(stored.Attempts + 1, envelope.Attempt);
    }

    [Fact]
    public async Task ConsumerWithoutBroker_HostStartsAndConnectionIsRetried()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new RabbitMqOptions
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port,
            User = rabbit.Options.User,
            Password = rabbit.Options.Password
        };
        using var host = CreateHost(dispatcher: false, options);
        using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await host.StartAsync(startupDeadline.Token);
        await WaitAsync(() => Task.FromResult(_log.Messages.Count(x => x.StartsWith("Import consumer unavailable", StringComparison.Ordinal)) >= 2));
        await host.StopAsync();
    }

    [Fact]
    public async Task DuplicateDeliveryIsGuarded()
    {
        var (job, message) = await SeedAsync();
        var envelope = Envelope(message);
        using var host = CreateHost(dispatcher: false);
        await host.StartAsync();
        await PublishAsync(envelope, envelope);
        await WaitAsync(() => Task.FromResult(_log.Acknowledged >= 2));
        await host.StopAsync();
        Assert.Single(_log.Messages, x => x.StartsWith("Previously processed", StringComparison.Ordinal));
        await using var database = postgres.CreateContext();
        var completed = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ImportJobStatus.Completed, completed.Status);
        Assert.Equal(1, completed.ProcessedRows);
        Assert.Equal(1, completed.AttemptCount);
        Assert.Single(await database.ImportRows.ToListAsync());
        Assert.Single(await database.Customers.ToListAsync());
        Assert.Single(await database.ProcessedMessages.ToListAsync());
        Assert.Equal(2, await database.OutboxMessages.CountAsync());
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task ProcessedMessageGuard_PrecedesPayloadAndJobValidation()
    {
        var (job, message) = await SeedAsync();
        await using (var database = postgres.CreateContext())
        {
            database.ProcessedMessages.Add(ProcessedMessage.Create(message.Id.ToString(), job.Id, job.RunVersion, DateTimeOffset.UtcNow));
            await database.SaveChangesAsync();
        }
        var envelope = Envelope(message) with { Payload = JsonSerializer.SerializeToElement(new { invalid = true }) };
        using var host = CreateHost(dispatcher: false);
        await host.StartAsync();
        await PublishAsync(envelope, envelope);
        await WaitAsync(() => Task.FromResult(_log.Acknowledged >= 2));
        await host.StopAsync();
        Assert.Equal(2, _log.Messages.Count(x => x.Contains("Previously processed", StringComparison.Ordinal)));
        await AssertUnchangedAsync(job.Id, expectedProcessed: 1);
        await AssertQueueEmptyAsync();
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("stale")]
    [InlineData("missing")]
    public async Task InvalidOrStaleDelivery_IsAcknowledgedWithoutEffects(string variant)
    {
        var (job, message) = await SeedAsync();
        var envelope = Envelope(message);
        envelope = variant switch
        {
            "unsupported" => envelope with { ContractVersion = "v99", Payload = JsonSerializer.SerializeToElement("invalid") },
            "stale" => envelope with { Payload = JsonSerializer.SerializeToElement(new ImportJobCreatedPayload(job.Id, job.DataSourceId, 1, job.RunVersion + 1), ImportMessageEnvelope.JsonOptions) },
            _ => envelope with { Payload = JsonSerializer.SerializeToElement(new ImportJobCreatedPayload(Guid.NewGuid(), job.DataSourceId, 1, 1), ImportMessageEnvelope.JsonOptions) }
        };
        using var host = CreateHost(dispatcher: false);
        await host.StartAsync();
        await PublishAsync(envelope);
        await WaitAsync(() => Task.FromResult(_log.Acknowledged == 1));
        await host.StopAsync();
        var expectedLog = variant == "unsupported" ? "Unsupported import contract version;" : "Missing job or stale import run ignored";
        Assert.Single(_log.Messages, x => x.StartsWith(expectedLog, StringComparison.Ordinal));
        await AssertUnchangedAsync(job.Id, 0);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task ConcurrentClaimsAreDisjoint_PreviousOwnerCannotCompleteOrFailAfterTakeover()
    {
        var (_, first) = await SeedAsync();
        var (_, second) = await SeedAsync();
        await using var one = postgres.CreateContext();
        await using var two = postgres.CreateContext();
        var storeOne = new OutboxStore(one);
        var storeTwo = new OutboxStore(two);
        var claims = await Task.WhenAll(storeOne.ClaimAsync("one", 1, 30, CancellationToken.None),
            storeTwo.ClaimAsync("two", 1, 30, CancellationToken.None));
        Assert.Single(claims[0]);
        Assert.Single(claims[1]);
        Assert.NotEqual(claims[0][0].Id, claims[1][0].Id);
        await one.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET locked_until = now() - interval '1 second' WHERE id = {first.Id}");
        Assert.Equal(0, await storeOne.MarkPublishedAsync(first.Id, "one", CancellationToken.None));
        var retaken = Assert.Single(await storeTwo.ClaimAsync("new-owner", 2, 30, CancellationToken.None));
        Assert.Equal(first.Id, retaken.Id);
        Assert.Equal(0, await storeOne.MarkPublishedAsync(first.Id, "one", CancellationToken.None));
        Assert.Equal(0, await storeOne.MarkFailedAsync(first.Id, "one", "IOException", CancellationToken.None));
        Assert.Equal(0, await storeTwo.MarkFailedAsync(first.Id, "two", "IOException", CancellationToken.None));
        Assert.Equal(1, await storeTwo.MarkPublishedAsync(first.Id, "new-owner", CancellationToken.None));
        Assert.Null((await ReadAsync(second.Id)).PublishedAt);
        var pending = await ReadAsync(second.Id);
        await one.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET locked_until = now() - interval '1 second' WHERE id = {second.Id}");
        Assert.Equal(1, await storeOne.MarkFailedAsync(second.Id, pending.Owner!, "IOException", CancellationToken.None));
        var failed = await ReadAsync(second.Id);
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.Owner);
        Assert.Null(failed.LockedUntil);
    }

    [Fact]
    public async Task LockedRowIsSkippedWithoutWaitingForItsTransaction()
    {
        var (_, message) = await SeedAsync();
        await using var locked = postgres.CreateContext();
        await using var transaction = await locked.Database.BeginTransactionAsync();
        await locked.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM outbox_messages WHERE id = {message.Id} FOR UPDATE");
        await using var other = postgres.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Empty(await new OutboxStore(other).ClaimAsync("other", 32, 30, deadline.Token));
        await transaction.RollbackAsync();
    }

    private IHost CreateHost(bool dispatcher, RabbitMqOptions? options = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_log);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? rabbit.Options));
        builder.Services.AddOptions<OutboxOptions>().ValidateDataAnnotations().ValidateOnStart();
        using var context = postgres.CreateContext();
        var connectionString = context.Database.GetConnectionString();
        builder.Services.AddDbContext<DataFlowDbContext>(configuration => configuration.UseNpgsql(connectionString));
        builder.Services.AddScoped<OutboxStore>();
        builder.Services.AddOptions<ImportProcessingOptions>();
        builder.Services.AddScoped<ImportJobStore>();
        builder.Services.AddScoped<CsvImportProcessor>();
        builder.Services.AddScoped<ICsvRowParser, CsvRowParser>();
        builder.Services.AddSingleton<IImportFileReader, TestFileReader>();
        if (dispatcher)
        {
            builder.Services.AddHostedService<OutboxDispatcher>();
        }
        else
        {
            builder.Services.AddHostedService<ImportConsumer>();
        }
        return builder.Build();
    }

    private async Task<(ImportJob Job, OutboxMessage Message)> SeedAsync()
    {
        await using var database = postgres.CreateContext();
        var source = DataSource.Create("Messaging", DataSourceDataType.Customer, AcceptedFileFormat.Csv,
            new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true), new("name", "string", true),
                new("email", "email", true), new("isActive", "boolean", true)]), DateTimeOffset.UtcNow);
        var job = ImportJob.Create(source.Id, "test.csv", Guid.NewGuid().ToString("N"), new string('a', 64), source.SchemaJson, source.SchemaVersion, DateTimeOffset.UtcNow);
        var payload = new ImportJobCreatedPayload(job.Id, source.Id, job.SchemaVersion, job.RunVersion);
        var message = OutboxMessage.Create("import.job.created", JsonSerializer.Serialize(payload, ImportMessageEnvelope.JsonOptions), DateTimeOffset.UtcNow);
        database.DataSources.Add(source);
        database.ImportJobs.Add(job);
        database.OutboxMessages.Add(message);
        await database.SaveChangesAsync();
        return (job, message);
    }

    private static ImportMessageEnvelope Envelope(OutboxMessage message) => ImportMessageEnvelope.Create(
        new ClaimedOutbox(message.Id, message.Type, message.Payload, message.OccurredAt, message.Attempts));

    private async Task PublishAsync(params ImportMessageEnvelope[] envelopes)
    {
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, true, CancellationToken.None);
        foreach (var envelope in envelopes)
        {
            await session.Channel.BasicPublishAsync(RabbitMqSession.Exchange, RabbitMqSession.RoutingKey, true,
                new BasicProperties { Persistent = true }, JsonSerializer.SerializeToUtf8Bytes(envelope, ImportMessageEnvelope.JsonOptions));
        }
    }

    private async Task<OutboxMessage> ReadAsync(Guid id)
    {
        await using var database = postgres.CreateContext();
        return await database.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task AssertUnchangedAsync(Guid jobId, int expectedProcessed)
    {
        await using var database = postgres.CreateContext();
        var job = await database.ImportJobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal(ImportJobStatus.Queued, job.Status);
        Assert.Equal(0, job.ProcessedRows);
        Assert.Equal(0, job.InsertedRows);
        Assert.Equal(0, job.UpdatedRows);
        Assert.Equal(0, job.InvalidRows);
        Assert.Equal(0, job.DuplicateRows);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(0, await database.ImportRows.CountAsync());
        Assert.Equal(expectedProcessed, await database.ProcessedMessages.CountAsync());
        Assert.Single(await database.OutboxMessages.ToListAsync());
    }

    private async Task AssertQueueEmptyAsync()
    {
        // After closing the consumer channel any unacked delivery would be requeued.
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, false, CancellationToken.None);
        Assert.Equal(0u, (await session.Channel.QueueDeclarePassiveAsync(RabbitMqSession.Queue)).MessageCount);
        Assert.Null(await session.Channel.BasicGetAsync(RabbitMqSession.Queue, true));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!await condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class DeliveryLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public int Acknowledged => Messages.Count(x => x == "Import delivery acknowledged");
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TestFileReader : IImportFileReader
    {
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("externalId,name,email,isActive\n1,Customer,customer@example.test,true\n")));
    }
}
