using System.Security.Cryptography;
using System.Text;
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

namespace DataFlow.Infrastructure.Uploads;

public sealed record ResolvedUploadRule(RuleSetVersion Version, RuleSet RuleSet, RuleDocument Document,
    DataSourceDataType DataType, DataSourceSchema ParserSchema);

public sealed class UploadRuleResolver(DataFlowDbContext database, ICurrentUser currentUser)
{
    private readonly Dictionary<Guid, ResolvedUploadRule> _cache = [];

    public async Task<DataSourceSchema> PreliminarySchemaAsync(DataSource source, CancellationToken cancellationToken)
    {
        if (source.DefaultRuleSetVersionId is not { } versionId)
        {
            return source.GetParsedSchema();
        }

        return (await ResolveAsync(versionId, cancellationToken, enforceAccess: false)).ParserSchema;
    }

    public async Task<ResolvedUploadRule> ResolveAsync(Guid versionId, CancellationToken cancellationToken,
        bool lockRuleSet = false, bool useCache = true, bool enforceAccess = true)
    {
        if (useCache && !lockRuleSet && enforceAccess && _cache.TryGetValue(versionId, out var cached))
        {
            return cached;
        }

        var version = await database.RuleSetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw new DomainException("rule_version_not_found", "Rule version was not found.");
        var setQuery = database.RuleSets.AsNoTracking().Where(s => s.Id == version.RuleSetId);
        var set = lockRuleSet
            ? await database.RuleSets.FromSqlInterpolated($"SELECT * FROM rule_sets WHERE id = {version.RuleSetId} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : await setQuery.SingleAsync(cancellationToken);
        if (enforceAccess && !await new RuleLibraryService(database, currentUser).AccessibleVersions.AnyAsync(v => v.Id == versionId,
                cancellationToken))
        {
            throw new DomainException("rule_version_not_found", "Rule version was not found.");
        }

        if (enforceAccess && set.IsArchived)
        {
            throw new DomainException("rule_set_archived", "Rule set is archived.");
        }

        var document = await database.RuleDocuments.AsNoTracking().SingleAsync(d => d.Id == version.RuleDocumentId, cancellationToken);
        if (document.DefinitionFormatVersion != 1)
        {
            throw new DomainException("unsupported_rule_definition", "Rule definition format is not supported.");
        }

        ResolvedUploadRule resolved;
        if (document.EngineVersion == "rules-v1")
        {
            RuleDefinition definition;
            string canonical;
            string hash;
            try
            {
                (definition, canonical, hash) = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(document.DefinitionJson));
            }
            catch (Exception exception) when (exception is RuleDefinitionValidationException or JsonException or DomainException)
            {
                throw new DomainException("invalid_rule_document", "Pinned rule document is invalid.");
            }

            // jsonb does not preserve object-key order; validate the reparsed canonical form and hash,
            // not the textual order returned by PostgreSQL.
            _ = canonical;
            if (!string.Equals(hash, document.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(set.DataType, definition.DataType, StringComparison.Ordinal))
            {
                throw new DomainException("invalid_rule_document", "Pinned rule document or its content hash is invalid.");
            }

            var dataType = definition.DataType == "Customer" ? DataSourceDataType.Customer : DataSourceDataType.Tabular;
            resolved = new(version, set, document, dataType, RuleRowProcessor.ParserSchema(definition));
        }
        else if (document.EngineVersion == "legacy-customer-v1")
        {
            DataSourceSchema schema;
            try { schema = DataSourceSchema.FromJson(document.DefinitionJson); }
            catch (DomainException) { throw new DomainException("invalid_rule_document", "Pinned legacy rule document is invalid."); }
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToJson())));
            if (!string.Equals(hash, document.ContentHash, StringComparison.Ordinal) || set.DataType != "Customer")
            {
                throw new DomainException("invalid_rule_document", "Pinned legacy rule document or its content hash is invalid.");
            }

            resolved = new(version, set, document, DataSourceDataType.Customer, schema);
        }
        else
        {
            throw new DomainException("unsupported_rule_engine", "Rule engine is not supported.");
        }

        if (useCache && !lockRuleSet && enforceAccess)
        {
            _cache[versionId] = resolved;
        }

        return resolved;
    }
}
