namespace DataFlow.Application.Dispatching;

public interface ICommand<TResponse> : IRequest<TResponse>;
