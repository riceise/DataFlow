using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ImportPartsEnforce : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $enforce$
            BEGIN
                IF EXISTS (
                    SELECT j.id FROM import_jobs j
                    LEFT JOIN import_parts p ON p.import_job_id = j.id
                    GROUP BY j.id HAVING count(p.id) <> 1) THEN
                    RAISE EXCEPTION 'ImportPart enforcement requires exactly one legacy part per job';
                END IF;
                IF EXISTS (SELECT 1 FROM import_rows WHERE import_part_id IS NULL) THEN
                    RAISE EXCEPTION 'ImportPart enforcement requires every import row to be backfilled';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM import_jobs j JOIN import_parts p ON p.import_job_id = j.id
                    WHERE p.part_index <> 0
                       OR p.last_committed_physical_row_number <> j.last_committed_row_number
                       OR p.total_rows IS DISTINCT FROM j.total_rows
                       OR p.processed_rows <> j.processed_rows OR p.valid_rows <> j.valid_rows
                       OR p.inserted_rows <> j.inserted_rows OR p.updated_rows <> j.updated_rows
                       OR p.invalid_rows <> j.invalid_rows OR p.duplicate_rows <> j.duplicate_rows) THEN
                    RAISE EXCEPTION 'ImportPart counters or checkpoint differ from their legacy job';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM import_parts p JOIN import_jobs j ON j.id = p.import_job_id
                    WHERE (j.status IN (1, 2, 3) AND p.status = 5)
                       OR (j.status = 6 AND j.retryable = 0 AND p.status <> 5)
                       OR (j.status = 6 AND j.retryable <> 0 AND p.status <> 2)) THEN
                    RAISE EXCEPTION 'ImportPart status is incompatible with its legacy job';
                END IF;
            END
            $enforce$;
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_import_rows_import_jobs_import_job_id",
            table: "import_rows");

        migrationBuilder.DropIndex(
            name: "ix_import_rows_job_natural_key",
            table: "import_rows");

        migrationBuilder.DropIndex(
            name: "ux_import_rows_job_row_number",
            table: "import_rows");

        migrationBuilder.AlterColumn<Guid>(
            name: "import_part_id",
            table: "import_rows",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "natural_key",
            table: "import_rows",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "natural_key",
            table: "customers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            collation: "C",
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AddForeignKey(
            name: "fk_import_rows_import_part",
            table: "import_rows",
            columns: new[] { "import_part_id", "import_job_id" },
            principalTable: "import_parts",
            principalColumns: new[] { "id", "import_job_id" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_import_rows_import_part",
            table: "import_rows");

        migrationBuilder.AlterColumn<Guid>(
            name: "import_part_id",
            table: "import_rows",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AlterColumn<string>(
            name: "natural_key",
            table: "import_rows",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true,
            oldCollation: "C");

        migrationBuilder.AlterColumn<string>(
            name: "natural_key",
            table: "customers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldCollation: "C");

        migrationBuilder.CreateIndex(
            name: "ix_import_rows_job_natural_key",
            table: "import_rows",
            columns: new[] { "import_job_id", "natural_key" },
            filter: "natural_key IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_import_rows_job_row_number",
            table: "import_rows",
            columns: new[] { "import_job_id", "row_number" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_import_rows_import_jobs_import_job_id",
            table: "import_rows",
            column: "import_job_id",
            principalTable: "import_jobs",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }
}
