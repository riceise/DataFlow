using System.ComponentModel.DataAnnotations;

namespace DataFlow.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required(AllowEmptyStrings = false, ErrorMessage = "ConnectionStrings:DataFlow is required.")]
    [MinLength(10, ErrorMessage = "ConnectionStrings:DataFlow must be a valid connection string.")]
    public string DataFlow { get; init; } = string.Empty;
}
