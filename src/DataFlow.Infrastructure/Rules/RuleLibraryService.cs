using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.DataSources;
using DataFlow.Application.Rules;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Infrastructure.Rules;

public sealed record SaveRuleSetRequest(string Name, string? Description, JsonElement Definition);
public sealed record SaveRuleVersionRequest(JsonElement Definition, string? Label = null, string? ChangeNote = null, Guid? BasedOnVersionId = null);
public sealed record UpdateRuleSetRequest(string Name, string? Description, bool IsArchived, int Revision);
public sealed record RuleVersionDto(Guid Id, Guid RuleSetId, string Name, int VersionNumber, string? Label,
    string? ChangeNote, string DataType, string ContentHash, JsonElement Definition, DateTimeOffset CreatedAt);

public sealed class RuleLibraryService(DataFlowDbContext db, ICurrentUser user)
{
    public IQueryable<RuleSetVersion> AccessibleVersions => db.RuleSetVersions.Where(v =>
        db.RuleSets.Any(s => s.Id == v.RuleSetId && s.OwnerUserId == user.UserId) ||
        db.RuleVersionGrants.Any(g => g.RuleSetVersionId == v.Id && g.RecipientUserId == user.UserId) ||
        db.RuleVersionPublications.Any(p => p.RuleSetVersionId == v.Id));

