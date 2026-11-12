using DataFlow.Domain.Exceptions;

namespace DataFlow.Application.Rules;

public sealed record RuleDefinitionIssue(string Code, string Path, string Message, string Hint, string? FieldName = null);

public sealed class RuleDefinitionValidationException(IReadOnlyList<RuleDefinitionIssue> issues)
    : DomainException("invalid_rule_definition", "Определение правил некорректно.")
{
    public IReadOnlyList<RuleDefinitionIssue> Issues { get; } = issues;
}
