using System.Net;
using System.Text.Json;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace DataFlow.IntegrationTests;

public sealed class RecoveryStage8Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>
{
    private async Task<ImportJob> SeedAsync()
    {
        await using var database = fixture.CreateContext();
        var schema = new DataSourceSchema(1, ["externalId"], [new("externalId", "string", true, 80)]);
        var source = DataSource.Create("Recovery", DataSourceDataType.Customer, AcceptedFileFormat.Csv, schema, DateTimeOffset.UtcNow);
        source.AssignOwner(TestAuthentication.UserId);
        var job = ImportJob.Create(source.Id, "test.csv", "test", new string('a', 64), schema.ToJson(), 1, DateTimeOffset.UtcNow);
        database.DataSources.Add(source);
        database.ImportJobs.Add(job);
        await database.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Heartbeat_RenewsAndRejectsExpiredOwnerAfterTakeover()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var old = (await store.AcquireAsync(job.Id, 1, "old", 60, default))!;
        var before = (await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).LeaseUntil;
        using var processing = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var heartbeat = new ImportHeartbeat(database.Database.GetConnectionString()!).RunAsync(old, 60, 1, processing, stop.Token);
        await Task.Delay(1300);
        await stop.CancelAsync();
        await heartbeat;
        Assert.True((await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).LeaseUntil > before);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET lease_until = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
        Assert.False(await store.RenewAsync(old, 60, default));
        var current = (await store.AcquireAsync(job.Id, 1, "new", 60, default))!;
        Assert.False(await store.RenewAsync(old, 60, default));
        Assert.True(await store.RenewAsync(current, 60, default));
        using var lost = new CancellationTokenSource();
        await new ImportHeartbeat(database.Database.GetConnectionString()!).RunAsync(old, 60, 1, lost, default);
        Assert.True(lost.IsCancellationRequested);
        await Assert.ThrowsAsync<ImportLeaseUnavailableException>(() => store.FinalizeAsync(old, Guid.NewGuid(), 0, null, false, default));
    }

