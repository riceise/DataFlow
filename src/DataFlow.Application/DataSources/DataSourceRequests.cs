using DataFlow.Application.Dispatching;

namespace DataFlow.Application.DataSources;

public sealed record CreateDataSourceCommand(SaveDataSourceRequest Source) : ICommand<DataSourceDto>;
public sealed record UpdateDataSourceCommand(Guid Id, SaveDataSourceRequest Source) : ICommand<DataSourceDto>;
public sealed record ActivateDataSourceCommand(Guid Id) : ICommand<DataSourceDto>;
public sealed record DeactivateDataSourceCommand(Guid Id) : ICommand<DataSourceDto>;
public sealed record GetDataSourceQuery(Guid Id) : IQuery<DataSourceDto>;
public sealed record ListDataSourcesQuery(int Page = 1, int PageSize = 50) : IQuery<PagedResult<DataSourceDto>>;
