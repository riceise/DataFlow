using System.Text.Json;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Application.Rules;

public static partial class RuleDefinitions
{
    /// <summary>
    /// Canonicalizes legacy Customer schema JSON for fingerprinting.
    /// The schema's revision is metadata, not executable content. Preserve key and field
    /// order and every supported constraint, including regex and unknown-column policy.
    /// </summary>
    public static string CanonicalizeHistorical(JsonElement schema)
    {
        return DataSourceSchema.FromJson(schema.GetRawText()).WithVersion(1).ToJson();
    }
}
