using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataFlow.Infrastructure.Storage;

public sealed class UploadFileHandler(DataFlowDbContext database, IFileStorage storage, TimeProvider clock, ICurrentUser currentUser,
    ILogger<UploadFileHandler> logger) : ICommandHandler<UploadFileCommand, JobAcceptedDto>
{
    public async Task<JobAcceptedDto> HandleAsync(UploadFileCommand request, CancellationToken cancellationToken)
    {
        if (!await database.DataSources.AnyAsync(s => s.Id == request.DataSourceId && s.OwnerUserId == currentUser.UserId, cancellationToken))
        {
            throw new DomainException("source_not_found", "Data source was not found.");
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { version = 2, userId = currentUser.UserId, sourceId = request.DataSourceId, request.FileSha256, request.Format, request.RuleSetVersionId })));
        var existing = await FindAsync(request, cancellationToken);
        if (existing is not null)
        {
            return await ResultAsync(existing, fingerprint, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var key = $"sources/{request.DataSourceId:N}/{now.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture)}/{Guid.NewGuid():N}";
        var committed = false;
        try
        {
            await storage.UploadAsync(key, request.FilePath, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({fingerprint}))", cancellationToken);
            existing = await FindAsync(request, cancellationToken);
            if (existing is not null)
            {
                return await ResultAsync(existing, fingerprint, cancellationToken);
            }

            var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DataSourceId, cancellationToken)
                ?? throw new DomainException("source_not_found", "Data source was not found.");
            if (!source.IsActive)
            {
                throw new DomainException("source_inactive", "Data source is inactive.");
            }

            RuleSetVersion? selectedVersion = null;
            RuleSet? ruleSet = null;
            if (request.RuleSetVersionId is { } versionId)
            {
                selectedVersion = await database.RuleSetVersions.SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
                    ?? throw new DomainException("rule_version_not_found", "Rule version was not found.");
                ruleSet = await database.RuleSets.FromSqlInterpolated($"SELECT * FROM rule_sets WHERE id = {selectedVersion.RuleSetId} FOR UPDATE").SingleAsync(cancellationToken);
                if (!await new RuleLibraryService(database, currentUser).AccessibleVersions.AnyAsync(v => v.Id == versionId, cancellationToken))
                {
                    throw new DomainException("rule_version_not_found", "Rule version was not found.");
                }

                if (ruleSet.IsArchived)
                {
                    throw new DomainException("rule_set_archived", "Rule set is archived.");
                }

                if (ruleSet.DataType != source.DataType.ToString())
                {
                    throw new DomainException("rule_type_mismatch", "Rules and source type differ.");
                }

                var reused = await database.ImportJobs.AsNoTracking().Where(j => j.ExecutionFingerprint == fingerprint &&
                    (j.Status == DataFlow.Domain.Enums.ImportJobStatus.Queued || j.Status == DataFlow.Domain.Enums.ImportJobStatus.Processing ||
                     j.Status == DataFlow.Domain.Enums.ImportJobStatus.RetryScheduled || j.Status == DataFlow.Domain.Enums.ImportJobStatus.Completed ||
                     j.Status == DataFlow.Domain.Enums.ImportJobStatus.CompletedWithErrors)).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                if (reused is not null)
                {
                    var alias = UploadRequest.Create(request.IdempotencyKey, fingerprint, source.Id, reused.StorageKey, request.FileSha256, now);
                    alias.LinkJob(reused.Id); database.UploadRequests.Add(alias);
                    await database.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
                    return new(reused.Id, reused.Status.ToString());
                }
            }
            else if (source.DefaultRuleSetVersionId is not null)
            {
                throw new DomainException("invalid_rule_definition", "An explicit ruleSetVersionId is required.");
            }

            if (!source.AcceptedFormats.HasFlag(request.Format))
            {
                throw new DomainException("file_format_not_allowed", "File format is not allowed.");
            }

            var job = ImportJob.Create(source.Id, request.FileName, key, request.FileSha256, source.SchemaJson, source.SchemaVersion, now);
            if (selectedVersion is not null)
            {
                job.PinRules(selectedVersion.Id, selectedVersion.RuleDocumentId, fingerprint, ruleSet!.Name, selectedVersion.VersionNumber);
            }

            var upload = UploadRequest.Create(request.IdempotencyKey, fingerprint, source.Id, key, request.FileSha256, now);
            upload.LinkJob(job.Id);
            database.UploadRequests.Add(upload);
            database.ImportJobs.Add(job);
            database.OutboxMessages.Add(OutboxMessage.Create("import.job.created", JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                dataSourceId = source.Id,
                schemaVersion = source.SchemaVersion,
                runVersion = job.RunVersion
            }), now));
            await database.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new(job.Id, job.Status.ToString());
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_upload_requests_source_idempotency_key" })
        {
            database.ChangeTracker.Clear();
            existing = await FindAsync(request, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return await ResultAsync(existing, fingerprint, cancellationToken);
        }
        finally
        {
            if (!committed)
            {
                // Independent bounded token: HTTP cancellation must not suppress compensation.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    // Also protects an ambiguous commit result: never delete a referenced object.
                    database.ChangeTracker.Clear();
                    if (!await database.ImportJobs.AnyAsync(x => x.StorageKey == key, cleanup.Token)
                        && !await storage.TryDeleteAsync(key, request.DataSourceId, cleanup.Token))
                    {
                        database.UploadRequests.Add(UploadRequest.Create($"cleanup-{Guid.NewGuid():N}", fingerprint,
                            request.DataSourceId, key, request.FileSha256, clock.GetUtcNow()));
                        await database.SaveChangesAsync(cleanup.Token);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("Compensation requires retry for {ObjectKey}, source {DataSourceId}, error type {ErrorType}",
                        key, request.DataSourceId, ex.GetType().Name);
                }
            }
        }
    }

    private Task<UploadRequest?> FindAsync(UploadFileCommand request, CancellationToken ct) => database.UploadRequests.AsNoTracking()
        .SingleOrDefaultAsync(x => x.DataSourceId == request.DataSourceId && x.IdempotencyKey == request.IdempotencyKey, ct);

    private async Task<JobAcceptedDto> ResultAsync(UploadRequest existing, string fingerprint, CancellationToken ct)
    {
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new DomainException("idempotency_conflict", "Idempotency key has different content.");
        }

        if (existing.JobId is null)
        {
            throw new DomainException("upload_incomplete", "Upload has not completed.");
        }

        var status = await database.ImportJobs.Where(x => x.Id == existing.JobId.Value).Select(x => x.Status).SingleAsync(ct);
        return new(existing.JobId.Value, status.ToString());
    }
}
