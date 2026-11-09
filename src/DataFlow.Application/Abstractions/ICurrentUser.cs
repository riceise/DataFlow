namespace DataFlow.Application.Abstractions;

public interface ICurrentUser
{
    Guid UserId { get; }
}
