using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DataFlow.Api.Endpoints;

public static class DataSourceEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/data-sources").WithTags("Data sources")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("", async Task<Created<DataSourceDto>> (
            SaveDataSourceRequest request, IDispatcher dispatcher, CancellationToken cancellationToken) =>
        {
            var source = await dispatcher.DispatchAsync<CreateDataSourceCommand, DataSourceDto>(new(request), cancellationToken);
            return TypedResults.Created($"/api/v1/data-sources/{source.Id}", source);
        });

        group.MapGet("", async Task<Ok<PagedResult<DataSourceDto>>> (
            IDispatcher dispatcher, CancellationToken cancellationToken, int page = 1, int pageSize = 50) =>
            TypedResults.Ok(await dispatcher.DispatchAsync<ListDataSourcesQuery, PagedResult<DataSourceDto>>(
                new(page, pageSize), cancellationToken)));

        group.MapGet("/{id:guid}", async Task<Ok<DataSourceDto>> (
            Guid id, IDispatcher dispatcher, CancellationToken cancellationToken) =>
            TypedResults.Ok(await dispatcher.DispatchAsync<GetDataSourceQuery, DataSourceDto>(new(id), cancellationToken)))
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", async Task<Ok<DataSourceDto>> (
            Guid id, SaveDataSourceRequest request, IDispatcher dispatcher, CancellationToken cancellationToken) =>
            TypedResults.Ok(await dispatcher.DispatchAsync<UpdateDataSourceCommand, DataSourceDto>(new(id, request), cancellationToken)))
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async Task<Ok<DataSourceDto>> (
            Guid id, IDispatcher dispatcher, CancellationToken cancellationToken) =>
            TypedResults.Ok(await dispatcher.DispatchAsync<ActivateDataSourceCommand, DataSourceDto>(new(id), cancellationToken)))
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async Task<Ok<DataSourceDto>> (
            Guid id, IDispatcher dispatcher, CancellationToken cancellationToken) =>
            TypedResults.Ok(await dispatcher.DispatchAsync<DeactivateDataSourceCommand, DataSourceDto>(new(id), cancellationToken)))
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }
}
