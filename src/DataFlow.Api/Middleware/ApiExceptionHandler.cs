using System.Text.Json;
using DataFlow.Application.Rules;
using DataFlow.Application.Validation;
using DataFlow.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DataFlow.Api.Middleware;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = StatusCodes.Status500InternalServerError;
        var code = "internal_error";
        IDictionary<string, string[]>? errors = null;

        switch (exception)
        {
            case RuleDefinitionValidationException definition:
                status = StatusCodes.Status400BadRequest;
                code = definition.Code;
                errors = definition.Issues.GroupBy(issue => issue.Path)
                    .ToDictionary(group => group.Key, group => group.Select(issue => $"{issue.Message} {issue.Hint}").ToArray());
                break;
            case RequestValidationException validation:
                status = StatusCodes.Status400BadRequest;
                code = validation.Code;
                errors = validation.Errors;
                break;
            case DomainException domain:
                if (domain.Code is "invalid_rule_definition" or "rule_version_not_found" or "revision_conflict" or
                    "rule_set_archived" or "rule_type_mismatch" or "share_recipient_not_found" or "source_inactive")
                {
                    code = domain.Code;
                    status = domain.Code is "rule_version_not_found" or "share_recipient_not_found" ? 404 :
                        domain.Code is "revision_conflict" or "rule_set_archived" ? 409 : 400;
                    errors = new Dictionary<string, string[]> { ["definition"] = [domain.Message] };
                    break;
                }
                if (domain.Code is "job_not_retryable" or "job_not_cancellable")
                {
                    status = StatusCodes.Status409Conflict;
                    code = domain.Code;
                    break;
                }
                if (domain.Code is "job_not_found" or "idempotency_conflict" or "upload_incomplete"
                    or "invalid_idempotency_key" or "invalid_multipart" or "unexpected_form_field" or "invalid_file_name"
                    or "file_format_not_allowed" or "file_too_large" or "empty_file" or "invalid_file_format")
                {
                    status = domain.Code == "job_not_found" ? 404 : domain.Code is "idempotency_conflict" or "upload_incomplete" ? 409 : 400;
                    code = domain.Code;
                    break;
                }
                status = domain.Code == "source_not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                code = domain.Code == "source_not_found" ? "source_not_found"
                    : domain.Code.Contains("schema", StringComparison.Ordinal) || domain.Code == "invalid_csv_delimiter"
                        ? "invalid_schema" : "validation_failed";
                if (code == "invalid_schema")
                {
                    errors = new Dictionary<string, string[]> { ["schema"] = ["Schema is invalid."] };
                }

                break;
            case BadHttpRequestException badRequest:
                status = StatusCodes.Status400BadRequest;
                var json = badRequest.InnerException as JsonException;
                var path = json?.Path?.TrimStart('$', '.') ?? "request";
                code = path == "schema" || path.StartsWith("schema.", StringComparison.Ordinal)
                    ? "invalid_schema" : "validation_failed";
                errors = new Dictionary<string, string[]> { [path] = ["The request value is invalid."] };
                break;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Type = $"https://www.rfc-editor.org/rfc/rfc9110#section-{(status == 404 ? "15.5.5" : status == 409 ? "15.5.10" : status == 400 ? "15.5.1" : "15.6.1")}",
            Title = code switch
            {
                "source_not_found" => "Data source was not found.",
                "source_inactive" => "Data source is inactive.",
                "job_not_found" => "Job was not found.",
                "job_not_retryable" => "Job cannot be retried.",
                "job_not_cancellable" => "Job cannot be cancelled.",
                "idempotency_conflict" => "Idempotency key has different content.",
                "upload_incomplete" => "Previous upload with this key has not completed.",
                "invalid_idempotency_key" => "Idempotency-Key header is required and limited to 128 characters.",
                "invalid_multipart" => "Multipart upload payload is invalid.",
                "unexpected_form_field" => "Multipart upload must contain exactly one file.",
                "invalid_file_name" => "File name is invalid.",
                "file_format_not_allowed" => "File format is not allowed for this data source.",
                "file_too_large" => "File exceeds the maximum allowed size.",
                "empty_file" => "Uploaded file cannot be empty.",
                "invalid_file_format" => "File content does not match the declared format.",
                "rule_version_not_found" => "Rule version was not found.",
                "rule_set_archived" => "Rule set is archived.",
                "rule_type_mismatch" => "Rule set data type does not match the data source.",
                "invalid_rule_definition" => "Rule definition is invalid.",
                "revision_conflict" => "Rule set was modified concurrently.",
                "share_recipient_not_found" => "Recipient user was not found.",
                "invalid_schema" => "Data source schema is invalid.",
                "validation_failed" => "Request validation failed.",
                _ => status == 400 ? "File upload request is invalid." : "An unexpected error occurred."
            },
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = code;
        if (exception is RuleDefinitionValidationException ruleValidation)
        {
            problem.Extensions["issues"] = ruleValidation.Issues;
        }
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        httpContext.Response.StatusCode = status;
        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
        return true;
    }
}
