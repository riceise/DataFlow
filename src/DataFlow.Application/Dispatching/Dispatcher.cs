using DataFlow.Application.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.Application.Dispatching;

public interface IRequest<TResponse>;
public interface ICommand<TResponse> : IRequest<TResponse>;
public interface IQuery<TResponse> : IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>;

public interface IDispatcher
{
    Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>;
}

internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    public async Task<TResponse> DispatchAsync<TRequest, TResponse>(
        TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest<TResponse>
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in services.GetServices<IValidator<TRequest>>())
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
        {
            throw new RequestValidationException(failures);
        }

        return await services.GetRequiredService<IRequestHandler<TRequest, TResponse>>()
            .HandleAsync(request, cancellationToken);
    }
}
