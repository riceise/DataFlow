using FluentValidation.Results;

namespace DataFlow.Application.Validation;

public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyList<ValidationFailure> failures)
        : base("The request is invalid.")
    {
        Code = failures.Any(failure => failure.ErrorCode == "invalid_schema")
            ? "invalid_schema"
            : "validation_failed";
        Errors = failures.GroupBy(failure => failure.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());
    }

    public string Code { get; }
    public IDictionary<string, string[]> Errors { get; }
}
