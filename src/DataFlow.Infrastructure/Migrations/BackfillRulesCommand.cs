using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Rules;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataFlow.Infrastructure.Migrations;

public sealed class BackfillRulesCommand(DataFlowDbContext database, ILogger<BackfillRulesCommand> logger)
{
    public async Task<BackfillResult> ExecuteAsync(Guid assignedOwnerId, CancellationToken cancellationToken)
    {
        if (!await database.Users.AnyAsync(u => u.Id == assignedOwnerId, cancellationToken))
        {
            throw new InvalidOperationException($"User {assignedOwnerId} not found. Create the user first.");
        }

        // Include previously assigned legacy sources; do not claim another user's data.
        var unconfiguredIds = await database.DataSources.AsNoTracking()
            .Where(s => s.DefaultRuleSetVersionId == null && (s.OwnerUserId == null || s.OwnerUserId == assignedOwnerId))
            .Select(s => s.Id).ToListAsync(cancellationToken);
        var partiallyPinnedIds = await database.ImportJobs.AsNoTracking()
            .Where(j => j.Parts.Count == 1).Where(NeedsBackfillExpression)
            .Join(database.DataSources.AsNoTracking(), j => j.DataSourceId, s => s.Id, (j, s) => new { j, s })
            .Where(x => x.s.OwnerUserId == null || x.s.OwnerUserId == assignedOwnerId)
            .Select(x => x.j.DataSourceId).ToListAsync(cancellationToken);
        var ids = unconfiguredIds.Union(partiallyPinnedIds).OrderBy(id => id).ToArray();
        var setsCreated = 0;
        var versionsCreated = 0;
        var documentsCreated = 0;
        var jobsLinked = 0;
        var conflicts = new List<Guid>();

        foreach (var id in ids)
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var source = await database.DataSources
                    .FromSqlInterpolated($"SELECT * FROM data_sources WHERE id = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
                // A competing run may already have committed this source.
                if (source is null || (source.OwnerUserId is not null && source.OwnerUserId != assignedOwnerId))
                {
                    continue;
                }

                if (source.DataType != DataSourceDataType.Customer)
                {
                    throw new InvalidOperationException($"Source {id} is not a legacy Customer source.");
                }

                var jobs = await database.ImportJobs.Where(j => j.DataSourceId == id)
                    .OrderBy(j => j.Id).ToListAsync(cancellationToken);
                var jobsToMigrate = jobs.Where(j => j.Parts.Count == 1 && NeedsBackfill(j)).ToArray();
                if (source.DefaultRuleSetVersionId is not null && jobsToMigrate.Length == 0)
                {
                    continue;
                }

                foreach (var job in jobsToMigrate)
                {
                    ValidateReferences(job);
                }

                var singlePartJobs = jobs.Where(j => j.Parts.Count == 1).ToArray();
                var schemas = singlePartJobs.Select(j => (Number: j.SchemaVersion, Json: j.SchemaJson))
                    .Append((Number: source.SchemaVersion, Json: source.SchemaJson))
                    .Select(s => (s.Number, Canonical: Canonical(s.Json)))
                    .GroupBy(s => s.Number).OrderBy(g => g.Key).ToArray();
                var conflictingVersions = schemas.Where(g => g.Select(s => s.Canonical).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    .Select(g => g.Key).ToArray();
                if (conflictingVersions.Length > 0)
                {
                    conflicts.Add(id);
                    logger.LogError("Source {SourceId} has conflicting definitions for schema versions {Versions}",
                        id, string.Join(", ", conflictingVersions));
                    continue;
                }

                var newDocuments = 0;
                var newVersions = 0;
                var documents = new Dictionary<Guid, RuleDocument>();
                var versionCache = new Dictionary<Guid, RuleSetVersion>();
                var setCache = new Dictionary<Guid, RuleSet>();
                RuleSet set;
                var versions = new Dictionary<int, RuleSetVersion>();
                var isNewSet = source.DefaultRuleSetVersionId is null;
                if (isNewSet)
                {
                    source.AssignOwner(assignedOwnerId);
                    var setName = $"Legacy rules for {source.Name}";
                    set = new RuleSet(assignedOwnerId, setName[..Math.Min(200, setName.Length)],
                        "Migrated current schema and historical job snapshots.", "Customer");
                    database.RuleSets.Add(set);
                    setCache.Add(set.Id, set);
                }
                else
                {
                    var defaultVersion = await database.RuleSetVersions.SingleAsync(v => v.Id == source.DefaultRuleSetVersionId, cancellationToken);
                    set = await database.RuleSets.SingleAsync(s => s.Id == defaultVersion.RuleSetId, cancellationToken);
                    setCache.Add(set.Id, set);
                    versions = await database.RuleSetVersions.Where(v => v.RuleSetId == set.Id).ToDictionaryAsync(v => v.VersionNumber, cancellationToken);
                    foreach (var version in versions.Values)
                    {
                        versionCache.Add(version.Id, version);
                    }
                }

                async Task<RuleDocument> GetDocumentAsync(Guid documentId)
                {
                    if (documents.TryGetValue(documentId, out var cached))
                    {
                        return cached;
                    }

                    var loaded = await database.RuleDocuments.SingleAsync(d => d.Id == documentId, cancellationToken);
                    documents.Add(documentId, loaded);
                    return loaded;
                }

                async Task<RuleSetVersion> GetVersionAsync(Guid versionId)
                {
                    if (versionCache.TryGetValue(versionId, out var cached))
                    {
                        return cached;
                    }

                    var loaded = await database.RuleSetVersions.SingleAsync(v => v.Id == versionId, cancellationToken);
                    versionCache.Add(versionId, loaded);
                    return loaded;
                }

                async Task<RuleSet> GetSetAsync(Guid setId)
                {
                    if (setCache.TryGetValue(setId, out var cached))
                    {
                        return cached;
                    }

                    var loaded = await database.RuleSets.SingleAsync(s => s.Id == setId, cancellationToken);
                    setCache.Add(setId, loaded);
                    return loaded;
                }

                async Task<RuleSetVersion> CreateVersionAsync(int number, string canonical)
                {
                    if (set.OwnerUserId != assignedOwnerId || number <= set.LastVersionNumber)
                    {
                        throw new DomainException("rule_reference_conflict",
                            $"Cannot add historical schema version {number} to rule set {set.Id}.");
                    }

                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                    await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({hash}))", cancellationToken);
                    var document = await database.RuleDocuments.SingleOrDefaultAsync(d =>
                        d.DefinitionFormatVersion == 1 && d.EngineVersion == "legacy-customer-v1" && d.ContentHash == hash, cancellationToken);
                    if (document is null)
                    {
                        document = new RuleDocument(hash, canonical, "legacy-customer-v1");
                        database.RuleDocuments.Add(document);
                        // Flush for deduplication against later historical revisions in this transaction.
                        await database.SaveChangesAsync(cancellationToken);
                        newDocuments++;
                    }
                    else if (Canonical(document.DefinitionJson) != canonical)
                    {
                        throw new InvalidOperationException("Historical rule document hash collision.");
                    }

                    documents[document.Id] = document;
                    set.RegisterHistoricalVersion(number);
                    var created = new RuleSetVersion(set.Id, number, document.Id,
                        $"Migrated v{number}", $"Historical schema version {number}", null);
                    database.RuleSetVersions.Add(created);
                    versions.Add(number, created);
                    versionCache.Add(created.Id, created);
                    newVersions++;
                    return created;
                }

                if (isNewSet)
                {
                    foreach (var schema in schemas)
                    {
                        await CreateVersionAsync(schema.Key, schema.First().Canonical);
                    }

                    source.SelectRules(versions[source.SchemaVersion].Id, source.DataType);
                }

                foreach (var job in jobsToMigrate)
                {
                    var part = job.Parts[0];
                    RuleSetVersion version;
                    RuleDocument document;
                    if (job.RuleSetVersionId is not null)
                    {
                        version = await GetVersionAsync(job.RuleSetVersionId.Value);
                        document = await GetDocumentAsync(job.RuleDocumentId!.Value);
                    }
                    else if (part.RuleSetVersionId is not null)
                    {
                        version = await GetVersionAsync(part.RuleSetVersionId.Value);
                        document = await GetDocumentAsync(part.RuleDocumentId!.Value);
                    }
                    else
                    {
                        if (!versions.TryGetValue(job.SchemaVersion, out version!))
                        {
                            // Existing sets cannot retroactively insert a number below their current LastVersionNumber.
                            // This is a conflict, not permission to renumber either the set or the historical job.
                            version = await CreateVersionAsync(job.SchemaVersion, Canonical(job.SchemaJson));
                        }
                        document = await GetDocumentAsync(version.RuleDocumentId);
                        if (!isNewSet && Canonical(document.DefinitionJson) != Canonical(job.SchemaJson))
                        {
                            throw new DomainException("rule_reference_conflict",
                                $"Default rule version {version.Id} does not match historical schema of job {job.Id}.");
                        }
                    }

                    var targetSet = await GetSetAsync(version.RuleSetId);
                    var name = job.RuleSetNameAtSubmission ?? part.RuleSetNameAtSubmission ?? targetSet.Name;
                    var number = job.RuleVersionNumberAtSubmission ?? part.RuleVersionNumberAtSubmission ?? version.VersionNumber;
                    var format = Path.GetExtension(job.OriginalFileName).ToLowerInvariant() switch
                    {
                        ".csv" => AcceptedFileFormat.Csv,
                        ".xlsx" => AcceptedFileFormat.Xlsx,
                        ".pdf" => AcceptedFileFormat.Pdf,
                        ".zip" => AcceptedFileFormat.Zip,
                        _ => AcceptedFileFormat.None
                    };
                    var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                    { version = 2, userId = assignedOwnerId, sourceId = id, job.FileSha256, Format = format, RuleSetVersionId = (Guid?)version.Id })));
                    job.MigrateLegacyRules(version.Id, version.RuleDocumentId, document.ContentHash, fingerprint, name, number);
                }

                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (isNewSet)
                {
                    setsCreated++;
                }
                versionsCreated += newVersions;
                documentsCreated += newDocuments;
                jobsLinked += jobsToMigrate.Length;
                logger.LogInformation("Migrated source {SourceId}: {Versions} versions, {Jobs} jobs", id, newVersions, jobsToMigrate.Length);
            }
            catch (DomainException exception) when (exception.Code is "rule_reference_conflict" or "rules_already_pinned")
            {
                await transaction.RollbackAsync(cancellationToken);
                conflicts.Add(id);
                logger.LogError(exception, "Source {SourceId} has incompatible pinned rule references", id);
            }
            finally
            {
                // Also clears entities whose SaveChanges succeeded inside a subsequently rolled-back transaction.
                database.ChangeTracker.Clear();
            }
        }

        logger.LogInformation("Backfill committed {Sets} sets, {Versions} versions, {Documents} documents, {Jobs} jobs",
            setsCreated, versionsCreated, documentsCreated, jobsLinked);
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException($"Backfill skipped conflicting sources: {string.Join(", ", conflicts.Distinct())}. Resolve them and rerun.");
        }

        return new(setsCreated, versionsCreated, documentsCreated, jobsLinked);
    }

    private static readonly Expression<Func<ImportJob, bool>> NeedsBackfillExpression = job =>
        job.RuleSetVersionId == null || job.RuleDocumentId == null || job.ExecutionFingerprint == null ||
        job.RuleSetNameAtSubmission == null || job.RuleVersionNumberAtSubmission == null ||
        job.Parts.Any(p => p.RuleSetVersionId == null || p.RuleDocumentId == null || p.RuleSnapshotHash == null ||
            p.RuleSetNameAtSubmission == null || p.RuleVersionNumberAtSubmission == null);

    private static bool NeedsBackfill(ImportJob job) =>
        job.RuleSetVersionId is null || job.RuleDocumentId is null || job.ExecutionFingerprint is null ||
        job.RuleSetNameAtSubmission is null || job.RuleVersionNumberAtSubmission is null ||
        job.Parts.Any(p => p.RuleSetVersionId is null || p.RuleDocumentId is null || p.RuleSnapshotHash is null ||
            p.RuleSetNameAtSubmission is null || p.RuleVersionNumberAtSubmission is null);

    private static void ValidateReferences(ImportJob job)
    {
        var part = job.Parts[0];
        if ((job.RuleSetVersionId is null) != (job.RuleDocumentId is null) ||
            (part.RuleSetVersionId is null) != (part.RuleDocumentId is null) ||
            (part.RuleSetVersionId is null) != (part.RuleSnapshotHash is null))
        {
            throw new DomainException("rule_reference_conflict", $"Job {job.Id} has incomplete rule references.");
        }

        if (job.RuleSetVersionId is not null && part.RuleSetVersionId is not null &&
            (job.RuleSetVersionId != part.RuleSetVersionId || job.RuleDocumentId != part.RuleDocumentId))
        {
            throw new DomainException("rule_reference_conflict", $"Job {job.Id} and its part have different rule references.");
        }
    }

    private static string Canonical(string json) =>
        RuleDefinitions.CanonicalizeHistorical(JsonSerializer.Deserialize<JsonElement>(json));
}

public sealed record BackfillResult(int SetsCreated, int VersionsCreated, int DocumentsCreated, int JobsLinked);
