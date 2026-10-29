using DataFlow.Application.Abstractions;
using DataFlow.Application.Imports;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Imports;

public sealed class CsvImportProcessor(DataFlowDbContext database, ImportJobStore store, IImportFileReader storage,
    ICsvRowParser parser, IOptions<ImportProcessingOptions> options, ILogger<CsvImportProcessor> logger)
{
    public async Task ProcessAsync(Guid jobId, int runVersion, Guid messageId, string owner, CancellationToken cancellationToken)
    {
        var snapshot = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, cancellationToken);
        if (snapshot.RunVersion != runVersion || snapshot.IsTerminal()) { return; }
        var lease = await store.AcquireAsync(jobId, runVersion, owner, options.Value.LeaseSeconds, cancellationToken)
            ?? throw new ImportLeaseUnavailableException();
        try
        {
            snapshot = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, cancellationToken);
            if (!string.Equals(Path.GetExtension(snapshot.OriginalFileName), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                throw new ImportFileException("unsupported_file_type", "Only CSV imports are supported.", 0);
            }
            var schema = DataSourceSchema.FromJson(snapshot.SchemaJson);
            // Restore only keys, never raw records or customers, when taking over a committed prefix.
            var keys = await database.ImportRows.AsNoTracking().Where(x => x.ImportJobId == jobId && x.NaturalKey != null)
                .Select(x => x.NaturalKey!).ToListAsync(cancellationToken);
            var processor = new CustomerFileRowProcessor(snapshot.DataSourceId, schema, keys);
            var batch = new List<ImportBatchRow>(options.Value.BatchSize);
            var total = 0;
            await using var stream = await storage.OpenReadAsync(snapshot.StorageKey, cancellationToken);
            await foreach (var row in parser.ParseAsync(stream, schema, cancellationToken))
            {
                if (!row.IsEmpty) { total++; }
                if (row.CheckpointRowNumber <= snapshot.LastCommittedRowNumber) { continue; }
                batch.Add(new(row, processor.Process(row)));
                if (batch.Count < options.Value.BatchSize) { continue; }
                await store.CommitBatchAsync(lease, batch, options.Value.LeaseSeconds, cancellationToken);
                batch.Clear();
            }
            if (batch.Count > 0) { await store.CommitBatchAsync(lease, batch, options.Value.LeaseSeconds, cancellationToken); }
            await store.FinalizeAsync(lease, messageId, total, null, false, cancellationToken);
            logger.LogInformation("Import completed with {TotalRows} data rows", total);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ImportLeaseUnavailableException) { throw; }
        catch (Exception exception)
        {
            var code = exception switch
            {
                ImportFileException file => file.Code,
                DomainException => "invalid_schema_snapshot",
                _ => "import_infrastructure_error"
            };
            var retryable = exception is not (ImportFileException or DomainException);
            await store.FinalizeAsync(lease, messageId, null, code, retryable, cancellationToken);
            logger.LogWarning("Import failed with {ErrorCode}; retryable: {Retryable}", code, retryable);
        }
    }
}
