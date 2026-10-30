using DataFlow.Application.Abstractions;
using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Jobs;

public sealed record JobRowDto(int RowNumber, string Status, string? RawData,
    IReadOnlyList<ImportRowError> Errors, string? NaturalKey, string? RowHash, DateTimeOffset ProcessedAt);

public sealed record ListJobRowsQuery(Guid JobId, int Page = 1, int PageSize = 50, string? Status = null)
    : IQuery<PagedResult<JobRowDto>>;

public sealed class ListJobRowsValidator : AbstractValidator<ListJobRowsQuery>
{
    public ListJobRowsValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.Status).Must(status => status is null || Enum.GetNames<ImportRowStatus>().Contains(status, StringComparer.Ordinal));
    }
}

public sealed class JobRowQueries(IDataFlowDbContext database) : IQueryHandler<ListJobRowsQuery, PagedResult<JobRowDto>>
{
    public async Task<PagedResult<JobRowDto>> HandleAsync(ListJobRowsQuery request, CancellationToken cancellationToken)
    {
        if (!await database.ImportJobs.AnyAsync(x => x.Id == request.JobId, cancellationToken))
        {
            throw new DomainException("job_not_found", "Job was not found.");
        }

        var query = database.ImportRows.AsNoTracking().Where(x => x.ImportJobId == request.JobId);
        if (request.Status is not null)
        {
            var status = Enum.Parse<ImportRowStatus>(request.Status);
            query = query.Where(x => x.Status == status);
        }
        var total = await query.LongCountAsync(cancellationToken);
        var offset = ((long)request.Page - 1) * request.PageSize;
        // Materialize only the requested page; error JSON is decoded after the SQL query.
        var rows = offset > int.MaxValue ? [] : await query.OrderBy(x => x.RowNumber)
            .Skip((int)offset).Take(request.PageSize).ToArrayAsync(cancellationToken);
        var items = rows.Select(row => new JobRowDto(row.RowNumber, row.Status.ToString(), row.RawData,
            row.GetErrors(), row.NaturalKey, row.RowHash, row.ProcessedAt)).ToArray();
        return new(items, request.Page, request.PageSize, total);
    }
}
