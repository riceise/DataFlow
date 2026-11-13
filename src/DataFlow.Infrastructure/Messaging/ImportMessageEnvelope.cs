using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataFlow.Infrastructure.Messaging;

public sealed record ImportMessageEnvelope(
    string ContractVersion,
    Guid MessageId,
    string Type,
    DateTimeOffset OccurredAt,
    int Attempt,
    string TraceParent,
    JsonElement Payload)
{
    public string? TraceState { get; init; }
    public const string SupportedVersion = "v1";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static ImportMessageEnvelope Create(ClaimedOutbox message) => new(
        SupportedVersion, message.Id, message.Type, message.OccurredAt.ToUniversalTime(),
        JsonSerializer.Deserialize<JsonElement>(message.Payload, JsonOptions).TryGetProperty("attempt", out var attempt)
             ? attempt.GetInt32() : 1, Activity.Current?.Id ?? message.TraceParent ?? string.Empty,
        JsonSerializer.Deserialize<JsonElement>(message.Payload, JsonOptions))
    { TraceState = Activity.Current?.TraceStateString ?? message.TraceState };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            Encoder = JavaScriptEncoder.Default,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public sealed record ImportJobCreatedPayload(Guid JobId, Guid DataSourceId, int SchemaVersion, int RunVersion);
