using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class FileUploads : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_upload_requests_data_source_id",
            table: "upload_requests");

        migrationBuilder.DropIndex(
            name: "ux_upload_requests_idempotency_key",
            table: "upload_requests");

        migrationBuilder.DropCheckConstraint(
            name: "ck_data_sources_formats",
            table: "data_sources");

        migrationBuilder.AlterColumn<string>(
            name: "idempotency_key",
            table: "upload_requests",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "character varying(128)",
            oldMaxLength: 128);

        migrationBuilder.CreateIndex(
            name: "IX_upload_requests_created_at",
            table: "upload_requests",
            column: "created_at",
            filter: "job_id IS NULL");

        migrationBuilder.CreateIndex(
            name: "ux_upload_requests_source_idempotency_key",
            table: "upload_requests",
            columns: new[] { "data_source_id", "idempotency_key" },
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_data_sources_formats",
            table: "data_sources",
            sql: "accepted_formats BETWEEN 1 AND 15");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_upload_requests_created_at",
            table: "upload_requests");

        migrationBuilder.DropIndex(
            name: "ux_upload_requests_source_idempotency_key",
            table: "upload_requests");

        migrationBuilder.DropCheckConstraint(
            name: "ck_data_sources_formats",
            table: "data_sources");

        migrationBuilder.AlterColumn<string>(
            name: "idempotency_key",
            table: "upload_requests",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(128)",
            oldMaxLength: 128,
            oldCollation: "C");

        migrationBuilder.CreateIndex(
            name: "IX_upload_requests_data_source_id",
            table: "upload_requests",
            column: "data_source_id");

        migrationBuilder.CreateIndex(
            name: "ux_upload_requests_idempotency_key",
            table: "upload_requests",
            column: "idempotency_key",
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_data_sources_formats",
            table: "data_sources",
            sql: "accepted_formats BETWEEN 1 AND 3");
    }
}