    public async Task<PagedResult<RuleSet>> ListSetsAsync(int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        var query = db.RuleSets.AsNoTracking().Where(s => s.OwnerUserId == user.UserId);
        var total = await query.LongCountAsync(ct);
        return new(await Page(query.OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id), page, pageSize).ToArrayAsync(ct), page, pageSize, total);
    }

    public async Task<RuleVersionDto> CreateAsync(SaveRuleSetRequest request, CancellationToken ct)
    {
        ValidateMetadata(request.Name, request.Description);
        var parsed = RuleDefinitions.Parse(request.Definition);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var document = await DocumentAsync(parsed.Canonical, parsed.Hash, ct);
        var set = new RuleSet(user.UserId, request.Name.Trim(), request.Description, parsed.Definition.DataType);
        var version = new RuleSetVersion(set.Id, set.NextVersion(), document.Id, null, null, null);
        db.RuleSets.Add(set); db.RuleSetVersions.Add(version);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(version, set, document);
    }

    public async Task<RuleSet> UpdateAsync(Guid id, UpdateRuleSetRequest request, CancellationToken ct)
    {
        ValidateMetadata(request.Name, request.Description);
        var set = await db.RuleSets.SingleOrDefaultAsync(s => s.Id == id && s.OwnerUserId == user.UserId, ct) ?? throw Missing();
        if (set.Revision != request.Revision)
        {
            throw new DomainException("revision_conflict", "Metadata was changed by another request.");
        }

        set.Update(request.Name.Trim(), request.Description, request.IsArchived);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new DomainException("revision_conflict", "Metadata was changed by another request."); }
        return set;
    }

    public async Task<RuleVersionDto> VersionAsync(Guid id, CancellationToken ct)
    {
        var version = await AccessibleVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, ct) ?? throw Missing();
        var set = await db.RuleSets.AsNoTracking().SingleAsync(s => s.Id == version.RuleSetId, ct);
        var document = await db.RuleDocuments.AsNoTracking().SingleAsync(d => d.Id == version.RuleDocumentId, ct);
        return Dto(version, set, document);
    }

    public async Task<PagedResult<object>> VersionsAsync(Guid? setId, string scope, int page, int pageSize, CancellationToken ct)
    {
        ValidatePage(page, pageSize);
        var versions = AccessibleVersions.AsNoTracking();
        if (scope == "catalog")
        {
            versions = versions.Where(v => db.RuleVersionPublications.Any(p => p.RuleSetVersionId == v.Id));
        }

        if (scope == "library")
        {
            versions = db.RuleSetVersions.AsNoTracking().Where(v =>
            db.UserRuleLibraryEntries.Any(e => e.RuleSetVersionId == v.Id && e.UserId == user.UserId) ||
            db.RuleVersionGrants.Any(g => g.RuleSetVersionId == v.Id && g.RecipientUserId == user.UserId));
        }

        if (setId is { } id)
        {
            if (!await versions.AnyAsync(v => v.RuleSetId == id, ct))
            {
                throw Missing();
            }

            versions = versions.Where(v => v.RuleSetId == id);
        }
        var total = await versions.LongCountAsync(ct);
        var query = versions.Join(db.RuleSets, v => v.RuleSetId, s => s.Id, (v, s) => new
        {
            v.Id,
            v.RuleSetId,
            s.Name,
            s.DataType,
            v.VersionNumber,
            v.Label,
            v.ChangeNote,
            v.CreatedAt,
            s.IsArchived,
            IsAvailable = !s.IsArchived && (s.OwnerUserId == user.UserId ||
                db.RuleVersionGrants.Any(g => g.RuleSetVersionId == v.Id && g.RecipientUserId == user.UserId) ||
                db.RuleVersionPublications.Any(p => p.RuleSetVersionId == v.Id))
        }).OrderByDescending(v => v.CreatedAt).ThenBy(v => v.Id);
        return new((await Page(query, page, pageSize).ToArrayAsync(ct)).Cast<object>().ToArray(), page, pageSize, total);
    }

    public async Task<RuleVersionDto> AddVersionAsync(Guid setId, SaveRuleVersionRequest request, CancellationToken ct)
    {
        if (request.Label?.Length > 100 || request.ChangeNote?.Length > 2000)
        {
            throw Invalid("Version metadata is too long.");
        }

        var parsed = RuleDefinitions.Parse(request.Definition);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var set = await db.RuleSets.FromSqlInterpolated($"SELECT * FROM rule_sets WHERE id = {setId} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw Missing();
        if (set.OwnerUserId != user.UserId)
        {
            throw Missing();
        }

        if (set.IsArchived)
        {
            throw new DomainException("rule_set_archived", "Rule set is archived.");
        }

        if (set.DataType != parsed.Definition.DataType)
        {
            throw new DomainException("rule_type_mismatch", "Rule set type cannot change.");
        }

        if (request.BasedOnVersionId is { } based && !await AccessibleVersions.AnyAsync(v => v.Id == based, ct))
        {
            throw Missing();
        }

        var document = await DocumentAsync(parsed.Canonical, parsed.Hash, ct);
        // Historical revisions may legitimately share the same executable document.
        var existing = await db.RuleSetVersions.Where(v => v.RuleSetId == setId && v.RuleDocumentId == document.Id)
            .OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Dto(existing, set, document);
        }

        var version = new RuleSetVersion(setId, set.NextVersion(), document.Id, request.Label, request.ChangeNote, request.BasedOnVersionId);
        db.RuleSetVersions.Add(version);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(version, set, document);
    }

    public async Task<RuleVersionDto> ForkAsync(Guid id, string name, CancellationToken ct)
    {
        var original = await VersionAsync(id, ct);
        ValidateMetadata(name, null);
        var version = await db.RuleSetVersions.SingleAsync(v => v.Id == id, ct);
        var set = new RuleSet(user.UserId, name.Trim(), null, original.DataType);
        var copy = new RuleSetVersion(set.Id, set.NextVersion(), version.RuleDocumentId, null, "Copied from an accessible version.", id);
        db.RuleSets.Add(set); db.RuleSetVersions.Add(copy); await db.SaveChangesAsync(ct);
        return Dto(copy, set, await db.RuleDocuments.SingleAsync(d => d.Id == version.RuleDocumentId, ct));
    }

    // All access changes serialize on the parent set, as does accepting a new job.
    public async Task SetAccessAsync(Guid id, Guid? recipient, bool publication, bool enabled, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var version = await db.RuleSetVersions.SingleOrDefaultAsync(v => v.Id == id, ct) ?? throw Missing();
        var set = await db.RuleSets.FromSqlInterpolated($"SELECT * FROM rule_sets WHERE id = {version.RuleSetId} FOR UPDATE").SingleAsync(ct);
        if (set.OwnerUserId != user.UserId)
        {
            throw Missing();
        }

        if (publication)
        {
            var item = await db.RuleVersionPublications.FindAsync([id], ct);
            if (enabled && item is null)
            {
                db.RuleVersionPublications.Add(new() { RuleSetVersionId = id });
            }

            if (!enabled && item is not null)
            {
                db.RuleVersionPublications.Remove(item);
            }
        }
        else
        {
            if (recipient is null || !await db.Users.AnyAsync(u => u.Id == recipient, ct))
            {
                throw new DomainException("share_recipient_not_found", "Recipient was not found.");
            }

            var item = await db.RuleVersionGrants.FindAsync([id, recipient.Value], ct);
            if (enabled && item is null)
            {
                db.RuleVersionGrants.Add(new() { RuleSetVersionId = id, RecipientUserId = recipient.Value });
            }

            if (!enabled && item is not null)
            {
                db.RuleVersionGrants.Remove(item);
            }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task BookmarkAsync(Guid id, bool enabled, CancellationToken ct)
    {
        if (enabled)
        {
            _ = await VersionAsync(id, ct);
        }

        var item = await db.UserRuleLibraryEntries.FindAsync([user.UserId, id], ct);
        if (enabled && item is null)
        {
            db.UserRuleLibraryEntries.Add(new() { UserId = user.UserId, RuleSetVersionId = id });
        }

        if (!enabled && item is not null)
        {
            db.UserRuleLibraryEntries.Remove(item);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<RuleDocument> DocumentAsync(string canonical, string hash, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({hash}))", ct);
        var document = await db.RuleDocuments.SingleOrDefaultAsync(d => d.EngineVersion == "rules-v1" && d.ContentHash == hash, ct);
        if (document is not null)
        {
            var parsed = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(document.DefinitionJson));
            if (parsed.Canonical != canonical)
            {
                throw new InvalidOperationException("Rule document hash collision.");
            }

            return document;
        }
        document = new(hash, canonical); db.RuleDocuments.Add(document); return document;
    }
    private static RuleVersionDto Dto(RuleSetVersion v, RuleSet s, RuleDocument d) =>
        new(v.Id, s.Id, s.Name, v.VersionNumber, v.Label, v.ChangeNote, s.DataType, d.ContentHash,
            JsonSerializer.Deserialize<JsonElement>(d.DefinitionJson), v.CreatedAt);
    private static void ValidateMetadata(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || description?.Length > 2000)
        {
            throw Invalid("Invalid name or description.");
        }
    }
    private static void ValidatePage(int page, int size)
    {
        if (page < 1 || size is < 1 or > 200)
        {
            throw Invalid("Invalid pagination.");
        }
    }
    private static IQueryable<T> Page<T>(IQueryable<T> query, int page, int size) => ((long)page - 1) * size > int.MaxValue
        ? query.Take(0) : query.Skip((page - 1) * size).Take(size);
    private static DomainException Missing() => new("rule_version_not_found", "Rule set or version was not found.");
    private static DomainException Invalid(string text) => new("invalid_rule_definition", text);
}
