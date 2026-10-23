using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Enums;
using FluentValidation;

namespace DataFlow.Application.Jobs;

public sealed record UploadFileCommand(Guid DataSourceId, string IdempotencyKey, string FilePath,
    string FileName, string FileSha256, AcceptedFileFormat Format) : ICommand<JobAcceptedDto>;
public sealed record JobAcceptedDto(Guid JobId, string Status);
public sealed record JobDto(Guid Id, Guid DataSourceId, string Status, int SchemaVersion,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int? TotalRows, long ProcessedRows, long InsertedRows, long UpdatedRows, long InvalidRows, long DuplicateRows);
public sealed record GetJobQuery(Guid Id) : IQuery<JobDto>;
public sealed record ListJobsQuery(Guid DataSourceId, int Page = 1, int PageSize = 50, string? Status = null)
    : IQuery<PagedResult<JobDto>>;

public sealed class ListJobsValidator : AbstractValidator<ListJobsQuery>
{
    public ListJobsValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).Must(status => status is null || Enum.GetNames<ImportJobStatus>().Contains(status, StringComparer.Ordinal));
    }
}
