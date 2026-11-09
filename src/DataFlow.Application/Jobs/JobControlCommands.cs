using DataFlow.Application.Dispatching;

namespace DataFlow.Application.Jobs;

public sealed record RetryJobCommand(Guid Id) : ICommand<JobAcceptedDto>;
public sealed record CancelJobCommand(Guid Id) : ICommand<JobAcceptedDto>;
