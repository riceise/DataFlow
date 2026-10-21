using DataFlow.Application.Validation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.Application.Dispatching;

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
