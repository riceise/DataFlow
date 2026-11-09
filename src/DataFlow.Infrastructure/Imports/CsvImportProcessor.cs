using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Imports;
using DataFlow.Application.Rules;
using DataFlow.Domain.Exceptions;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Imports;

public sealed class CsvImportProcessor(DataFlowDbContext database, ImportJobStore store, IImportFileReader storage,
    ICsvRowParser parser, IOptions<ImportProcessingOptions> options, ILogger<CsvImportProcessor> logger,
    IXlsxRowParser? xlsxParser = null)
{
    public async Task ProcessAsync(Guid jobId, int runVersion, Guid messageId, string owner, CancellationToken cancellationToken, int attempt = 1)
    {
        var snapshot = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, cancellationToken);
        if (snapshot.RunVersion != runVersion || snapshot.IsTerminal()) { return; }
        var lease = await store.AcquireAsync(jobId, runVersion, owner, options.Value.LeaseSeconds, cancellationToken, attempt)
            ?? throw new ImportLeaseUnavailableException();
        var callerToken = cancellationToken;
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        using var heartbeatStop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.DeadlineSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(processing.Token, deadline.Token);
        cancellationToken = linked.Token;
        var heartbeat = new ImportHeartbeat(database.Database.GetConnectionString()!).RunAsync(lease,
            options.Value.LeaseSeconds, options.Value.HeartbeatSeconds, processing, heartbeatStop.Token);
        try
        {
            if (await store.CancellationRequestedAsync(lease, cancellationToken)) { throw new ImportCancellationRequestedException(); }
            snapshot = await database.ImportJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, cancellationToken);
            var extension = Path.GetExtension(snapshot.OriginalFileName);
            var isCsv = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase);
            var isXlsx = string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase);
            if (!isCsv && !isXlsx)
            {
                throw new ImportFileException("unsupported_file_type", "Only CSV and XLSX imports are supported.", 0);
            }
            var schema = DataSourceSchema.FromJson(snapshot.SchemaJson);
            RuleDefinition? definition = null;
            if (snapshot.RuleDocumentId is { } documentId)
            {
                var document = await database.RuleDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId, cancellationToken);
                if (document.EngineVersion == "rules-v1")
                {
                    definition = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(document.DefinitionJson)).Definition;
                    schema = RuleRowProcessor.ParserSchema(definition);
                }
                else if (document.EngineVersion == "legacy-customer-v1")
                {
                    schema = DataSourceSchema.FromJson(document.DefinitionJson);
                }
                else
                {
                    throw new DomainException("unsupported_rule_engine", "Unsupported rule engine.");
                }
            }
            // Restore only keys, never raw records or customers, when taking over a committed prefix.
            var keys = await database.ImportRows.AsNoTracking().Where(x => x.ImportJobId == jobId && x.NaturalKey != null)
                .Select(x => x.NaturalKey!).ToListAsync(cancellationToken);
            var processor = definition is null ? new CustomerFileRowProcessor(snapshot.DataSourceId, schema, keys) : null;
            var ruleProcessor = definition is null ? null : new RuleRowProcessor(snapshot.DataSourceId, definition, keys);
            var batch = new List<ImportBatchRow>(options.Value.BatchSize);
            var total = 0;
            await using var stream = await storage.OpenReadAsync(snapshot.StorageKey, cancellationToken);
            var rows = isCsv
                ? parser.ParseAsync(stream, schema, cancellationToken)
                : (xlsxParser ?? new Parsing.XlsxRowParser(options)).ParseAsync(stream, schema, cancellationToken);
            await foreach (var row in rows)
            {
                if (!row.IsEmpty) { total++; }
                if (row.CheckpointRowNumber <= snapshot.LastCommittedRowNumber) { continue; }
                if (batch.Count == 0 && await store.CancellationRequestedAsync(lease, cancellationToken))
                {
                    throw new ImportCancellationRequestedException();
                }
                batch.Add(new(row, ruleProcessor is null ? processor!.Process(row) : ruleProcessor.Process(row)));
                if (batch.Count < options.Value.BatchSize) { continue; }
                await store.CommitBatchAsync(lease, batch, options.Value.LeaseSeconds, cancellationToken);
                batch.Clear();
                if (await store.CancellationRequestedAsync(lease, cancellationToken)) { throw new ImportCancellationRequestedException(); }
            }
            if (batch.Count > 0) { await store.CommitBatchAsync(lease, batch, options.Value.LeaseSeconds, cancellationToken); }
            await store.FinalizeAsync(lease, messageId, total, null, false, cancellationToken);
            logger.LogInformation("Import completed with {TotalRows} data rows", total);
        }
        catch (ImportCancellationRequestedException)
        {
            await store.FinalizeAsync(lease, messageId, null, null, false, callerToken);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (processing.IsCancellationRequested) { throw new ImportLeaseUnavailableException(); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await store.FinalizeAsync(lease, messageId, null, "import_deadline_exceeded", true, callerToken, attempt);
        }
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
            await store.FinalizeAsync(lease, messageId, null, code, retryable, cancellationToken, attempt);
            logger.LogWarning("Import failed with {ErrorCode}; retryable: {Retryable}", code, retryable);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
    }
}
