namespace DataFlow.Application.Dispatching;

public interface IDispatcher
{
    Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>;
}
