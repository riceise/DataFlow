using System.Diagnostics;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Observability;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataFlow.Infrastructure.Imports;

public sealed class JobControlHandler(DataFlowDbContext database, ICurrentUser currentUser, ILogger<JobControlHandler>? logger = null) :
    ICommandHandler<RetryJobCommand, JobAcceptedDto>, ICommandHandler<CancelJobCommand, JobAcceptedDto>
{
    public Task<JobAcceptedDto> HandleAsync(RetryJobCommand request, CancellationToken cancellationToken) =>
        ControlAsync(request.Id, true, cancellationToken);

    public Task<JobAcceptedDto> HandleAsync(CancelJobCommand request, CancellationToken cancellationToken) =>
        ControlAsync(request.Id, false, cancellationToken);

    private async Task<JobAcceptedDto> ControlAsync(Guid id, bool retry, CancellationToken cancellationToken)
    {
        using var activity = DataFlowTelemetry.Activities.StartActivity(retry ? "import manual retry" : "import cancel");
        activity?.SetTag("job.id", id);
        using var jobScope = logger?.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = id,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? string.Empty
        });
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10s'; SET LOCAL lock_timeout = '5s'", cancellationToken);
        var job = await database.ImportJobs.FromSqlInterpolated($"SELECT * FROM import_jobs WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw new DomainException("job_not_found", "Job was not found.");
        if (!await database.DataSources.AnyAsync(s => s.Id == job.DataSourceId && s.OwnerUserId == currentUser.UserId, cancellationToken))
        {
            throw new DomainException("job_not_found", "Job was not found.");
        }

        var now = await database.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        if (retry)
        {
            job.Retry(now);
            database.OutboxMessages.Add(ImportRecoveryMessage.Create(job, 1, now));
        }
        else
        {
            job.RequestCancel();
            // Jobs without a live owner can stop immediately, without waiting for a TTL delivery.
            if (job.Status != ImportJobStatus.Processing) { job.ConfirmCancel(now); }
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        activity?.SetTag("job.run_version", job.RunVersion);
        logger?.LogInformation("Import control committed: {Operation}; run {RunVersion}", retry ? "retry" : "cancel", job.RunVersion);
        return new(job.Id, job.Status.ToString());
    }
}
