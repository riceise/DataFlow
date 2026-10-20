using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "data_sources",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                data_type = table.Column<int>(type: "integer", nullable: false),
                accepted_formats = table.Column<int>(type: "integer", nullable: false),
                schema_json = table.Column<string>(type: "jsonb", nullable: false),
                schema_version = table.Column<int>(type: "integer", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_data_sources", x => x.id);
                table.CheckConstraint("ck_data_sources_data_type", "data_type = 1");
                table.CheckConstraint("ck_data_sources_formats", "accepted_formats BETWEEN 1 AND 3");
                table.CheckConstraint("ck_data_sources_schema_version", "schema_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false),
                last_error = table.Column<string>(type: "text", nullable: true),
                owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_messages", x => x.id);
                table.CheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
            });

        migrationBuilder.CreateTable(
            name: "customers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                data_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                external_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                registered_at = table.Column<DateOnly>(type: "date", nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                natural_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                row_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_customers", x => x.id);
                table.ForeignKey(
                    name: "FK_customers_data_sources_data_source_id",
                    column: x => x.data_source_id,
                    principalTable: "data_sources",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "import_jobs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                data_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                schema_json = table.Column<string>(type: "jsonb", nullable: false),
                schema_version = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                total_rows = table.Column<int>(type: "integer", nullable: true),
                processed_rows = table.Column<long>(type: "bigint", nullable: false),
                inserted_rows = table.Column<long>(type: "bigint", nullable: false),
                updated_rows = table.Column<long>(type: "bigint", nullable: false),
                invalid_rows = table.Column<long>(type: "bigint", nullable: false),
                duplicate_rows = table.Column<long>(type: "bigint", nullable: false),
                last_committed_row_number = table.Column<long>(type: "bigint", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_error = table.Column<string>(type: "text", nullable: true),
                last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                retryable = table.Column<int>(type: "integer", nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                lease_owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                lease_version = table.Column<int>(type: "integer", nullable: false),
                cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                run_version = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_jobs", x => x.id);
                table.CheckConstraint("ck_import_jobs_counters", "processed_rows = inserted_rows + updated_rows + invalid_rows + duplicate_rows AND inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND (total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_row_number >= 0");
                table.CheckConstraint("ck_import_jobs_retryable", "retryable IN (0, 1)");
                table.CheckConstraint("ck_import_jobs_status", "status BETWEEN 1 AND 7");
                table.CheckConstraint("ck_import_jobs_versions", "schema_version > 0 AND run_version > 0 AND lease_version >= 0 AND attempt_count >= 0");
                table.ForeignKey(
                    name: "FK_import_jobs_data_sources_data_source_id",
                    column: x => x.data_source_id,
                    principalTable: "data_sources",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "import_rows",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_number = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                raw_data = table.Column<string>(type: "jsonb", nullable: true),
                natural_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                errors_json = table.Column<string>(type: "jsonb", nullable: true),
                row_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_rows", x => x.id);
                table.CheckConstraint("ck_import_rows_invalid_details", "status <> 3 OR (raw_data IS NOT NULL AND errors_json IS NOT NULL AND jsonb_typeof(errors_json) = 'array' AND errors_json <> '[]'::jsonb)");
                table.CheckConstraint("ck_import_rows_number", "row_number > 0");
                table.CheckConstraint("ck_import_rows_status", "status BETWEEN 1 AND 4");
                table.ForeignKey(
                    name: "FK_import_rows_import_jobs_import_job_id",
                    column: x => x.import_job_id,
                    principalTable: "import_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "processed_messages",
            columns: table => new
            {
                message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: false),
                run_version = table.Column<int>(type: "integer", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_processed_messages", x => x.message_id);
                table.CheckConstraint("ck_processed_messages_run_version", "run_version > 0");
                table.ForeignKey(
                    name: "FK_processed_messages_import_jobs_job_id",
                    column: x => x.job_id,
                    principalTable: "import_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "upload_requests",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                data_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_upload_requests", x => x.id);
                table.ForeignKey(
                    name: "FK_upload_requests_data_sources_data_source_id",
                    column: x => x.data_source_id,
                    principalTable: "data_sources",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_upload_requests_import_jobs_job_id",
                    column: x => x.job_id,
                    principalTable: "import_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_customers_source_natural_key",
            table: "customers",
            columns: new[] { "data_source_id", "natural_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_created_at",
            table: "import_jobs",
            column: "created_at",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_import_jobs_data_source_id",
            table: "import_jobs",
            column: "data_source_id");

        migrationBuilder.CreateIndex(
            name: "ix_import_jobs_status_created_at",
            table: "import_jobs",
            columns: new[] { "status", "created_at" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "ix_import_rows_job_natural_key",
            table: "import_rows",
            columns: new[] { "import_job_id", "natural_key" },
            filter: "natural_key IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_import_rows_job_status_row_number",
            table: "import_rows",
            columns: new[] { "import_job_id", "status", "row_number" });

        migrationBuilder.CreateIndex(
            name: "ux_import_rows_job_row_number",
            table: "import_rows",
            columns: new[] { "import_job_id", "row_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_outbox_messages_unpublished",
            table: "outbox_messages",
            column: "occurred_at",
            filter: "published_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_processed_messages_job_id",
            table: "processed_messages",
            column: "job_id");

        migrationBuilder.CreateIndex(
            name: "IX_upload_requests_data_source_id",
            table: "upload_requests",
            column: "data_source_id");

        migrationBuilder.CreateIndex(
            name: "IX_upload_requests_job_id",
            table: "upload_requests",
            column: "job_id");

        migrationBuilder.CreateIndex(
            name: "ux_upload_requests_idempotency_key",
            table: "upload_requests",
            column: "idempotency_key",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "customers");

        migrationBuilder.DropTable(
            name: "import_rows");

        migrationBuilder.DropTable(
            name: "outbox_messages");

        migrationBuilder.DropTable(
            name: "processed_messages");

        migrationBuilder.DropTable(
            name: "upload_requests");

        migrationBuilder.DropTable(
            name: "import_jobs");

        migrationBuilder.DropTable(
            name: "data_sources");
    }
}
