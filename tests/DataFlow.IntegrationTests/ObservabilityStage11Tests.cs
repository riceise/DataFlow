using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DataFlow.Domain.Entities;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Observability;
using DataFlow.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class ObservabilityStage11Tests(DataSourcesApiFixture postgres) : IClassFixture<DataSourcesApiFixture>
{
    [Fact]
    public async Task DurableTraceContext_SurvivesDispatchWithoutAnAmbientActivity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DataFlowTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        await using var factory = postgres.WithWebHostBuilder(_ => { });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataFlow.Infrastructure.Persistence.DataFlowDbContext>();
        Guid id;
        string? parent;
        using (var upload = DataFlowTelemetry.Activities.StartActivity("upload test"))
        {
            upload!.TraceStateString = "vendor=test";
            parent = upload.Id;
            var message = OutboxMessage.Create("import.job.created", "{\"jobId\":\"" + Guid.NewGuid() + "\",\"runVersion\":1}", DateTimeOffset.UtcNow);
            id = message.Id;
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
        }
        var claimed = Assert.Single(await new OutboxStore(db).ClaimAsync("trace-test", 100, 30, default), m => m.Id == id);
        Assert.Equal(parent, claimed.TraceParent);
        Assert.Equal("vendor=test", claimed.TraceState);
        var envelope = ImportMessageEnvelope.Create(claimed);
        Assert.Equal(parent, envelope.TraceParent);
        Assert.Equal("vendor=test", envelope.TraceState);
        Assert.Equal(DataFlowTelemetry.Parent(parent).TraceId, DataFlowTelemetry.Parent(envelope.TraceParent).TraceId);
        Assert.Equal(default, DataFlowTelemetry.Parent("not-a-trace"));
    }

    [Fact]
    public async Task Readiness_ReportsSafeDependencyNames_LivenessRemainsIndependent()
    {
        await using var factory = postgres.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:Port"] = "1",
                ["RabbitMq:Password"] = "never-expose-this-password",
                ["Storage:Endpoint"] = "http://127.0.0.1:1",
                ["Storage:AccessKey"] = "test",
                ["Storage:SecretKey"] = "never-expose-this-secret"
            })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        var dependencies = JsonDocument.Parse(body).RootElement.GetProperty("dependencies");
        Assert.Equal("Healthy", dependencies.GetProperty("postgres").GetString());
        Assert.Equal("Unhealthy", dependencies.GetProperty("rabbitmq").GetString());
        Assert.Equal("Unhealthy", dependencies.GetProperty("storage").GetString());
        Assert.DoesNotContain("never-expose", body);
        Assert.DoesNotContain("127.0.0.1", body);
    }

    [Fact]
    public void TempCleanup_PreservesLiveLeaseRecentAndForeignFiles_DeletesCrashLeftovers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dataflow-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stale = Path.Combine(directory, new string('a', 64));
            var active = Path.Combine(directory, new string('b', 64));
            var recent = Path.Combine(directory, new string('c', 64));
            var foreign = Path.Combine(directory, "operator.txt");
            foreach (var path in new[] { stale, active, recent, foreign }) { File.WriteAllText(path, "test"); }
            foreach (var path in new[] { stale, active, foreign }) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2)); }
            using var lease = new FileStream(active + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var service = new StorageMaintenanceService(null!, Options.Create(new StorageOptions { TempDirectory = directory }),
                NullLogger<StorageMaintenanceService>.Instance);
            Assert.Equal(1, service.CleanupTemporaryFiles());
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(active));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(foreign));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ObjectLock_SkipsInFlightUpload_AndReleasesForNextCleaner()
    {
        await using var db = postgres.CreateContext();
        var key = "sources/" + Guid.NewGuid().ToString("N");
        await using (var held = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, key, true, default))
        {
            Assert.NotNull(held);
            await using var skipped = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, key, false, default);
            Assert.Null(skipped);
        }
        await using var acquired = await StorageObjectLock.AcquireAsync(db.Database.GetConnectionString()!, key, false, default);
        Assert.NotNull(acquired);
    }
}
