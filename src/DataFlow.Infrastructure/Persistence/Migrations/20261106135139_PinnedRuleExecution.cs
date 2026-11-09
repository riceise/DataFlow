using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PinnedRuleExecution : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_import_rows_status",
            table: "import_rows");

        migrationBuilder.DropCheckConstraint(
            name: "ck_import_jobs_counters",
            table: "import_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_data_sources_data_type",
            table: "data_sources");

        migrationBuilder.AddColumn<string>(
            name: "execution_fingerprint",
            table: "import_jobs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "rule_document_id",
            table: "import_jobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "rule_set_name_at_submission",
            table: "import_jobs",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "rule_set_version_id",
            table: "import_jobs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "rule_version_number_at_submission",
            table: "import_jobs",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "valid_rows",
            table: "import_jobs",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "default_rule_set_version_id",
            table: "data_sources",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_import_rows_status",
            table: "import_rows",
            sql: "status BETWEEN 1 AND 5");

        migrationBuilder.CreateIndex(
            name: "IX_import_jobs_execution_fingerprint",
            table: "import_jobs",
            column: "execution_fingerprint");

        migrationBuilder.CreateIndex(
            name: "IX_import_jobs_rule_set_version_id_rule_document_id",
            table: "import_jobs",
            columns: new[] { "rule_set_version_id", "rule_document_id" });

        migrationBuilder.AddCheckConstraint(
            name: "ck_import_jobs_counters",
            table: "import_jobs",
            sql: "processed_rows = valid_rows + inserted_rows + updated_rows + invalid_rows + duplicate_rows AND valid_rows >= 0 AND inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND (total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_row_number >= 0");

        migrationBuilder.CreateIndex(
            name: "IX_data_sources_default_rule_set_version_id",
            table: "data_sources",
            column: "default_rule_set_version_id");

        migrationBuilder.CreateIndex(
            name: "IX_data_sources_owner_user_id_created_at",
            table: "data_sources",
            columns: new[] { "owner_user_id", "created_at" });

        migrationBuilder.AddCheckConstraint(
            name: "ck_data_sources_data_type",
            table: "data_sources",
            sql: "data_type IN (1, 2)");

        migrationBuilder.AddForeignKey(
            name: "FK_data_sources_rule_set_versions_default_rule_set_version_id",
            table: "data_sources",
            column: "default_rule_set_version_id",
            principalTable: "rule_set_versions",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_import_jobs_rule_set_versions_rule_set_version_id_rule_docu~",
            table: "import_jobs",
            columns: new[] { "rule_set_version_id", "rule_document_id" },
            principalTable: "rule_set_versions",
            principalColumns: new[] { "id", "rule_document_id" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_data_sources_rule_set_versions_default_rule_set_version_id",
            table: "data_sources");

        migrationBuilder.DropForeignKey(
            name: "FK_import_jobs_rule_set_versions_rule_set_version_id_rule_docu~",
            table: "import_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_import_rows_status",
            table: "import_rows");

        migrationBuilder.DropIndex(
            name: "IX_import_jobs_execution_fingerprint",
            table: "import_jobs");

        migrationBuilder.DropIndex(
            name: "IX_import_jobs_rule_set_version_id_rule_document_id",
            table: "import_jobs");

        migrationBuilder.DropCheckConstraint(
            name: "ck_import_jobs_counters",
            table: "import_jobs");

        migrationBuilder.DropIndex(
            name: "IX_data_sources_default_rule_set_version_id",
            table: "data_sources");

        migrationBuilder.DropIndex(
            name: "IX_data_sources_owner_user_id_created_at",
            table: "data_sources");

        migrationBuilder.DropCheckConstraint(
            name: "ck_data_sources_data_type",
            table: "data_sources");

        migrationBuilder.DropColumn(
            name: "execution_fingerprint",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "rule_document_id",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "rule_set_name_at_submission",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "rule_set_version_id",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "rule_version_number_at_submission",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "valid_rows",
            table: "import_jobs");

        migrationBuilder.DropColumn(
            name: "default_rule_set_version_id",
            table: "data_sources");

        migrationBuilder.AddCheckConstraint(
            name: "ck_import_rows_status",
            table: "import_rows",
            sql: "status BETWEEN 1 AND 4");

        migrationBuilder.AddCheckConstraint(
            name: "ck_import_jobs_counters",
            table: "import_jobs",
            sql: "processed_rows = inserted_rows + updated_rows + invalid_rows + duplicate_rows AND inserted_rows >= 0 AND updated_rows >= 0 AND invalid_rows >= 0 AND duplicate_rows >= 0 AND (total_rows IS NULL OR total_rows >= processed_rows) AND last_committed_row_number >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_data_sources_data_type",
            table: "data_sources",
            sql: "data_type = 1");
    }
}