    [Fact]
    public async Task Cancel_AtBatchBoundaryPreservesCommittedPrefix()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "owner", 60, default))!;
        var tracked = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
        tracked.RecordBatchCompletion(251, 0, 0, 250, 0, DateTimeOffset.UtcNow);
        tracked.RequestCancel();
        await database.SaveChangesAsync();
        await Assert.ThrowsAsync<ImportCancellationRequestedException>(() => store.CommitBatchAsync(lease, [], 60, default));
        await store.FinalizeAsync(lease, Guid.NewGuid(), null, null, false, default);
        var cancelled = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ImportJobStatus.Cancelled, cancelled.Status);
        Assert.Equal(250, cancelled.ProcessedRows);
        Assert.Equal(251, cancelled.LastCommittedRowNumber);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 300)]
    [InlineData(4, 0)]
    public async Task Retry_AtomicallyPersistsScheduleInboxAndOutbox_OrFailsIntoDlq(int attempt, int delay)
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "owner", 60, default, attempt))!;
        var messageId = Guid.NewGuid();
        await store.FinalizeAsync(lease, messageId, null, "import_infrastructure_error", true, default, attempt);
        var current = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(delay > 0 ? ImportJobStatus.RetryScheduled : ImportJobStatus.Failed, current.Status);
        Assert.True(await database.ProcessedMessages.AnyAsync(x => x.MessageId == messageId.ToString()));
        var messages = await database.OutboxMessages.AsNoTracking().ToListAsync();
        var message = Assert.Single(messages, x => x.Type == (delay > 0 ? RabbitMqSession.RoutingKey : ImportRecoveryMessage.Dead)
            && JsonDocument.Parse(x.Payload).RootElement.GetProperty("jobId").GetGuid() == job.Id);
        var envelope = ImportMessageEnvelope.Create(new(message.Id, message.Type, message.Payload, message.OccurredAt, 99));
        Assert.Equal(delay > 0 ? attempt + 1 : 4, envelope.Attempt);
        Assert.Equal(delay > 0 ? $"imports.retry.{delay}" : ImportRecoveryMessage.Dead, ImportRecoveryMessage.Route(envelope));
        if (delay > 0)
        {
            Assert.Equal(TimeSpan.FromSeconds(delay), current.NextAttemptAt!.Value - current.UpdatedAt);
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET next_attempt_at = clock_timestamp() - interval '1 second' WHERE id = {job.Id}");
            Assert.Null(await store.AcquireAsync(job.Id, 1, "stale", 60, default, attempt));
            Assert.NotNull(await store.AcquireAsync(job.Id, 1, "retry", 60, default, attempt + 1));
        }
        else { Assert.Equal("import_retry_exhausted", current.LastErrorCode); Assert.Equal(1, current.Retryable); }
    }

    [Fact]
    public async Task Retry_OutboxFailureRollsBackScheduleAndInbox()
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        var store = new ImportJobStore(database);
        var lease = (await store.AcquireAsync(job.Id, 1, "owner", 60, default))!;
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_stage8_retry() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.type = 'import.job.created' THEN RAISE EXCEPTION 'injected'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_stage8_retry BEFORE INSERT ON outbox_messages FOR EACH ROW EXECUTE FUNCTION reject_stage8_retry();
            """);
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => store.FinalizeAsync(lease, Guid.NewGuid(), null, "error", true, default));
            await using var observed = fixture.CreateContext();
            Assert.Equal(ImportJobStatus.Processing, (await observed.ImportJobs.SingleAsync(x => x.Id == job.Id)).Status);
            Assert.False(await observed.ProcessedMessages.AnyAsync(x => x.JobId == job.Id));
        }
        finally { await database.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_stage8_retry ON outbox_messages; DROP FUNCTION reject_stage8_retry()"); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Reconciliation_RecoversMissingQueuedExpiredLeaseAndOverdueRetry_WithoutFlood(int status)
    {
        var job = await SeedAsync();
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE import_jobs SET status = {status}, lease_owner = 'dead', lease_until = clock_timestamp() - interval '2 minutes',
                next_attempt_at = clock_timestamp() - interval '2 minutes', updated_at = clock_timestamp() - interval '2 minutes'
            WHERE id = {job.Id}
            """);
        var store = new ImportReconciliationStore(database);
        await store.ReconcileAsync(default);
        var count = await database.OutboxMessages.CountAsync();
        await store.ReconcileAsync(default);
        Assert.Equal(count, await database.OutboxMessages.CountAsync());
        var messages = await database.OutboxMessages.ToListAsync();
        Assert.Single(messages, x => x.Type == RabbitMqSession.RoutingKey && JsonDocument.Parse(x.Payload).RootElement.GetProperty("jobId").GetGuid() == job.Id);
    }

    [Fact]
    public async Task RetryAndCancelApi_PreserveCheckpointAndGuardStates()
    {
        var job = await SeedAsync();
        await using (var database = fixture.CreateContext())
        {
            var current = await database.ImportJobs.SingleAsync(x => x.Id == job.Id);
            current.AcquireLease("owner", DateTimeOffset.UtcNow, 60);
            current.RecordBatchCompletion(251, 0, 0, 250, 0, DateTimeOffset.UtcNow);
            current.Fail("import_retry_exhausted", "Retry exhausted.", true, DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var retry = await client.PostAsync($"/api/v1/import-jobs/{job.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        using var duplicate = await client.PostAsync($"/api/v1/import-jobs/{job.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var cancel = await client.PostAsync($"/api/v1/import-jobs/{job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        using var missing = await client.PostAsync($"/api/v1/import-jobs/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var observed = fixture.CreateContext();
        var result = await observed.ImportJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(2, result.RunVersion);
        Assert.Equal(251, result.LastCommittedRowNumber);
        Assert.Equal(250, result.ProcessedRows);
        Assert.Equal(ImportJobStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Reconciliation_ConcurrentWorkersDoNotDuplicatePendingRecovery()
    {
        var job = await SeedAsync();
        await using var one = fixture.CreateContext();
        await using var two = fixture.CreateContext();
        await one.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET updated_at = clock_timestamp() - interval '2 minutes' WHERE id = {job.Id}");
        await Task.WhenAll(new ImportReconciliationStore(one).ReconcileAsync(default), new ImportReconciliationStore(two).ReconcileAsync(default));
        var messages = await one.OutboxMessages.AsNoTracking().ToListAsync();
        Assert.Single(messages, x => x.Type == RabbitMqSession.RoutingKey && JsonDocument.Parse(x.Payload).RootElement.GetProperty("jobId").GetGuid() == job.Id);
        await one.Database.ExecuteSqlInterpolatedAsync($"UPDATE import_jobs SET updated_at = clock_timestamp() - interval '2 minutes' WHERE id = {job.Id}");
        await new ImportReconciliationStore(two).ReconcileAsync(default);
        Assert.Equal(messages.Count, await one.OutboxMessages.CountAsync());
    }
}

public sealed class RetryDlxStage8Tests(RabbitMqFixture rabbit) : IClassFixture<RabbitMqFixture>
{
    [Fact]
    public async Task RetryDlx_RetainsExpiredMessageWhileTargetMissing_ThenDelivers()
    {
        await using var session = await RabbitMqSession.ConnectAsync(rabbit.Options, true, default);
        await session.Channel.QueueDeleteAsync(RabbitMqSession.Queue);
        var body = Guid.NewGuid().ToByteArray();
        await session.Channel.BasicPublishAsync(RabbitMqSession.Exchange, "imports.retry.5", true,
            new BasicProperties { Persistent = true }, body);
        await Task.Delay(TimeSpan.FromSeconds(8));
        // The target queue does not exist when TTL expires. Quorum at-least-once retains it.
        await using var restored = await RabbitMqSession.ConnectAsync(rabbit.Options, true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(210));
        BasicGetResult? message;
        while ((message = await restored.Channel.BasicGetAsync(RabbitMqSession.Queue, false)) is null)
        {
            if (timeout.IsCancellationRequested) { Assert.Fail(await rabbit.DiagnosticsAsync()); }
            await Task.Delay(200);
        }
        Assert.Equal(body, message.Body.ToArray());
        Assert.NotNull(message.BasicProperties.Headers);
        Assert.True(message.BasicProperties.Headers.ContainsKey("x-death"));
        await restored.Channel.BasicAckAsync(message.DeliveryTag, false);
    }
}
