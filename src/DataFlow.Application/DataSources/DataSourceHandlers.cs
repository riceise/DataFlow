using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.DataSources;

internal sealed class DataSourceHandlers(IDataFlowDbContext database) :
    ICommandHandler<CreateDataSourceCommand, DataSourceDto>,
    ICommandHandler<UpdateDataSourceCommand, DataSourceDto>,
    ICommandHandler<ActivateDataSourceCommand, DataSourceDto>,
    ICommandHandler<DeactivateDataSourceCommand, DataSourceDto>,
    IQueryHandler<GetDataSourceQuery, DataSourceDto>,
    IQueryHandler<ListDataSourcesQuery, PagedResult<DataSourceDto>>
{
    public async Task<DataSourceDto> HandleAsync(CreateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var source = DataSource.Create(request.Source.Name!, DataSourceDataType.Customer,
            request.Source.ToFormats(), request.Source.ToSchema(), DateTimeOffset.UtcNow);
        database.DataSources.Add(source);
        await database.SaveChangesAsync(cancellationToken);
        return source.ToDto();
    }

    public async Task<DataSourceDto> HandleAsync(UpdateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var source = await FindAsync(request.Id, cancellationToken);
        source.Update(request.Source.Name!, request.Source.ToFormats(), request.Source.ToSchema(), DateTimeOffset.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return source.ToDto();
    }

    public async Task<DataSourceDto> HandleAsync(ActivateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var source = await FindAsync(request.Id, cancellationToken);
        source.Activate(DateTimeOffset.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return source.ToDto();
    }

    public async Task<DataSourceDto> HandleAsync(DeactivateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var source = await FindAsync(request.Id, cancellationToken);
        source.Deactivate(DateTimeOffset.UtcNow);
        await database.SaveChangesAsync(cancellationToken);
        return source.ToDto();
    }

    public async Task<DataSourceDto> HandleAsync(GetDataSourceQuery request, CancellationToken cancellationToken)
    {
        var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(source => source.Id == request.Id, cancellationToken)
            ?? throw NotFound();
        return source.ToDto();
    }

    public async Task<PagedResult<DataSourceDto>> HandleAsync(ListDataSourcesQuery request, CancellationToken cancellationToken)
    {
        var query = database.DataSources.AsNoTracking();
        var total = await query.LongCountAsync(cancellationToken);
        var offset = ((long)request.Page - 1) * request.PageSize;
        var sources = offset > int.MaxValue ? [] : await query.OrderByDescending(source => source.CreatedAt).ThenBy(source => source.Id)
            .Skip((int)offset).Take(request.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<DataSourceDto>(sources.Select(source => source.ToDto()).ToArray(), request.Page, request.PageSize, total);
    }

    private async Task<DataSource> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await database.DataSources.SingleOrDefaultAsync(source => source.Id == id, cancellationToken) ?? throw NotFound();

    private static DomainException NotFound() => new("source_not_found", "Data source was not found.");
}
