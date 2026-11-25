using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Imports;
using DataFlow.Application.Jobs;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataFlow.Infrastructure.Uploads;

public sealed class CommitPreparedUploadHandler(
    DataFlowDbContext database,
    PreparedUploadIntegrityVerifier integrity,
    UploadRuleResolver rules,
    CsvUploadInspector csv,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<ImportProcessingOptions> options,
    IOptions<StorageOptions> storageOptions,
    ILogger<CommitPreparedUploadHandler> logger) : ICommandHandler<CommitPreparedUploadCommand, JobAcceptedDto>
{
    public async Task<JobAcceptedDto> HandleAsync(CommitPreparedUploadCommand command, CancellationToken cancellationToken)
    {
        var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == command.DataSourceId && x.OwnerUserId == currentUser.UserId, cancellationToken)
            ?? throw new DomainException("source_not_found", "Data source was not found.");
        var request = command.Request;
        var lockKey = $"commit-v3|{currentUser.UserId:N}|{source.Id:N}|{command.IdempotencyKey}";
        await using var idempotencyLock = await SessionAdvisoryLock.AcquireAsync(
            database.Database.GetConnectionString()!, lockKey, cancellationToken);
        var existing = await FindRequestAsync(source.Id, command.IdempotencyKey, cancellationToken);
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.UploadToken)));
        var preparedSnapshot = await database.PreparedUploads.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OwnerUserId == currentUser.UserId && x.DataSourceId == source.Id && x.TokenHash == tokenHash,
            cancellationToken);
        if (preparedSnapshot is null)
        {
            throw new DomainException(existing is null ? "invalid_upload_token" : "idempotency_conflict",
                existing is null ? "Prepared upload token is invalid." : "Idempotency key has different content.");
        }

        var manifest = preparedSnapshot.ManifestJson is null ? null : JsonSerializer.Deserialize<UploadManifest>(preparedSnapshot.ManifestJson);
        if (manifest is not null)
        {
            if (existing is not null)
            {
                ValidateReplayShape(request.Parts, manifest);
            }
            else
            {
                ValidateShape(request.Parts, manifest);
            }
        }
        else if (preparedSnapshot.Status is not (PreparedUploadStatus.Expired or PreparedUploadStatus.Aborted))
        {
            throw new DomainException("invalid_manifest", "Prepared upload manifest is unavailable.");
        }

        if (existing is not null)
        {
            return await ReplayAsync(existing, preparedSnapshot, manifest, request.Parts, cancellationToken);
        }

        if (!source.IsActive)
        {
            throw new DomainException("source_inactive", "Data source is inactive.");
        }

        if (preparedSnapshot.Status == PreparedUploadStatus.Consumed)
        {
            throw new DomainException("upload_token_consumed", "Prepared upload token has already been consumed.");
        }

        if (preparedSnapshot.Status is PreparedUploadStatus.Expired or PreparedUploadStatus.Aborted ||
            preparedSnapshot.ExpiresAt <= clock.GetUtcNow())
        {
            throw new DomainException("prepared_upload_expired", "Prepared upload has expired.");
        }

        if (preparedSnapshot.Status != PreparedUploadStatus.Ready || manifest is null || preparedSnapshot.StorageKey is null)
        {
            throw new DomainException("upload_incomplete", "Prepared upload is not ready.");
        }

        if (!source.AcceptedFormats.HasFlag(preparedSnapshot.Format))
        {
            throw new DomainException("file_format_not_allowed", "File format is not allowed.");
        }

        ValidateContainer(manifest, preparedSnapshot.Format);
        var bindings = await ResolveBindingsAsync(manifest, request.Parts, cancellationToken);
        var fingerprint = Fingerprint(preparedSnapshot, bindings);
        await using var objectLock = await StorageObjectLock.AcquireAsync(database.Database.GetConnectionString()!,
            preparedSnapshot.StorageKey, true, cancellationToken);
        await integrity.VerifyAsync(preparedSnapshot, cancellationToken);

        Guid jobId;
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            var prepared = await database.PreparedUploads.FromSqlInterpolated(
                $"SELECT * FROM prepared_uploads WHERE id = {preparedSnapshot.Id} FOR UPDATE").SingleAsync(cancellationToken);
            var databaseNow = await DatabaseUtcNowAsync(cancellationToken);
            existing = await FindRequestAsync(source.Id, command.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await ReplayAsync(existing, preparedSnapshot, manifest, request.Parts, cancellationToken);
            }

            if (prepared.Status == PreparedUploadStatus.Consumed)
            {
                throw new DomainException("upload_token_consumed", "Prepared upload token has already been consumed.");
            }

            if (prepared.Status is PreparedUploadStatus.Expired or PreparedUploadStatus.Aborted ||
                prepared.ExpiresAt <= databaseNow)
            {
                throw new DomainException("prepared_upload_expired", "Prepared upload has expired.");
            }

            if (prepared.Status != PreparedUploadStatus.Ready || prepared.StorageKey != preparedSnapshot.StorageKey)
            {
                throw new DomainException("upload_token_consumed", "Prepared upload token is no longer available.");
            }

            var lockedSource = await database.DataSources.FromSqlInterpolated(
                $"SELECT * FROM data_sources WHERE id = {source.Id} FOR UPDATE").SingleAsync(cancellationToken);
            if (lockedSource.OwnerUserId != currentUser.UserId)
            {
                throw new DomainException("source_not_found", "Data source was not found.");
            }

            if (!lockedSource.IsActive)
            {
                throw new DomainException("source_inactive", "Data source is inactive.");
            }

            if (!lockedSource.AcceptedFormats.HasFlag(prepared.Format))
            {
                throw new DomainException("file_format_not_allowed", "File format is not allowed.");
            }

            foreach (var binding in bindings.Where(x => x.Rule is not null).OrderBy(x => x.Rule!.RuleSet.Id)
                         .ThenBy(x => x.Rule!.Version.Id))
            {
                await rules.ResolveAsync(binding.Rule!.Version.Id, cancellationToken, lockRuleSet: true, useCache: false);
            }

            var now = databaseNow;
            var job = ImportJob.CreatePrepared(source.Id, prepared.OriginalFileName, prepared.StorageKey,
                prepared.FileSha256, lockedSource.SchemaJson, lockedSource.SchemaVersion, now, fingerprint);
            foreach (var binding in bindings)
            {
                var part = binding.ManifestPart;
                var rule = binding.Rule;
                job.AddPreparedPart(part.PartIndex, part.PartType, part.SourceName, part.SheetName, part.SheetIndex,
                    part.SourceLocator, binding.Request.Enabled, binding.Scope, now,
                    rule?.Version.Id, rule?.Document.Id, rule?.Document.ContentHash, rule?.RuleSet.Name,
                    rule?.Version.VersionNumber);
            }

            job.CompletePreparedConfiguration();
            jobId = job.Id;
            var upload = UploadRequest.Create(command.IdempotencyKey, fingerprint, source.Id, prepared.StorageKey,
                prepared.FileSha256, now);
            upload.LinkJob(jobId);
            prepared.Consume(jobId, now);
            database.ImportJobs.Add(job);
            database.UploadRequests.Add(upload);
            database.OutboxMessages.Add(OutboxMessage.Create("import.job.created", JsonSerializer.Serialize(new
            {
                jobId,
                dataSourceId = source.Id,
                schemaVersion = lockedSource.SchemaVersion,
                runVersion = job.RunVersion
            }), now));
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_upload_requests_source_idempotency_key" })
            {
                await transaction.RollbackAsync(cancellationToken);
                database.ChangeTracker.Clear();
                existing = await FindRequestAsync(source.Id, command.IdempotencyKey, cancellationToken);
                if (existing is null)
                {
                    throw;
                }

                return await ReplayAsync(existing, preparedSnapshot, manifest, request.Parts, cancellationToken);
            }
        }

        logger.LogInformation("Prepared upload committed as job {JobId} with {PartCount} parts", jobId, manifest.Parts.Count);
        return new(jobId, ImportJobStatus.Queued.ToString());
    }

    private void ValidateContainer(UploadManifest manifest, AcceptedFileFormat format)
    {
        var settings = options.Value;
        if (manifest.Parts.Count == 0 || manifest.Parts.Count > settings.MaxPartsPerJob)
        {
            throw new ImportFileException("parts_limit_exceeded", "The upload exceeds the part count limit.", 0);
        }

        if (manifest.Container is not { } container)
        {
            throw new DomainException("invalid_manifest", "Container measurements are missing from the manifest.");
        }

        switch (format)
        {
            case AcceptedFileFormat.Csv:
                if (container.ArchiveBytes > storageOptions.Value.MaxFileBytes)
                {
                    throw new ImportFileException("file_too_large", "The CSV file exceeds the current size limit.", 0);
                }

                break;
            case AcceptedFileFormat.Xlsx:
                if (container.ArchiveBytes > Math.Min(storageOptions.Value.MaxFileBytes, settings.MaxFileBytes))
                {
                    throw new ImportFileException("file_too_large", "The XLSX file exceeds the current size limit.", 0);
                }

                if (manifest.Parts.Count > Math.Min(settings.MaxSheetsPerWorkbook, settings.MaxPartsPerJob))
                {
                    throw new ImportFileException("xlsx_sheet_limit_exceeded", "The workbook exceeds the sheet limit.", 0);
                }

                if (container.EntryCount > settings.MaxZipEntries || container.TotalUncompressedBytes > settings.MaxUncompressedBytes)
                {
                    throw new ImportFileException("xlsx_uncompressed_limit_exceeded", "The workbook exceeds its container budget.", 0);
                }

                break;
            case AcceptedFileFormat.Zip:
                if (container.ArchiveBytes > Math.Min(storageOptions.Value.MaxFileBytes, settings.Zip.MaxArchiveBytes))
                {
                    throw new ImportFileException("file_too_large", "The ZIP archive exceeds the current size limit.", 0);
                }

                if (container.EntryCount > settings.Zip.MaxEntries ||
                    manifest.Parts.Count > Math.Min(settings.MaxCsvFilesPerArchive, settings.Zip.MaxCsvFiles))
                {
                    throw new ImportFileException("zip_entry_limit_exceeded", "The ZIP archive exceeds its entry limit.", 0);
                }

                if (container.MaxEntryUncompressedBytes > settings.Zip.MaxEntryBytes ||
                    container.TotalUncompressedBytes > settings.Zip.MaxTotalUncompressedBytes)
                {
                    throw new ImportFileException("zip_uncompressed_limit_exceeded", "The ZIP archive exceeds its uncompressed budget.", 0);
                }

                break;
        }
    }

    private async Task<List<Binding>> ResolveBindingsAsync(UploadManifest manifest,
        IReadOnlyList<CommitPreparedPartRequest> requests, CancellationToken cancellationToken)
    {
        var bindings = new List<Binding>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var part = manifest.Parts[index];
            if (!request.Enabled)
            {
                bindings.Add(new(part, request, null, null));
                continue;
            }

            var rule = await rules.ResolveAsync(request.RuleSetVersionId!.Value, cancellationToken);
            _ = csv.ReparsePreview(part, rule.ParserSchema);
            var scope = DeduplicationScopes.Create(rule.DataType, part.PartIndex, request.DeduplicationGroup);
            bindings.Add(new(part, request, rule, scope));
        }

        return bindings;
    }

    private string Fingerprint(PreparedUpload prepared, IReadOnlyList<Binding> bindings) => UploadFingerprints.Commit(
        currentUser.UserId, prepared.DataSourceId, prepared.Id, prepared.FileSha256, prepared.Format,
        bindings.Select(x => new CommitFingerprintPart(x.Part.PartIndex, x.Request.Enabled,
            x.Rule?.Version.Id, x.Scope)).ToArray());

    private async Task<JobAcceptedDto> ReplayAsync(UploadRequest existing, PreparedUpload prepared,
        UploadManifest? manifest, IReadOnlyList<CommitPreparedPartRequest> requests, CancellationToken cancellationToken)
    {
        if (existing.JobId is null)
        {
            throw new DomainException("upload_incomplete", "Commit has not completed.");
        }

        var job = await database.ImportJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == existing.JobId.Value,
            cancellationToken) ?? throw new DomainException("job_not_found", "Job was not found.");
        if (manifest is not null)
        {
            var scopes = job.Parts.ToDictionary(x => x.PartIndex);
            var fingerprintParts = requests.Select(request =>
            {
                scopes.TryGetValue(request.PartIndex, out var part);
                var scope = request.Enabled
                    ? DeduplicationScopes.Create(part?.DeduplicationScope == "customer:job"
                        ? DataSourceDataType.Customer
                        : DataSourceDataType.Tabular, request.PartIndex, request.DeduplicationGroup)
                    : null;
                return new CommitFingerprintPart(request.PartIndex, request.Enabled,
                    request.Enabled ? request.RuleSetVersionId : null, scope);
            }).ToArray();
            var fingerprint = UploadFingerprints.Commit(currentUser.UserId, prepared.DataSourceId, prepared.Id,
                prepared.FileSha256, prepared.Format, fingerprintParts);
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainException("idempotency_conflict", "Idempotency key has different content.");
            }
        }
        else if (!string.Equals(existing.Fingerprint, string.Empty, StringComparison.Ordinal))
        {
            throw new DomainException("prepared_upload_expired", "Prepared upload has expired.");
        }

        return new(job.Id, job.Status.ToString());
    }

    private static void ValidateReplayShape(IReadOnlyList<CommitPreparedPartRequest> requests, UploadManifest manifest)
    {
        try
        {
            ValidateShape(requests, manifest);
        }
        catch (DomainException)
        {
            throw new DomainException("idempotency_conflict", "Idempotency key has different content.");
        }
    }

    private static void ValidateShape(IReadOnlyList<CommitPreparedPartRequest> requests, UploadManifest manifest)
    {
        if (requests.Count != manifest.Parts.Count || requests.Count == 0)
        {
            throw new DomainException("invalid_manifest", "Commit must contain every server part in its original order.");
        }

        var enabled = 0;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            if (request.PartIndex != manifest.Parts[index].PartIndex || request.PartIndex != index)
            {
                throw new DomainException("invalid_manifest", "Part indexes cannot be duplicated, omitted, unknown or reordered.");
            }

            if (request.Enabled)
            {
                enabled++;
                if (request.RuleSetVersionId is null || request.RuleSetVersionId == Guid.Empty)
                {
                    throw new DomainException("invalid_manifest", "Every enabled part requires a rule version.");
                }
            }
            else if (request.RuleSetVersionId is not null || request.DeduplicationGroup is not null)
            {
                throw new DomainException("invalid_manifest", "Disabled parts cannot have rules or deduplication groups.");
            }
        }

        if (enabled == 0)
        {
            throw new DomainException("no_enabled_parts", "At least one part must be enabled.");
        }
    }

    private async Task<DateTimeOffset> DatabaseUtcNowAsync(CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT clock_timestamp()";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return its current timestamp.")
        };
    }

    private Task<UploadRequest?> FindRequestAsync(Guid sourceId, string key, CancellationToken cancellationToken) =>
        database.UploadRequests.AsNoTracking().SingleOrDefaultAsync(x => x.DataSourceId == sourceId && x.IdempotencyKey == key,
            cancellationToken);

    private sealed record Binding(UploadManifestPart ManifestPart, CommitPreparedPartRequest Request,
        ResolvedUploadRule? Rule, string? Scope)
    {
        public UploadManifestPart Part => ManifestPart;
    }
}
