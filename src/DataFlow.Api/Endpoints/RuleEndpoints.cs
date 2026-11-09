using System.Text.Json;
using DataFlow.Application.Rules;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Rules;
using Microsoft.AspNetCore.Identity;

namespace DataFlow.Api.Endpoints;

public static class RuleEndpoints
{
    public sealed record ShareRequest(string Email);
    public sealed record ForkRequest(string Name);
    public static void MapRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1").RequireAuthorization().WithTags("Rules");
        group.MapPost("/rule-definitions/validate", (JsonElement definition) =>
        {
            var parsed = RuleDefinitions.Parse(definition);
            return Results.Ok(new { definition = parsed.Definition, contentHash = parsed.Hash });
        });
        group.MapGet("/rule-sets", (RuleLibraryService service, CancellationToken ct, int page = 1, int pageSize = 50) => service.ListSetsAsync(page, pageSize, ct));
        group.MapPost("/rule-sets", async (SaveRuleSetRequest request, RuleLibraryService service, CancellationToken ct) =>
        {
            var version = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/rule-set-versions/{version.Id}", version);
        });
        group.MapPatch("/rule-sets/{id:guid}", (Guid id, UpdateRuleSetRequest request, RuleLibraryService service, CancellationToken ct) => service.UpdateAsync(id, request, ct));
        group.MapGet("/rule-sets/{id:guid}/versions", (Guid id, RuleLibraryService service, CancellationToken ct, int page = 1, int pageSize = 50) => service.VersionsAsync(id, "accessible", page, pageSize, ct));
        group.MapPost("/rule-sets/{id:guid}/versions", (Guid id, SaveRuleVersionRequest request, RuleLibraryService service, CancellationToken ct) => service.AddVersionAsync(id, request, ct));
        group.MapGet("/rule-set-versions/{id:guid}", (Guid id, RuleLibraryService service, CancellationToken ct) => service.VersionAsync(id, ct));
        group.MapGet("/rule-set-versions/{id:guid}/export", async (Guid id, RuleLibraryService service, CancellationToken ct) =>
        {
            var version = await service.VersionAsync(id, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(version.Definition.GetRawText()), "application/json", $"rules-{id:N}.json");
        });
        group.MapPost("/rule-set-versions/{id:guid}/fork", (Guid id, ForkRequest request, RuleLibraryService service, CancellationToken ct) => service.ForkAsync(id, request.Name, ct));
        group.MapPost("/rule-set-versions/{id:guid}/grants", async (Guid id, ShareRequest request, UserManager<ApplicationUser> users, RuleLibraryService service, CancellationToken ct) =>
        {
            var recipient = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email);
            if (recipient is null)
            {
                throw new DomainException("share_recipient_not_found", "Recipient was not found.");
            }

            await service.SetAccessAsync(id, recipient.Id, false, true, ct); return Results.NoContent();
        });
        group.MapDelete("/rule-set-versions/{id:guid}/grants/{userId:guid}", async (Guid id, Guid userId, RuleLibraryService service, CancellationToken ct) =>
        { await service.SetAccessAsync(id, userId, false, false, ct); return Results.NoContent(); });
        group.MapPut("/rule-set-versions/{id:guid}/publication", async (Guid id, RuleLibraryService service, CancellationToken ct) =>
        { await service.SetAccessAsync(id, null, true, true, ct); return Results.NoContent(); });
        group.MapDelete("/rule-set-versions/{id:guid}/publication", async (Guid id, RuleLibraryService service, CancellationToken ct) =>
        { await service.SetAccessAsync(id, null, true, false, ct); return Results.NoContent(); });
        group.MapGet("/rule-catalog", (RuleLibraryService service, CancellationToken ct, int page = 1, int pageSize = 50) => service.VersionsAsync(null, "catalog", page, pageSize, ct));
        group.MapGet("/rule-library", (RuleLibraryService service, CancellationToken ct, int page = 1, int pageSize = 50) => service.VersionsAsync(null, "library", page, pageSize, ct));
        group.MapPut("/rule-library/{id:guid}", async (Guid id, RuleLibraryService service, CancellationToken ct) =>
        { await service.BookmarkAsync(id, true, ct); return Results.NoContent(); });
        group.MapDelete("/rule-library/{id:guid}", async (Guid id, RuleLibraryService service, CancellationToken ct) =>
        { await service.BookmarkAsync(id, false, ct); return Results.NoContent(); });
    }
}
