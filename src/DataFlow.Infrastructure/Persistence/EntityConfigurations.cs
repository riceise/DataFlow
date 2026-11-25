using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataFlow.Infrastructure.Persistence;

internal sealed class DataSourceConfiguration : IEntityTypeConfiguration<DataSource>
{
    public void Configure(EntityTypeBuilder<DataSource> builder)
    {
        builder.ToTable("data_sources", table =>
        {
            table.HasCheckConstraint("ck_data_sources_schema_version", "schema_version > 0");
            table.HasCheckConstraint("ck_data_sources_data_type", "data_type IN (1, 2)");
            table.HasCheckConstraint("ck_data_sources_formats", "accepted_formats BETWEEN 1 AND 15");
        });
        builder.HasKey(source => source.Id);
        builder.Property(source => source.Id).ValueGeneratedNever();
        builder.Property(source => source.Name).HasMaxLength(200).IsRequired();
        builder.Property(source => source.SchemaJson).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(source => new { source.OwnerUserId, source.CreatedAt });
        builder.HasOne<RuleSetVersion>().WithMany().HasForeignKey(s => s.DefaultRuleSetVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> builder)
    {
        builder.ToTable("import_jobs", table =>
        {
            table.HasCheckConstraint("ck_import_jobs_status", "status BETWEEN 1 AND 7");
            table.HasCheckConstraint("ck_import_jobs_counters",
                "processed_rows = valid_rows + inserted_rows + updated_rows + invalid_rows + duplicate_rows AND valid_rows >= 0 AND " +
                "inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND " +
                "(total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_row_number >= 0");
            table.HasCheckConstraint("ck_import_jobs_versions",
                "schema_version > 0 AND run_version > 0 AND lease_version >= 0 AND attempt_count >= 0");
            table.HasCheckConstraint("ck_import_jobs_retryable", "retryable IN (0, 1)");
        });
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(job => job.StorageKey).HasMaxLength(1024).IsRequired();
        builder.Property(job => job.FileSha256).HasMaxLength(64).IsRequired();
        builder.Property(job => job.SchemaJson).HasColumnType("jsonb").IsRequired();
        builder.Property(job => job.LastErrorCode).HasMaxLength(100);
        builder.Property(job => job.LeaseOwner).HasMaxLength(200);
        builder.Property(job => job.LeaseVersion).IsConcurrencyToken();
        builder.Property(job => job.RunVersion).IsConcurrencyToken();
        builder.Property(job => job.ExecutionFingerprint).HasMaxLength(64);
        builder.HasIndex(job => job.ExecutionFingerprint);
        builder.Property(job => job.RuleSetNameAtSubmission).HasMaxLength(200);
        builder.HasOne<RuleSetVersion>().WithMany().HasForeignKey(j => new { j.RuleSetVersionId, j.RuleDocumentId })
            .HasPrincipalKey(v => new { v.Id, v.RuleDocumentId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DataSource>().WithMany().HasForeignKey(job => job.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(job => job.Parts).WithOne().HasForeignKey(part => part.ImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(job => job.Parts).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasIndex(job => job.CreatedAt).IsDescending().HasDatabaseName("ix_import_jobs_created_at");
        builder.HasIndex(job => new { job.Status, job.CreatedAt }).IsDescending(false, true)
            .HasDatabaseName("ix_import_jobs_status_created_at");
    }
}

internal sealed class ImportPartConfiguration : IEntityTypeConfiguration<ImportPart>
{
    public void Configure(EntityTypeBuilder<ImportPart> builder)
    {
        builder.ToTable("import_parts", table =>
        {
            table.HasCheckConstraint("ck_import_parts_identity", "part_index >= 0 AND part_type BETWEEN 1 AND 4 AND (sheet_index IS NULL OR sheet_index >= 0)");
            table.HasCheckConstraint("ck_import_parts_status", "status BETWEEN 1 AND 7");
            table.HasCheckConstraint("ck_import_parts_counters",
                "processed_rows = valid_rows + inserted_rows + updated_rows + invalid_rows + duplicate_rows AND valid_rows >= 0 AND " +
                "inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND " +
                "(total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_physical_row_number >= 0");
            table.HasCheckConstraint("ck_import_parts_rules",
                "(rule_set_version_id IS NULL AND rule_document_id IS NULL AND rule_snapshot_hash IS NULL) OR " +
                "(rule_set_version_id IS NOT NULL AND rule_document_id IS NOT NULL AND rule_snapshot_hash IS NOT NULL)");
            table.HasCheckConstraint("ck_import_parts_deduplication_scope",
                "(status = 7 AND deduplication_scope IS NULL) OR (status <> 7 AND deduplication_scope IS NOT NULL)");
        });
        builder.HasKey(part => part.Id);
        builder.Property(part => part.Id).ValueGeneratedNever();
        builder.HasAlternateKey(part => new { part.Id, part.ImportJobId }).HasName("ak_import_parts_id_import_job_id");
        builder.HasIndex(part => new { part.ImportJobId, part.PartIndex }).IsUnique()
            .HasDatabaseName("ux_import_parts_job_part_index");
        builder.HasIndex(part => new { part.ImportJobId, part.DeduplicationScope, part.Id })
            .HasDatabaseName("ix_import_parts_job_deduplication_scope");
        builder.Property(part => part.SourceName).HasMaxLength(255).IsRequired();
        builder.Property(part => part.SheetName).HasMaxLength(255);
        builder.Property(part => part.SourceLocator).HasMaxLength(1024).IsRequired();
        builder.Property(part => part.LastErrorCode).HasMaxLength(100);
        builder.Property(part => part.RuleSnapshotHash).HasMaxLength(64);
        builder.Property(part => part.RuleSetNameAtSubmission).HasMaxLength(200);
        builder.Property(part => part.DeduplicationScope).HasMaxLength(100);
        builder.HasOne<RuleSetVersion>().WithMany().HasForeignKey(part => new { part.RuleSetVersionId, part.RuleDocumentId })
            .HasPrincipalKey(version => new { version.Id, version.RuleDocumentId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("import_rows", table =>
        {
            table.HasCheckConstraint("ck_import_rows_number", "row_number > 0");
            table.HasCheckConstraint("ck_import_rows_status", "status BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_import_rows_invalid_details",
                "status <> 3 OR (raw_data IS NOT NULL AND errors_json IS NOT NULL AND " +
                "jsonb_typeof(errors_json) = 'array' AND errors_json <> '[]'::jsonb)");
        });
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.RawData).HasColumnType("jsonb");
        builder.Property(row => row.ErrorsJson).HasColumnType("jsonb");
        builder.Property(row => row.NaturalKey).HasMaxLength(256).UseCollation("C");
        builder.Property(row => row.RowHash).HasMaxLength(64);
        builder.HasOne<ImportPart>().WithMany().HasForeignKey(row => new { row.ImportPartId, row.ImportJobId })
            .HasPrincipalKey(part => new { part.Id, part.ImportJobId }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_import_rows_import_part");
        builder.HasIndex(row => new { row.ImportPartId, row.RowNumber }).IsUnique()
            .HasDatabaseName("ux_import_rows_part_row_number");
        builder.HasIndex(row => new { row.ImportJobId, row.Status, row.RowNumber })
            .HasDatabaseName("ix_import_rows_job_status_row_number");
        builder.HasIndex(row => new { row.ImportPartId, row.NaturalKey }).HasFilter("natural_key IS NOT NULL")
            .HasDatabaseName("ix_import_rows_part_natural_key");
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).ValueGeneratedNever();
        builder.Property(customer => customer.ExternalId).HasMaxLength(80).IsRequired();
        builder.Property(customer => customer.Name).HasMaxLength(200).IsRequired();
        builder.Property(customer => customer.Email).HasMaxLength(254).IsRequired();
        builder.Property(customer => customer.NaturalKey).HasMaxLength(256).UseCollation("C").IsRequired();
        builder.Property(customer => customer.RowHash).HasMaxLength(64).IsRequired();
        builder.Property(customer => customer.RegisteredAt).HasColumnType("date");
        builder.HasOne<DataSource>().WithMany().HasForeignKey(customer => customer.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(customer => new { customer.DataSourceId, customer.NaturalKey }).IsUnique()
            .HasDatabaseName("ux_customers_source_natural_key");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", table =>
            table.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0"));
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).ValueGeneratedNever();
        builder.Property(message => message.Type).HasMaxLength(200).IsRequired();
        builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(message => message.Owner).HasMaxLength(200);
        builder.Property<string>("TraceParent").HasMaxLength(128);
        builder.Property<string>("TraceState").HasMaxLength(512);
        builder.HasIndex(message => message.OccurredAt).HasFilter("published_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_unpublished");
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages", table =>
            table.HasCheckConstraint("ck_processed_messages_run_version", "run_version > 0"));
        builder.HasKey(message => message.MessageId);
        builder.Property(message => message.MessageId).HasMaxLength(200).ValueGeneratedNever();
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(message => message.JobId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PreparedUploadConfiguration : IEntityTypeConfiguration<PreparedUpload>
{
    public void Configure(EntityTypeBuilder<PreparedUpload> builder)
    {
        builder.ToTable("prepared_uploads", table =>
        {
            table.HasCheckConstraint("ck_prepared_uploads_status", "status BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_prepared_uploads_content", "content_length > 0 AND expires_at > created_at");
            table.HasCheckConstraint("ck_prepared_uploads_lifecycle",
                "(status = 3 AND consumed_at IS NOT NULL AND job_id IS NOT NULL) OR (status <> 3 AND consumed_at IS NULL AND job_id IS NULL)");
            table.HasCheckConstraint("ck_prepared_uploads_tombstone",
                "status <> 4 OR (storage_key IS NULL AND manifest_json IS NULL AND replay_response_ciphertext IS NULL)");
        });
        builder.HasKey(upload => upload.Id);
        builder.Property(upload => upload.Id).ValueGeneratedNever();
        builder.Property(upload => upload.IdempotencyKey).HasMaxLength(128).UseCollation("C").IsRequired();
        builder.Property(upload => upload.RequestFingerprint).HasMaxLength(64).UseCollation("C").IsRequired();
        builder.Property(upload => upload.StorageKey).HasMaxLength(1024);
        builder.Property(upload => upload.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(upload => upload.FileSha256).HasMaxLength(64).UseCollation("C").IsRequired();
        builder.Property(upload => upload.StorageChecksumMetadata).HasMaxLength(64).IsRequired();
        builder.Property(upload => upload.TokenHash).HasMaxLength(64).UseCollation("C").IsRequired();
        builder.Property(upload => upload.ReplayKeyId).HasMaxLength(100).IsRequired();
        builder.Property(upload => upload.ManifestJson).HasColumnType("jsonb");
        builder.HasIndex(upload => new { upload.OwnerUserId, upload.DataSourceId, upload.IdempotencyKey }).IsUnique()
            .HasDatabaseName("ux_prepared_uploads_owner_source_idempotency_key");
        builder.HasIndex(upload => upload.StorageKey).HasFilter("storage_key IS NOT NULL")
            .HasDatabaseName("ix_prepared_uploads_storage_key");
        builder.HasIndex(upload => new { upload.Status, upload.ExpiresAt })
            .HasDatabaseName("ix_prepared_uploads_status_expires_at");
        builder.HasIndex(upload => new { upload.OwnerUserId, upload.DataSourceId, upload.TokenHash })
            .HasDatabaseName("ix_prepared_uploads_owner_source_token_hash");
        builder.HasOne<DataSource>().WithMany().HasForeignKey(upload => upload.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(upload => upload.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(upload => upload.JobId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UploadRequestConfiguration : IEntityTypeConfiguration<UploadRequest>
{
    public void Configure(EntityTypeBuilder<UploadRequest> builder)
    {
        builder.ToTable("upload_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.IdempotencyKey).HasMaxLength(128).UseCollation("C").IsRequired();
        builder.Property(request => request.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(request => request.StorageKey).HasMaxLength(1024).IsRequired();
        builder.Property(request => request.FileSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(request => new { request.DataSourceId, request.IdempotencyKey }).IsUnique()
            .HasDatabaseName("ux_upload_requests_source_idempotency_key");
        builder.HasIndex(request => request.CreatedAt).HasFilter("job_id IS NULL");
        builder.HasOne<DataSource>().WithMany().HasForeignKey(request => request.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(request => request.JobId).OnDelete(DeleteBehavior.Restrict);
    }
}
