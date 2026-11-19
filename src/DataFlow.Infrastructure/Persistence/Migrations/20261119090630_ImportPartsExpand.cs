using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ImportPartsExpand : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "import_part_id",
            table: "import_rows",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "import_parts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                part_index = table.Column<int>(type: "integer", nullable: false),
                part_type = table.Column<int>(type: "integer", nullable: false),
                source_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                sheet_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                sheet_index = table.Column<int>(type: "integer", nullable: true),
                source_locator = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                last_committed_physical_row_number = table.Column<long>(type: "bigint", nullable: false),
                total_rows = table.Column<int>(type: "integer", nullable: true),
                processed_rows = table.Column<long>(type: "bigint", nullable: false),
                valid_rows = table.Column<long>(type: "bigint", nullable: false),
                inserted_rows = table.Column<long>(type: "bigint", nullable: false),
                updated_rows = table.Column<long>(type: "bigint", nullable: false),
                invalid_rows = table.Column<long>(type: "bigint", nullable: false),
                duplicate_rows = table.Column<long>(type: "bigint", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                last_error_row_number = table.Column<int>(type: "integer", nullable: true),
                rule_set_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                rule_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                rule_snapshot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                rule_set_name_at_submission = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                rule_version_number_at_submission = table.Column<int>(type: "integer", nullable: true),
                deduplication_scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_import_parts", x => x.id);
                table.UniqueConstraint("ak_import_parts_id_import_job_id", x => new { x.id, x.import_job_id });
                table.CheckConstraint("ck_import_parts_counters", "processed_rows = valid_rows + inserted_rows + updated_rows + invalid_rows + duplicate_rows AND valid_rows >= 0 AND inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND (total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_physical_row_number >= 0");
                table.CheckConstraint("ck_import_parts_deduplication_scope", "(status = 7 AND deduplication_scope IS NULL) OR (status <> 7 AND deduplication_scope IS NOT NULL)");
                table.CheckConstraint("ck_import_parts_identity", "part_index >= 0 AND part_type BETWEEN 1 AND 4 AND (sheet_index IS NULL OR sheet_index >= 0)");
                table.CheckConstraint("ck_import_parts_rules", "(rule_set_version_id IS NULL AND rule_document_id IS NULL AND rule_snapshot_hash IS NULL) OR (rule_set_version_id IS NOT NULL AND rule_document_id IS NOT NULL AND rule_snapshot_hash IS NOT NULL)");
                table.CheckConstraint("ck_import_parts_status", "status BETWEEN 1 AND 7");
                table.ForeignKey(
                    name: "FK_import_parts_import_jobs_import_job_id",
                    column: x => x.import_job_id,
                    principalTable: "import_jobs",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_import_parts_rule_set_versions_rule_set_version_id_rule_doc~",
                    columns: x => new { x.rule_set_version_id, x.rule_document_id },
                    principalTable: "rule_set_versions",
                    principalColumns: new[] { "id", "rule_document_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_import_rows_import_part_id_import_job_id",
            table: "import_rows",
            columns: new[] { "import_part_id", "import_job_id" });

        migrationBuilder.CreateIndex(
            name: "ix_import_rows_part_natural_key",
            table: "import_rows",
            columns: new[] { "import_part_id", "natural_key" },
            filter: "natural_key IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_import_rows_part_row_number",
            table: "import_rows",
            columns: new[] { "import_part_id", "row_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_import_parts_job_deduplication_scope",
            table: "import_parts",
            columns: new[] { "import_job_id", "deduplication_scope", "id" });

        migrationBuilder.CreateIndex(
            name: "IX_import_parts_rule_set_version_id_rule_document_id",
            table: "import_parts",
            columns: new[] { "rule_set_version_id", "rule_document_id" });

        migrationBuilder.CreateIndex(
            name: "ux_import_parts_job_part_index",
            table: "import_parts",
            columns: new[] { "import_job_id", "part_index" },
            unique: true);

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "import_parts");

        migrationBuilder.DropIndex(
            name: "IX_import_rows_import_part_id_import_job_id",
            table: "import_rows");

        migrationBuilder.DropIndex(
            name: "ix_import_rows_part_natural_key",
            table: "import_rows");

        migrationBuilder.DropIndex(
            name: "ux_import_rows_part_row_number",
            table: "import_rows");

        migrationBuilder.DropColumn(
            name: "import_part_id",
            table: "import_rows");
    }
}
