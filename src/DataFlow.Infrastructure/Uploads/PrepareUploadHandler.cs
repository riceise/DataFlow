using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Entities;
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

public sealed class PrepareUploadHandler(
    DataFlowDbContext database,
    IPreparedFileStorage storage,
    UploadManifestInspector inspector,
    UploadRuleResolver rules,
    PreparedUploadReplayCrypto crypto,
    ICurrentUser currentUser,
    TimeProvider clock,
    IOptions<ImportProcessingOptions> options,
    ILogger<PrepareUploadHandler> logger) : ICommandHandler<PrepareUploadCommand, PreparedUploadResponse>
{
    public async Task<PreparedUploadResponse> HandleAsync(PrepareUploadCommand request, CancellationToken cancellationToken)
    {
        var source = await database.DataSources.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.DataSourceId && x.OwnerUserId == currentUser.UserId, cancellationToken)
            ?? throw new DomainException("source_not_found", "Data source was not found.");
        if (!source.IsActive)
        {
            throw new DomainException("source_inactive", "Data source is inactive.");
        }

        if (!source.AcceptedFormats.HasFlag(request.Format))
        {
            throw new DomainException("file_format_not_allowed", "File format is not allowed.");
        }

        var fingerprint = UploadFingerprints.Prepare(currentUser.UserId, source.Id, request.FileSha256, request.Format);
        var lockKey = $"prepare-v3|{currentUser.UserId:N}|{source.Id:N}|{request.IdempotencyKey}";
        await using var idempotencyLock = await SessionAdvisoryLock.AcquireAsync(
            database.Database.GetConnectionString()!, lockKey, cancellationToken);
        var existing = await FindAsync(request, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        var now = clock.GetUtcNow();
        var key = $"sources/{source.Id:N}/{now.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture)}/{Guid.NewGuid():N}";
        var persisted = false;
        await using var objectLock = await StorageObjectLock.AcquireAsync(database.Database.GetConnectionString()!, key, true,
            cancellationToken);
        try
        {
            await storage.UploadAsync(key, request.FilePath, request.FileSha256, cancellationToken);
            var preliminarySchema = await rules.PreliminarySchemaAsync(source, cancellationToken);
            var manifest = await inspector.InspectAsync(request.FilePath, request.FileName, request.Format,
                preliminarySchema, cancellationToken);
            var expiresAt = now.AddSeconds(options.Value.PreparedUploadTtlSeconds);
            var token = NewToken();
            var response = new PreparedUploadResponse(token, expiresAt, request.FileName,
                UploadFingerprints.PublicFormat(request.Format), manifest.Parts.Select(part => part.ToDto()).ToArray());
            var manifestJson = JsonSerializer.Serialize(manifest);
            var preparedId = Guid.NewGuid();
            var encrypted = crypto.Encrypt(preparedId, currentUser.UserId, source.Id, request.FileSha256,
                UploadFingerprints.PublicFormat(request.Format), JsonSerializer.SerializeToUtf8Bytes(response));
            var prepared = PreparedUpload.CreateReady(currentUser.UserId, source.Id, request.IdempotencyKey,
                fingerprint, key, request.FileName, request.FileSha256, request.Format, request.ContentLength,
                TokenHash(token), encrypted.Ciphertext, encrypted.KeyId, manifestJson, now, expiresAt, preparedId);
            database.PreparedUploads.Add(prepared);
            await database.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            persisted = true;
            logger.LogInformation("Prepared upload accepted with {PartCount} parts", manifest.Parts.Count);
            return response;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_prepared_uploads_owner_source_idempotency_key" })
        {
            database.ChangeTracker.Clear();
            existing = await FindAsync(request, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return Replay(existing, fingerprint);
        }
        finally
        {
            if (!persisted)
            {
                await CompensateAsync(request, key, fingerprint);
            }
        }
    }

    private PreparedUploadResponse Replay(PreparedUpload existing, string fingerprint)
    {
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new DomainException("idempotency_conflict", "Idempotency key has different content.");
        }

        if (existing.Status == PreparedUploadStatus.Consumed)
        {
            throw new DomainException("upload_token_consumed", "Prepared upload token has already been consumed.");
        }

        if (existing.Status is PreparedUploadStatus.Expired or PreparedUploadStatus.Aborted || existing.ExpiresAt <= clock.GetUtcNow())
        {
            throw new DomainException("prepared_upload_expired", "Prepared upload has expired; use a new idempotency key and upload again.");
        }

        if (existing.Status != PreparedUploadStatus.Ready)
        {
            throw new DomainException("upload_incomplete", "Prepared upload has not completed.");
        }

        return JsonSerializer.Deserialize<PreparedUploadResponse>(crypto.Decrypt(existing))
            ?? throw new InvalidOperationException("Prepared upload replay response is empty.");
    }

    private Task<PreparedUpload?> FindAsync(PrepareUploadCommand request, CancellationToken cancellationToken) =>
        database.PreparedUploads.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerUserId == currentUser.UserId &&
            x.DataSourceId == request.DataSourceId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);

    private async Task CompensateAsync(PrepareUploadCommand request, string key, string fingerprint)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            database.ChangeTracker.Clear();
            var referenced = await database.PreparedUploads.AnyAsync(x => x.StorageKey == key, cleanup.Token) ||
                await database.ImportJobs.AnyAsync(x => x.StorageKey == key, cleanup.Token);
            if (!referenced && !await storage.TryDeleteAsync(key, request.DataSourceId, cleanup.Token))
            {
                database.UploadRequests.Add(UploadRequest.Create($"cleanup-{Guid.NewGuid():N}", fingerprint,
                    request.DataSourceId, key, request.FileSha256, clock.GetUtcNow()));
                await database.SaveChangesAsync(cleanup.Token);
            }
        }
        catch (Exception exception)
        {
            logger.LogError("Prepared upload compensation requires retry for {ObjectKey}, source {DataSourceId}, error type {ErrorType}",
                key, request.DataSourceId, exception.GetType().Name);
        }
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
