using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.Application;

public static class ApplicationExtensions
{
    public static IServiceCollection AddDataFlowApplication(this IServiceCollection services)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<JobQueries>();
        services.AddScoped<IRequestHandler<GetJobQuery, JobDto>>(p => p.GetRequiredService<JobQueries>());
        services.AddScoped<IRequestHandler<ListJobsQuery, PagedResult<JobDto>>>(p => p.GetRequiredService<JobQueries>());
        services.AddScoped<IValidator<ListJobsQuery>, ListJobsValidator>();
        services.AddScoped<IRequestHandler<ListJobRowsQuery, PagedResult<JobRowDto>>, JobRowQueries>();
        services.AddScoped<IValidator<ListJobRowsQuery>, ListJobRowsValidator>();
        services.AddScoped<DataSourceHandlers>();
        services.AddScoped<IRequestHandler<CreateDataSourceCommand, DataSourceDto>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IRequestHandler<UpdateDataSourceCommand, DataSourceDto>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IRequestHandler<ActivateDataSourceCommand, DataSourceDto>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IRequestHandler<DeactivateDataSourceCommand, DataSourceDto>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IRequestHandler<GetDataSourceQuery, DataSourceDto>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IRequestHandler<ListDataSourcesQuery, PagedResult<DataSourceDto>>>(provider => provider.GetRequiredService<DataSourceHandlers>());
        services.AddScoped<IValidator<CreateDataSourceCommand>, CreateDataSourceCommandValidator>();
        services.AddScoped<IValidator<UpdateDataSourceCommand>, UpdateDataSourceCommandValidator>();
        services.AddScoped<IValidator<ListDataSourcesQuery>, ListDataSourcesQueryValidator>();
        return services;
    }
}
