using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PreparedUploads : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "prepared_uploads",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                data_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                format = table.Column<int>(type: "integer", nullable: false),
                content_length = table.Column<long>(type: "bigint", nullable: false),
                storage_checksum_metadata = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                replay_response_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                replay_key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                job_id = table.Column<Guid>(type: "uuid", nullable: true),
                manifest_json = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_prepared_uploads", x => x.id);
                table.CheckConstraint("ck_prepared_uploads_content", "content_length > 0 AND expires_at > created_at");
                table.CheckConstraint("ck_prepared_uploads_lifecycle", "(status = 3 AND consumed_at IS NOT NULL AND job_id IS NOT NULL) OR (status <> 3 AND consumed_at IS NULL AND job_id IS NULL)");
                table.CheckConstraint("ck_prepared_uploads_status", "status BETWEEN 1 AND 5");
                table.CheckConstraint("ck_prepared_uploads_tombstone", "status <> 4 OR (storage_key IS NULL AND manifest_json IS NULL AND replay_response_ciphertext IS NULL)");
                table.ForeignKey(
                    name: "FK_prepared_uploads_AspNetUsers_owner_user_id",
                    column: x => x.owner_user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_prepared_uploads_data_sources_data_source_id",
                    column: x => x.data_source_id,
                    principalTable: "data_sources",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_prepared_uploads_import_jobs_job_id",
                    column: x => x.job_id,
                    principalTable: "import_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_prepared_uploads_data_source_id",
            table: "prepared_uploads",
            column: "data_source_id");

        migrationBuilder.CreateIndex(
            name: "IX_prepared_uploads_job_id",
            table: "prepared_uploads",
            column: "job_id");

        migrationBuilder.CreateIndex(
            name: "ix_prepared_uploads_owner_source_token_hash",
            table: "prepared_uploads",
            columns: new[] { "owner_user_id", "data_source_id", "token_hash" });

        migrationBuilder.CreateIndex(
            name: "ix_prepared_uploads_status_expires_at",
            table: "prepared_uploads",
            columns: new[] { "status", "expires_at" });

        migrationBuilder.CreateIndex(
            name: "ix_prepared_uploads_storage_key",
            table: "prepared_uploads",
            column: "storage_key",
            filter: "storage_key IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_prepared_uploads_owner_source_idempotency_key",
            table: "prepared_uploads",
            columns: new[] { "owner_user_id", "data_source_id", "idempotency_key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "prepared_uploads");
    }
}
