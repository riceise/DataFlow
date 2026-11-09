using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Rules;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
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
        var ids = await database.DataSources.AsNoTracking()
            .Where(s => s.DefaultRuleSetVersionId == null && (s.OwnerUserId == null || s.OwnerUserId == assignedOwnerId))
            .OrderBy(s => s.Id).Select(s => s.Id).ToListAsync(cancellationToken);
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
                if (source is null || source.DefaultRuleSetVersionId is not null ||
                    (source.OwnerUserId is not null && source.OwnerUserId != assignedOwnerId))
                {
                    continue;
                }

                if (source.DataType != DataSourceDataType.Customer)
                {
                    throw new InvalidOperationException($"Source {id} is not a legacy Customer source.");
                }

                var jobs = await database.ImportJobs.Where(j => j.DataSourceId == id)
                    .OrderBy(j => j.Id).ToListAsync(cancellationToken);
                if (jobs.Any(j => j.RuleDocumentId is not null || j.RuleSetVersionId is not null))
                {
                    throw new InvalidOperationException($"Source {id} already has pinned jobs but no default version.");
                }

                var schemas = jobs.Select(j => (Number: j.SchemaVersion, Json: j.SchemaJson))
                    .Append((Number: source.SchemaVersion, Json: source.SchemaJson))
                    .Select(s => (s.Number, Canonical: RuleDefinitions.CanonicalizeHistorical(JsonSerializer.Deserialize<JsonElement>(s.Json))))
                    .GroupBy(s => s.Number).OrderBy(g => g.Key).ToArray();
                if (schemas.Any(g => g.Select(s => s.Canonical).Distinct(StringComparer.Ordinal).Skip(1).Any()))
                {
                    conflicts.Add(id);
                    logger.LogError("Source {SourceId} has conflicting definitions for schema versions {Versions}",
                        id, string.Join(", ", schemas.Where(g => g.Select(s => s.Canonical).Distinct(StringComparer.Ordinal).Skip(1).Any()).Select(g => g.Key)));
                    continue;
                }

                source.AssignOwner(assignedOwnerId);
                var setName = $"Legacy rules for {source.Name}";
                var set = new RuleSet(assignedOwnerId, setName[..Math.Min(200, setName.Length)],
                    "Migrated current schema and historical job snapshots.", "Customer");
                database.RuleSets.Add(set);
                var versions = new Dictionary<int, RuleSetVersion>();
                var newDocuments = 0;
                foreach (var schema in schemas)
                {
                    var canonical = schema.First().Canonical;
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
                    else if (RuleDefinitions.CanonicalizeHistorical(JsonSerializer.Deserialize<JsonElement>(document.DefinitionJson)) != canonical)
                    {
                        throw new InvalidOperationException("Historical rule document hash collision.");
                    }

                    set.RegisterHistoricalVersion(schema.Key);
                    var version = new RuleSetVersion(set.Id, schema.Key, document.Id,
                        $"Migrated v{schema.Key}", $"Historical schema version {schema.Key}", null);
                    database.RuleSetVersions.Add(version);
                    versions.Add(schema.Key, version);
                }

                source.SelectRules(versions[source.SchemaVersion].Id, source.DataType);
                foreach (var job in jobs)
                {
                    var version = versions[job.SchemaVersion];
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
                    job.MigrateLegacyRules(version.Id, version.RuleDocumentId, fingerprint, set.Name, version.VersionNumber);
                }
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                setsCreated++;
                versionsCreated += versions.Count;
                documentsCreated += newDocuments;
                jobsLinked += jobs.Count;
                logger.LogInformation("Migrated source {SourceId}: {Versions} versions, {Jobs} jobs", id, versions.Count, jobs.Count);
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
            throw new InvalidOperationException($"Backfill skipped conflicting sources: {string.Join(", ", conflicts)}. Resolve them and rerun.");
        }

        return new(setsCreated, versionsCreated, documentsCreated, jobsLinked);
    }
}

public sealed record BackfillResult(int SetsCreated, int VersionsCreated, int DocumentsCreated, int JobsLinked);
