using System.Linq.Expressions;
using DataFlow.Application.Abstractions;
using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Jobs;

public sealed class JobQueries(IDataFlowDbContext database) :
    IQueryHandler<GetJobQuery, JobDto>, IQueryHandler<ListJobsQuery, PagedResult<JobDto>>
{
    private static readonly Expression<Func<ImportJob, JobDto>> Projection = job => new JobDto(
        job.Id, job.DataSourceId, job.Status.ToString(), job.SchemaVersion, job.CreatedAt, job.StartedAt,
        job.CompletedAt, job.TotalRows, job.ProcessedRows, job.InsertedRows, job.UpdatedRows, job.InvalidRows, job.DuplicateRows);

    public async Task<JobDto> HandleAsync(GetJobQuery request, CancellationToken cancellationToken) =>
        await database.ImportJobs.AsNoTracking().Where(x => x.Id == request.Id).Select(Projection)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new DomainException("job_not_found", "Job was not found.");

    public async Task<PagedResult<JobDto>> HandleAsync(ListJobsQuery request, CancellationToken cancellationToken)
    {
        if (!await database.DataSources.AnyAsync(x => x.Id == request.DataSourceId, cancellationToken))
        {
            throw new DomainException("source_not_found", "Data source was not found.");
        }

        var query = database.ImportJobs.AsNoTracking().Where(x => x.DataSourceId == request.DataSourceId);
        if (request.Status is not null)
        {
            var status = Enum.Parse<ImportJobStatus>(request.Status);
            query = query.Where(x => x.Status == status);
        }
        var total = await query.LongCountAsync(cancellationToken);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((int)offset).Take(request.PageSize).Select(Projection).ToArrayAsync(cancellationToken);
        return new(items, request.Page, request.PageSize, total);
    }
}
