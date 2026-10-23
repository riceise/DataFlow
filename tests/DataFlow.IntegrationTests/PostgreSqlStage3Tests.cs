using System.Text.Json;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DataFlow.IntegrationTests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public DataFlowDbContext CreateContext() => new(new DbContextOptionsBuilder<DataFlowDbContext>()
        .UseNpgsql(_postgres.GetConnectionString()).Options);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

public sealed class PostgreSqlStage3Tests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_CreatesAllTablesAndHasNoPendingChanges()
    {
        await using var context = fixture.CreateContext();
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            ORDER BY table_name
            """, connection);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "customers", "data_sources", "import_jobs", "import_rows", "outbox_messages", "processed_messages", "upload_requests" }, tables);
    }

    [Fact]
    public async Task Entities_RoundTripJsonUtcSnapshotsAndSystemMessages()
    {
        var localNow = Now.ToOffset(TimeSpan.FromHours(3));
        var source = NewSource(localNow);
        var job = NewJob(source, localNow);
        var originalSchema = job.SchemaJson;
        var customer = NewCustomer(source, "customer-1", localNow);
        var row = ImportRow.Create(job.Id, 2, ImportRowStatus.Invalid, "{\"email\":\"invalid\"}", "bad-key",
            [new ImportRowError("email", "invalid_email", "Invalid email")], localNow, new string('b', 64));
        var outbox = OutboxMessage.Create("ImportJobRequested.v1", JsonSerializer.Serialize(new { job.Id }), localNow);
        var upload = UploadRequest.Create(Guid.NewGuid().ToString(), new string('a', 64), source.Id, "uploads/file", new string('b', 64), localNow);
        upload.LinkJob(job.Id);
        job.AcquireLease("worker", localNow, 60);
        job.RecordBatchCompletion(2, 0, 0, 1, 0, localNow);
        job.SetTotalRows(1, localNow);
        job.Complete(localNow);
        var processed = ProcessedMessage.Create(Guid.NewGuid().ToString(), job.Id, job.RunVersion, localNow);
        source.Update("Updated source", AcceptedFileFormat.All,
            new DataSourceSchema(1, ["externalId"], [new DataSourceFieldDefinition("externalId", "string", true, 100)]), localNow);

        await using (var context = fixture.CreateContext())
        {
            context.AddRange(source, job, customer, row, outbox, upload, processed);
            await context.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var savedSource = await read.DataSources.SingleAsync(entity => entity.Id == source.Id);
        var savedJob = await read.ImportJobs.SingleAsync(entity => entity.Id == job.Id);
        var savedRow = await read.ImportRows.SingleAsync(entity => entity.Id == row.Id);
        var savedCustomer = await read.Customers.SingleAsync(entity => entity.Id == customer.Id);
        Assert.Equal(2, savedSource.SchemaVersion);
        Assert.Equal(1, savedJob.SchemaVersion);
        Assert.Equal(DataSourceSchema.FromJson(originalSchema), DataSourceSchema.FromJson(savedJob.SchemaJson));
        Assert.Equal(ImportJobStatus.CompletedWithErrors, savedJob.Status);
        Assert.Equal(1, savedJob.ProcessedRows);
        Assert.Equal("invalid_email", Assert.Single(savedRow.GetErrors()).Code);
        using var raw = JsonDocument.Parse(savedRow.RawData!);
        Assert.Equal("invalid", raw.RootElement.GetProperty("email").GetString());
        Assert.Equal(new string('b', 64), savedRow.RowHash);
        Assert.Equal(new DateOnly(2026, 1, 2), savedCustomer.RegisteredAt);
        Assert.Equal(customer.NaturalKey, savedCustomer.NaturalKey);
        Assert.Equal(TimeSpan.Zero, savedSource.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, savedJob.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, savedJob.CompletedAt!.Value.Offset);
        Assert.Equal(Now, savedRow.CreatedAt);
        Assert.Equal(job.Id, (await read.UploadRequests.SingleAsync(entity => entity.Id == upload.Id)).JobId);
        Assert.Equal(job.RunVersion, (await read.ProcessedMessages.SingleAsync(entity => entity.MessageId == processed.MessageId)).RunVersion);
        using var payload = JsonDocument.Parse((await read.OutboxMessages.SingleAsync(entity => entity.Id == outbox.Id)).Payload);
        Assert.Equal(job.Id, payload.RootElement.GetProperty("Id").GetGuid());
    }

    [Theory]
    [InlineData("customer", "ux_customers_source_natural_key")]
    [InlineData("row", "ux_import_rows_job_row_number")]
    [InlineData("upload", "ux_upload_requests_source_idempotency_key")]
    [InlineData("processed", "PK_processed_messages")]
    public async Task DuplicateKeys_AreRejectedByPostgreSql(string kind, string constraint)
    {
        var source = NewSource();
        var job = NewJob(source);
        var key = Guid.NewGuid().ToString();
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(source, job, DuplicateEntity(kind, source, job, key));
            await seed.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        context.Add(DuplicateEntity(kind, source, job, key));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
    }

    [Fact]
    public async Task CustomerKeys_AreScopedToSourceAndEmailIsNotUnique()
    {
        var first = NewSource();
        var second = NewSource();
        await using var context = fixture.CreateContext();
        context.AddRange(first, second,
            NewCustomer(first, "same-key"), NewCustomer(second, "same-key"), NewCustomer(first, "other-key"));
        await context.SaveChangesAsync();
        Assert.Equal(3, await context.Customers.CountAsync(customer => customer.DataSourceId == first.Id || customer.DataSourceId == second.Id));
    }

    [Fact]
    public async Task RowNumbers_AreScopedToJobAndResumeKeysMayRepeat()
    {
        var source = NewSource();
        var first = NewJob(source);
        var second = NewJob(source);
        await using var context = fixture.CreateContext();
        context.AddRange(source, first, second,
            ImportRow.Create(first.Id, 2, ImportRowStatus.Inserted, null, "same-key", null, Now),
            ImportRow.Create(first.Id, 3, ImportRowStatus.Duplicate, null, "same-key", null, Now),
            ImportRow.Create(second.Id, 2, ImportRowStatus.Inserted, null, "same-key", null, Now));
        await context.SaveChangesAsync();
        Assert.Equal(2, await context.ImportRows.CountAsync(row => row.ImportJobId == first.Id && row.NaturalKey == "same-key"));
    }

    [Fact]
    public async Task BatchRollback_RevertsCustomersRowsCountersCheckpointAndMessages()
    {
        var source = NewSource();
        var job = NewJob(source);
        job.AcquireLease("worker", Now, 60);
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(source, job);
            await seed.SaveChangesAsync();
        }

        var customer = NewCustomer(source, "rolled-back");
        var row = ImportRow.Create(job.Id, 2, ImportRowStatus.Inserted, null, customer.NaturalKey, null, Now);
        var outbox = OutboxMessage.Create("Completed.v1", "{}", Now);
        var processed = ProcessedMessage.Create(Guid.NewGuid().ToString(), job.Id, 1, Now);
        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var loaded = await context.ImportJobs.SingleAsync(entity => entity.Id == job.Id);
            loaded.RecordBatchCompletion(2, 1, 0, 0, 0, Now);
            loaded.SetTotalRows(1, Now);
            loaded.Complete(Now);
            context.AddRange(customer, row, outbox, processed);
            await context.SaveChangesAsync();
            Assert.Equal(1, await context.ImportRows.CountAsync(entity => entity.ImportJobId == job.Id));
            await transaction.RollbackAsync();
        }

        await using var read = fixture.CreateContext();
        var persisted = await read.ImportJobs.SingleAsync(entity => entity.Id == job.Id);
        Assert.Equal(ImportJobStatus.Processing, persisted.Status);
        Assert.Equal(0, persisted.ProcessedRows);
        Assert.Equal(0, persisted.LastCommittedRowNumber);
        Assert.Null(persisted.TotalRows);
        Assert.False(await read.Customers.AnyAsync(entity => entity.Id == customer.Id));
        Assert.False(await read.ImportRows.AnyAsync(entity => entity.ImportJobId == job.Id));
        Assert.False(await read.OutboxMessages.AnyAsync(entity => entity.Id == outbox.Id));
        Assert.False(await read.ProcessedMessages.AnyAsync(entity => entity.MessageId == processed.MessageId));
    }

    [Fact]
    public async Task FailedSave_DoesNotLeaveJobWithoutOutbox()
    {
        var source = NewSource();
        await using (var seed = fixture.CreateContext())
        {
            seed.Add(source);
            await seed.SaveChangesAsync();
        }

        var job = NewJob(source);
        var outbox = OutboxMessage.Create("ImportJobRequested.v1", "not-json", Now);
        await using (var context = fixture.CreateContext())
        {
            context.AddRange(job, outbox);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await using var read = fixture.CreateContext();
        Assert.False(await read.ImportJobs.AnyAsync(entity => entity.Id == job.Id));
        Assert.False(await read.OutboxMessages.AnyAsync(entity => entity.Id == outbox.Id));
    }

    [Theory]
    [InlineData("UPDATE import_jobs SET processed_rows = 1 WHERE id = @id", "ck_import_jobs_counters")]
    [InlineData("UPDATE import_jobs SET invalid_rows = -1, processed_rows = -1 WHERE id = @id", "ck_import_jobs_counters")]
    [InlineData("UPDATE import_jobs SET total_rows = -1 WHERE id = @id", "ck_import_jobs_counters")]
    [InlineData("UPDATE import_jobs SET status = 99 WHERE id = @id", "ck_import_jobs_status")]
    public async Task Database_RejectsBrokenJobInvariants(string sql, string constraint)
    {
        var source = NewSource();
        var job = NewJob(source);
        await using var context = fixture.CreateContext();
        context.AddRange(source, job);
        await context.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("id", job.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task ForeignKeys_RejectOrphanJobAndDeletionOfUsedSource()
    {
        var source = NewSource();
        await using (var context = fixture.CreateContext())
        {
            context.Add(NewJob(source));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }

        await using var seeded = fixture.CreateContext();
        seeded.AddRange(source, NewJob(source));
        await seeded.SaveChangesAsync();
        var deletion = await Assert.ThrowsAsync<PostgresException>(() =>
            seeded.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM data_sources WHERE id = {source.Id}"));
        // PostgreSQL 18 reports RESTRICT violations as 23001; earlier versions used 23503.
        Assert.Contains(deletion.SqlState, new[] { PostgresErrorCodes.ForeignKeyViolation, "23001" });
    }

    [Fact]
    public async Task LeaseVersion_RejectsStaleConcurrentAcquisition()
    {
        var source = NewSource();
        var job = NewJob(source);
        await using (var seed = fixture.CreateContext())
        {
            seed.AddRange(source, job);
            await seed.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var firstJob = await first.ImportJobs.SingleAsync(entity => entity.Id == job.Id);
        var secondJob = await second.ImportJobs.SingleAsync(entity => entity.Id == job.Id);
        firstJob.AcquireLease("first", Now, 60);
        secondJob.AcquireLease("second", Now, 60);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Schema_UsesJsonbUtcAndRequiredPartialIndexes()
    {
        await using var context = fixture.CreateContext();
        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public'", connection))
        {
            var indexes = new Dictionary<string, string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0), reader.GetString(1));
            }

            Assert.Contains("(created_at DESC)", indexes["ix_import_jobs_created_at"]);
            Assert.Contains("(status, created_at DESC)", indexes["ix_import_jobs_status_created_at"]);
            Assert.Contains("(import_job_id, status, row_number)", indexes["ix_import_rows_job_status_row_number"]);
            Assert.Contains("(import_job_id, natural_key)", indexes["ix_import_rows_job_natural_key"]);
            Assert.Contains("WHERE (natural_key IS NOT NULL)", indexes["ix_import_rows_job_natural_key"]);
            Assert.Contains("WHERE (published_at IS NULL)", indexes["ix_outbox_messages_unpublished"]);
        }

        await using (var command = new NpgsqlCommand("""
            SELECT table_name, column_name, data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            """, connection))
        {
            var columns = new Dictionary<string, string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}", reader.GetString(2));
            }

            Assert.Equal("jsonb", columns["data_sources.schema_json"]);
            Assert.Equal("jsonb", columns["import_jobs.schema_json"]);
            Assert.Equal("jsonb", columns["import_rows.raw_data"]);
            Assert.Equal("jsonb", columns["import_rows.errors_json"]);
            Assert.Equal("jsonb", columns["outbox_messages.payload"]);
            Assert.DoesNotContain("import_rows.normalized_data", columns.Keys);
            foreach (var column in columns.Where(pair => pair.Key.EndsWith("_at", StringComparison.Ordinal) ||
                pair.Key.EndsWith("_until", StringComparison.Ordinal)))
            {
                Assert.Equal(column.Key == "customers.registered_at" ? "date" : "timestamp with time zone", column.Value);
            }
        }
    }

    private static object DuplicateEntity(string kind, DataSource source, ImportJob job, string key) => kind switch
    {
        "customer" => NewCustomer(source, key),
        "row" => ImportRow.Create(job.Id, 2, ImportRowStatus.Inserted, null, key, null, Now),
        "upload" => UploadRequest.Create(key, new string('a', 64), source.Id, "uploads/file", new string('b', 64), Now),
        "processed" => ProcessedMessage.Create(key, job.Id, 1, Now),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static DataSource NewSource(DateTimeOffset? now = null) => DataSource.Create("Customers", DataSourceDataType.Customer,
        AcceptedFileFormat.Csv, new DataSourceSchema(1, ["externalId"], [new DataSourceFieldDefinition("externalId", "string", true, 80)]), now ?? Now);

    private static ImportJob NewJob(DataSource source, DateTimeOffset? now = null) =>
        ImportJob.Create(source.Id, "customers.csv", "uploads/file", new string('a', 64), source.SchemaJson, source.SchemaVersion, now ?? Now);

    private static Customer NewCustomer(DataSource source, string externalId, DateTimeOffset? now = null) =>
        Customer.Create(source.Id, externalId, "Customer", "same@example.test", new DateOnly(2026, 1, 2), true, new string('b', 64), now ?? Now);
}
