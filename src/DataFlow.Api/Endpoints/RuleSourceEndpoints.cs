using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Rules;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Api.Endpoints;

public static class RuleSourceEndpoints
{
    public sealed record CreateRuleSource(string Name, Guid RuleSetVersionId);
    public sealed record SelectRuleVersion(Guid RuleSetVersionId);
    public static void MapRuleSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1").RequireAuthorization().WithTags("Data sources");
        group.MapPost("/rule-sources", async (CreateRuleSource request, RuleLibraryService rules, DataFlowDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            {
                throw new DomainException("invalid_rule_definition", "Source name is required and limited to 200 characters.");
            }

            var version = await rules.VersionAsync(request.RuleSetVersionId, ct);
            var set = await db.RuleSets.SingleAsync(s => s.Id == version.RuleSetId, ct);
            if (set.IsArchived)
            {
                throw new DomainException("rule_set_archived", "Rule set is archived.");
            }
            // Legacy storage remains until historical backfill is verified. New workers use the pinned document.
            var placeholder = new DataSourceSchema(1, ["externalId"], [new DataSourceFieldDefinition("externalId", "string", true)]);
            var source = DataSource.Create(request.Name, Enum.Parse<DataSourceDataType>(version.DataType), AcceptedFileFormat.All,
                placeholder, DateTimeOffset.UtcNow);
            source.AssignOwner(user.UserId); source.SelectRules(version.Id, Enum.Parse<DataSourceDataType>(version.DataType));
            db.DataSources.Add(source); await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/data-sources/{source.Id}", new { source.Id, source.Name, dataType = source.DataType.ToString(), source.DefaultRuleSetVersionId });
        });
        group.MapPut("/data-sources/{id:guid}/rule-version", async (Guid id, SelectRuleVersion request, RuleLibraryService rules, DataFlowDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var source = await db.DataSources.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == user.UserId, ct)
                ?? throw new DomainException("source_not_found", "Data source was not found.");
            var version = await rules.VersionAsync(request.RuleSetVersionId, ct);
            if (version.DataType != source.DataType.ToString())
            {
                throw new DomainException("rule_type_mismatch", "Rules and source types differ.");
            }

            source.SelectRules(version.Id, source.DataType); await db.SaveChangesAsync(ct);
            return Results.Ok(new { source.Id, source.DefaultRuleSetVersionId });
        });
        group.MapGet("/jobs/{id:guid}/rule-definition", async (Guid id, DataFlowDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var job = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id &&
                db.DataSources.Any(s => s.Id == j.DataSourceId && s.OwnerUserId == user.UserId), ct)
                ?? throw new DomainException("job_not_found", "Job was not found.");
            var json = job.RuleDocumentId is { } doc ? await db.RuleDocuments.Where(d => d.Id == doc).Select(d => d.DefinitionJson).SingleAsync(ct) : job.SchemaJson;
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(json));
        });
    }
}
