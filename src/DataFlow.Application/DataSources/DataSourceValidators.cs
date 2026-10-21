using FluentValidation;
using FluentValidation.Results;

namespace DataFlow.Application.DataSources;

public sealed class SaveDataSourceRequestValidator : AbstractValidator<SaveDataSourceRequest>
{
    internal const string RequiredKeyField = "externalId";
    internal static readonly string[] FieldOrder = [RequiredKeyField, "name", "email", "registeredAt", "isActive"];

    public SaveDataSourceRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(200).OverridePropertyName("name");
        RuleFor(request => request.AcceptedFormats).NotEmpty().OverridePropertyName("acceptedFormats");
        RuleForEach(request => request.AcceptedFormats)
            .Must(format => format is "Csv" or "Xlsx")
            .WithMessage("Accepted formats must be Csv or Xlsx.").OverridePropertyName("acceptedFormats");
        RuleFor(request => request).Custom(ValidateSchema);
    }

    private static void ValidateSchema(SaveDataSourceRequest request, ValidationContext<SaveDataSourceRequest> context)
    {
        void Error(string path, string message) => context.AddFailure(new ValidationFailure(path, message)
        {
            ErrorCode = "invalid_schema"
        });

        var schema = request.Schema;
        if (schema is null)
        {
            Error("schema", "Schema is required.");
            return;
        }

        if (schema.KeyFields is not { Count: 1 } || schema.KeyFields[0] != RequiredKeyField)
        {
            Error("schema.keyFields", $"Key fields must be exactly [\"{RequiredKeyField}\"].");
        }

        if (schema.CsvDelimiter is not { Length: 1 })
        {
            Error("schema.csvDelimiter", "CSV delimiter must be a single character.");
        }

        if (schema.Fields is null || schema.Fields.Count == 0)
        {
            Error("schema.fields", "Schema fields are required.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < schema.Fields.Count; index++)
        {
            var field = schema.Fields[index];
            var path = $"schema.fields[{index}]";
            if (field is null)
            {
                Error(path, "Field definition is required.");
                continue;
            }

            if (field.Name is not null && !names.Add(field.Name))
            {
                Error($"{path}.name", "Field names must be unique ignoring case.");
            }

            if (!FieldOrder.Contains(field.Name, StringComparer.Ordinal))
            {
                Error($"{path}.name", "Unknown Customer field name.");
                continue;
            }

            var expectedType = field.Name switch
            {
                "email" => "email",
                "registeredAt" => "date",
                "isActive" => "boolean",
                _ => "string"
            };
            if (field.Type != expectedType)
            {
                Error($"{path}.type", $"Field type must be {expectedType}.");
            }

            if (field.Required != (field.Name != "registeredAt"))
            {
                Error($"{path}.required", "Required must match the Customer field definition.");
            }

            var maximum = field.Name switch { RequiredKeyField => 80, "name" => 200, "email" => 254, _ => 0 };
            if (maximum > 0 && (field.MaxLength is null || field.MaxLength < 1 || field.MaxLength > maximum))
            {
                Error($"{path}.maxLength", $"Max length must be between 1 and {maximum}.");
            }

            if (maximum == 0 && field.MaxLength is not null)
            {
                Error($"{path}.maxLength", "Max length is not supported for date or boolean fields.");
            }

            if (field.Name == "registeredAt" ? field.Format != "yyyy-MM-dd" : field.Format is not null)
            {
                Error($"{path}.format", "Only registeredAt supports format, which must be yyyy-MM-dd.");
            }

            if (field.RegexPattern is not null)
            {
                Error($"{path}.regexPattern", "Regex patterns are not supported in MVP.");
            }
        }

        foreach (var name in FieldOrder.Where(name => name != "registeredAt"))
        {
            if (!schema.Fields.Any(field => field?.Name == name))
            {
                Error("schema.fields", $"Required field {name} is missing.");
            }
        }
    }
}

public sealed class CreateDataSourceCommandValidator : AbstractValidator<CreateDataSourceCommand>
{
    public CreateDataSourceCommandValidator()
    {
        RuleFor(command => command.Source).SetValidator(new SaveDataSourceRequestValidator()).OverridePropertyName("");
    }
}

public sealed class UpdateDataSourceCommandValidator : AbstractValidator<UpdateDataSourceCommand>
{
    public UpdateDataSourceCommandValidator()
    {
        RuleFor(command => command.Source).SetValidator(new SaveDataSourceRequestValidator()).OverridePropertyName("");
    }
}

public sealed class ListDataSourcesQueryValidator : AbstractValidator<ListDataSourcesQuery>
{
    public ListDataSourcesQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).OverridePropertyName("page");
        RuleFor(query => query.PageSize).InclusiveBetween(1, 200).OverridePropertyName("pageSize");
    }
}
