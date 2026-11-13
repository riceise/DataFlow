using System.Diagnostics;
using System.Diagnostics.Metrics;
using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataFlow.Infrastructure.Observability;

public static class DataFlowTelemetry
{
    public const string Name = "DataFlow";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Rows = Meter.CreateCounter<long>("dataflow.import.rows", "{row}");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("dataflow.import.duration", "s");
    public static readonly Counter<long> Retries = Meter.CreateCounter<long>("dataflow.import.retries", "{retry}");
    public static readonly Counter<long> Redeliveries = Meter.CreateCounter<long>("dataflow.message.redeliveries", "{delivery}");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("dataflow.dependency.errors", "{error}");
    public static readonly Counter<long> Deleted = Meter.CreateCounter<long>("dataflow.cleanup.deleted", "{file}");

    public static ActivityContext Parent(string? parent, string? state = null) =>
        ActivityContext.TryParse(parent, state, isRemote: true, out var context) ? context : default;
}

// Capture at the same durable boundary as the outbox record, not at dispatch time.
public sealed class OutboxTraceInterceptor : SaveChangesInterceptor
{
    private static void Capture(DbContext? db)
    {
        if (db is null) { return; }
        foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added))
        {
            entry.Property("TraceParent").CurrentValue = Activity.Current?.Id;
            entry.Property("TraceState").CurrentValue = Activity.Current?.TraceStateString;
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }
}
