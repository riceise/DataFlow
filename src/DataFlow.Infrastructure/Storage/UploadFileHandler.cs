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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataFlow.Infrastructure.Storage;

public sealed class UploadFileHandler(DataFlowDbContext database, IFileStorage storage, TimeProvider clock,
    ILogger<UploadFileHandler> logger) : ICommandHandler<UploadFileCommand, JobAcceptedDto>
{
    public async Task<JobAcceptedDto> HandleAsync(UploadFileCommand request, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.DataSourceId:N}\n{request.FileSha256}")));
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
            if (!source.AcceptedFormats.HasFlag(request.Format))
            {
                throw new DomainException("file_format_not_allowed", "File format is not allowed.");
            }

            var job = ImportJob.Create(source.Id, request.FileName, key, request.FileSha256, source.SchemaJson, source.SchemaVersion, now);
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
