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
            table.HasCheckConstraint("ck_data_sources_data_type", "data_type = 1");
            table.HasCheckConstraint("ck_data_sources_formats", "accepted_formats BETWEEN 1 AND 3");
        });
        builder.HasKey(source => source.Id);
        builder.Property(source => source.Id).ValueGeneratedNever();
        builder.Property(source => source.Name).HasMaxLength(200).IsRequired();
        builder.Property(source => source.SchemaJson).HasColumnType("jsonb").IsRequired();
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
                "processed_rows = inserted_rows + updated_rows + invalid_rows + duplicate_rows AND " +
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
        builder.HasOne<DataSource>().WithMany().HasForeignKey(job => job.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(job => job.CreatedAt).IsDescending().HasDatabaseName("ix_import_jobs_created_at");
        builder.HasIndex(job => new { job.Status, job.CreatedAt }).IsDescending(false, true)
            .HasDatabaseName("ix_import_jobs_status_created_at");
    }
}

internal sealed class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> builder)
    {
        builder.ToTable("import_rows", table =>
        {
            table.HasCheckConstraint("ck_import_rows_number", "row_number > 0");
            table.HasCheckConstraint("ck_import_rows_status", "status BETWEEN 1 AND 4");
            table.HasCheckConstraint("ck_import_rows_invalid_details",
                "status <> 3 OR (raw_data IS NOT NULL AND errors_json IS NOT NULL AND " +
                "jsonb_typeof(errors_json) = 'array' AND errors_json <> '[]'::jsonb)");
        });
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.RawData).HasColumnType("jsonb");
        builder.Property(row => row.ErrorsJson).HasColumnType("jsonb");
        builder.Property(row => row.NaturalKey).HasMaxLength(256);
        builder.Property(row => row.RowHash).HasMaxLength(64);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(row => row.ImportJobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(row => new { row.ImportJobId, row.RowNumber }).IsUnique()
            .HasDatabaseName("ux_import_rows_job_row_number");
        builder.HasIndex(row => new { row.ImportJobId, row.Status, row.RowNumber })
            .HasDatabaseName("ix_import_rows_job_status_row_number");
        builder.HasIndex(row => new { row.ImportJobId, row.NaturalKey }).HasFilter("natural_key IS NOT NULL")
            .HasDatabaseName("ix_import_rows_job_natural_key");
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
        builder.Property(customer => customer.NaturalKey).HasMaxLength(256).IsRequired();
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

internal sealed class UploadRequestConfiguration : IEntityTypeConfiguration<UploadRequest>
{
    public void Configure(EntityTypeBuilder<UploadRequest> builder)
    {
        builder.ToTable("upload_requests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).ValueGeneratedNever();
        builder.Property(request => request.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(request => request.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(request => request.StorageKey).HasMaxLength(1024).IsRequired();
        builder.Property(request => request.FileSha256).HasMaxLength(64).IsRequired();
        builder.HasIndex(request => request.IdempotencyKey).IsUnique().HasDatabaseName("ux_upload_requests_idempotency_key");
        builder.HasOne<DataSource>().WithMany().HasForeignKey(request => request.DataSourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ImportJob>().WithMany().HasForeignKey(request => request.JobId).OnDelete(DeleteBehavior.Restrict);
    }
}
