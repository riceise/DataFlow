using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Infrastructure.Uploads;

public sealed class InspectPreparedPartHandler(DataFlowDbContext database, UploadRuleResolver rules,
    CsvUploadInspector csv, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<InspectPreparedPartCommand, PreparedPartDto>
{
    public async Task<PreparedPartDto> HandleAsync(InspectPreparedPartCommand command, CancellationToken cancellationToken)
    {
        if (!await database.DataSources.AnyAsync(x => x.Id == command.DataSourceId && x.OwnerUserId == currentUser.UserId,
                cancellationToken))
        {
            throw new DomainException("source_not_found", "Data source was not found.");
        }

        var request = command.Request;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.UploadToken)));
        var prepared = await database.PreparedUploads.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OwnerUserId == currentUser.UserId && x.DataSourceId == command.DataSourceId && x.TokenHash == hash,
            cancellationToken) ?? throw new DomainException("invalid_upload_token", "Prepared upload token is invalid.");
        if (prepared.Status == PreparedUploadStatus.Consumed)
        {
            throw new DomainException("upload_token_consumed", "Prepared upload token has already been consumed.");
        }

        if (prepared.Status is PreparedUploadStatus.Expired or PreparedUploadStatus.Aborted || prepared.ExpiresAt <= clock.GetUtcNow())
        {
            throw new DomainException("prepared_upload_expired", "Prepared upload has expired.");
        }

        if (prepared.Status != PreparedUploadStatus.Ready || prepared.ManifestJson is null)
        {
            throw new DomainException("upload_token_consumed", "Prepared upload token is no longer available.");
        }

        var manifest = JsonSerializer.Deserialize<UploadManifest>(prepared.ManifestJson)
            ?? throw new InvalidOperationException("Prepared upload manifest is empty.");
        var part = manifest.Parts.SingleOrDefault(x => x.PartIndex == request.PartIndex)
            ?? throw new DomainException("invalid_manifest", "Part index is not present in the server manifest.");
        var rule = await rules.ResolveAsync(request.RuleSetVersionId, cancellationToken);
        return csv.ReparsePreview(part, rule.ParserSchema).ToDto();
    }
}
