using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Enums;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Dashboard;

public sealed record DashboardRecentJobDto(
    Guid Id,
    Guid DataSourceId,
    string DataSourceName,
    string OriginalFileName,
    string Format,
    string Status,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? TotalRows,
    long ProcessedRows,
    long InsertedRows,
    long UpdatedRows,
    long InvalidRows,
    long DuplicateRows,
    long ValidRows = 0,
    Guid? RuleSetVersionId = null,
    string? RuleSetName = null,
    int? RuleVersionNumber = null);

public sealed record DashboardSummaryDto(
    string Period,
    DateTimeOffset? FromUtc,
    DateTimeOffset ToUtc,
    long TotalJobs,
    long CompletedJobs,
    long CompletedWithErrorsJobs,
    long FailedJobs,
    long CancelledJobs,
    long AttentionJobs,
    long ActiveJobs,
    long QueuedJobs,
    long ProcessingJobs,
    double CompletionSuccessRate,
    long ProcessedRows,
    long InsertedRows,
    long UpdatedRows,
    long InvalidRows,
    long DuplicateRows,
    long ValidRows,
    long AcceptedRows,
    IReadOnlyList<DashboardRecentJobDto> RecentJobs);

public sealed record GetDashboardSummaryQuery(string Period = "week", int RecentLimit = 8)
    : IQuery<DashboardSummaryDto>;

public sealed class GetDashboardSummaryValidator : AbstractValidator<GetDashboardSummaryQuery>
{
    private static readonly string[] AllowedPeriods = ["today", "week", "month", "all"];

    public GetDashboardSummaryValidator()
    {
        RuleFor(x => x.Period)
            .NotEmpty()
            .Must(period => AllowedPeriods.Contains(period, StringComparer.Ordinal));
        RuleFor(x => x.RecentLimit)
            .InclusiveBetween(1, 50);
    }
}

public sealed class DashboardQueries(IDataFlowDbContext database, ICurrentUser currentUser, TimeProvider clock)
    : IQueryHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    public async Task<DashboardSummaryDto> HandleAsync(GetDashboardSummaryQuery request, CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().ToUniversalTime();
        DateTimeOffset? fromUtc = request.Period switch
        {
            "today" => new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, TimeSpan.Zero),
            "week" => nowUtc.AddDays(-7),
            "month" => nowUtc.AddDays(-30),
            _ => null
        };

        var jobsQuery = from job in database.ImportJobs.AsNoTracking()
                        join source in database.DataSources.AsNoTracking() on job.DataSourceId equals source.Id
                        where source.OwnerUserId == currentUser.UserId &&
                              (fromUtc == null || job.CreatedAt >= fromUtc.Value)
                        select new
                        {
                            Job = job,
                            SourceName = source.Name
                        };

        var aggregates = await jobsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalJobs = g.LongCount(),
                CompletedJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.Completed),
                CompletedWithErrorsJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.CompletedWithErrors),
                FailedJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.Failed),
                CancelledJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.Cancelled),
                QueuedJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.Queued || x.Job.Status == ImportJobStatus.RetryScheduled),
                ProcessingJobs = g.LongCount(x => x.Job.Status == ImportJobStatus.Processing),
                ProcessedRows = g.Sum(x => x.Job.ProcessedRows),
                InsertedRows = g.Sum(x => x.Job.InsertedRows),
                UpdatedRows = g.Sum(x => x.Job.UpdatedRows),
                InvalidRows = g.Sum(x => x.Job.InvalidRows),
                DuplicateRows = g.Sum(x => x.Job.DuplicateRows),
                ValidRows = g.Sum(x => x.Job.ValidRows)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var recentRaw = await jobsQuery
            .OrderByDescending(x => x.Job.CreatedAt)
            .ThenBy(x => x.Job.Id)
            .Take(request.RecentLimit)
            .Select(x => new
            {
                x.Job.Id,
                x.Job.DataSourceId,
                x.SourceName,
                x.Job.OriginalFileName,
                x.Job.Status,
                x.Job.SchemaVersion,
                x.Job.CreatedAt,
                x.Job.StartedAt,
                x.Job.CompletedAt,
                x.Job.TotalRows,
                x.Job.ProcessedRows,
                x.Job.InsertedRows,
                x.Job.UpdatedRows,
                x.Job.InvalidRows,
                x.Job.DuplicateRows,
                x.Job.ValidRows,
                x.Job.RuleSetVersionId,
                x.Job.RuleSetNameAtSubmission,
                x.Job.RuleVersionNumberAtSubmission
            })
            .ToArrayAsync(cancellationToken);

        var recentJobs = recentRaw.Select(x => new DashboardRecentJobDto(
            x.Id,
            x.DataSourceId,
            x.SourceName,
            x.OriginalFileName,
            x.OriginalFileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "XLSX" : "CSV",
            x.Status.ToString(),
            x.SchemaVersion,
            x.CreatedAt,
            x.StartedAt,
            x.CompletedAt,
            x.TotalRows,
            x.ProcessedRows,
            x.InsertedRows,
            x.UpdatedRows,
            x.InvalidRows,
            x.DuplicateRows,
            x.ValidRows,
            x.RuleSetVersionId,
            x.RuleSetNameAtSubmission,
            x.RuleVersionNumberAtSubmission)).ToArray();

        var totalJobs = aggregates?.TotalJobs ?? 0L;
        var completedJobs = aggregates?.CompletedJobs ?? 0L;
        var completedWithErrorsJobs = aggregates?.CompletedWithErrorsJobs ?? 0L;
        var failedJobs = aggregates?.FailedJobs ?? 0L;
        var cancelledJobs = aggregates?.CancelledJobs ?? 0L;
        var queuedJobs = aggregates?.QueuedJobs ?? 0L;
        var processingJobs = aggregates?.ProcessingJobs ?? 0L;
        var attentionJobs = completedWithErrorsJobs + failedJobs;
        var activeJobs = queuedJobs + processingJobs;

        var finishedOrFailed = completedJobs + completedWithErrorsJobs + failedJobs;
        var completionSuccessRate = finishedOrFailed == 0
            ? 0d
            : Math.Round((double)(completedJobs + completedWithErrorsJobs) * 100d / finishedOrFailed, 2, MidpointRounding.AwayFromZero);

        var processedRows = aggregates?.ProcessedRows ?? 0L;
        var insertedRows = aggregates?.InsertedRows ?? 0L;
        var updatedRows = aggregates?.UpdatedRows ?? 0L;
        var invalidRows = aggregates?.InvalidRows ?? 0L;
        var duplicateRows = aggregates?.DuplicateRows ?? 0L;
        var validRows = aggregates?.ValidRows ?? 0L;
        var acceptedRows = insertedRows + updatedRows + duplicateRows + validRows;

        return new DashboardSummaryDto(
            request.Period,
            fromUtc,
            nowUtc,
            totalJobs,
            completedJobs,
            completedWithErrorsJobs,
            failedJobs,
            cancelledJobs,
            attentionJobs,
            activeJobs,
            queuedJobs,
            processingJobs,
            completionSuccessRate,
            processedRows,
            insertedRows,
            updatedRows,
            invalidRows,
            duplicateRows,
            validRows,
            acceptedRows,
            recentJobs);
    }
}
