using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class RuleLibrary : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "rule_documents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                engine_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                definition_format_version = table.Column<int>(type: "integer", nullable: false),
                content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                definition_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rule_documents", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "rule_sets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                data_type = table.Column<string>(type: "text", nullable: false),
                last_version_number = table.Column<int>(type: "integer", nullable: false),
                revision = table.Column<int>(type: "integer", nullable: false),
                is_archived = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rule_sets", x => x.id);
                table.CheckConstraint("ck_rule_sets_type", "data_type IN ('Customer', 'Tabular')");
            });

        migrationBuilder.CreateTable(
            name: "rule_set_versions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                rule_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                version_number = table.Column<int>(type: "integer", nullable: false),
                rule_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                based_on_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                change_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rule_set_versions", x => x.id);
                table.UniqueConstraint("AK_rule_set_versions_id_rule_document_id", x => new { x.id, x.rule_document_id });
                table.CheckConstraint("ck_rule_versions_number", "version_number > 0");
                table.ForeignKey(
                    name: "FK_rule_set_versions_rule_documents_rule_document_id",
                    column: x => x.rule_document_id,
                    principalTable: "rule_documents",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_rule_set_versions_rule_set_versions_based_on_version_id",
                    column: x => x.based_on_version_id,
                    principalTable: "rule_set_versions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_rule_set_versions_rule_sets_rule_set_id",
                    column: x => x.rule_set_id,
                    principalTable: "rule_sets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "rule_version_grants",
            columns: table => new
            {
                rule_set_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rule_version_grants", x => new { x.rule_set_version_id, x.recipient_user_id });
                table.ForeignKey(
                    name: "FK_rule_version_grants_rule_set_versions_rule_set_version_id",
                    column: x => x.rule_set_version_id,
                    principalTable: "rule_set_versions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "rule_version_publications",
            columns: table => new
            {
                rule_set_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rule_version_publications", x => x.rule_set_version_id);
                table.ForeignKey(
                    name: "FK_rule_version_publications_rule_set_versions_rule_set_versio~",
                    column: x => x.rule_set_version_id,
                    principalTable: "rule_set_versions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "user_rule_library_entries",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                rule_set_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_rule_library_entries", x => new { x.user_id, x.rule_set_version_id });
                table.ForeignKey(
                    name: "FK_user_rule_library_entries_rule_set_versions_rule_set_versio~",
                    column: x => x.rule_set_version_id,
                    principalTable: "rule_set_versions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_rule_documents_definition_format_version_engine_version_con~",
            table: "rule_documents",
            columns: new[] { "definition_format_version", "engine_version", "content_hash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_rule_set_versions_based_on_version_id",
            table: "rule_set_versions",
            column: "based_on_version_id");

        migrationBuilder.CreateIndex(
            name: "IX_rule_set_versions_rule_document_id",
            table: "rule_set_versions",
            column: "rule_document_id");

        migrationBuilder.CreateIndex(
            name: "IX_rule_set_versions_rule_set_id_version_number",
            table: "rule_set_versions",
            columns: new[] { "rule_set_id", "version_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_rule_sets_owner_user_id_is_archived_updated_at",
            table: "rule_sets",
            columns: new[] { "owner_user_id", "is_archived", "updated_at" });

        migrationBuilder.CreateIndex(
            name: "IX_rule_version_grants_recipient_user_id_created_at",
            table: "rule_version_grants",
            columns: new[] { "recipient_user_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "IX_rule_version_publications_published_at",
            table: "rule_version_publications",
            column: "published_at");

        migrationBuilder.CreateIndex(
            name: "IX_user_rule_library_entries_rule_set_version_id",
            table: "user_rule_library_entries",
            column: "rule_set_version_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "rule_version_grants");

        migrationBuilder.DropTable(
            name: "rule_version_publications");

        migrationBuilder.DropTable(
            name: "user_rule_library_entries");

        migrationBuilder.DropTable(
            name: "rule_set_versions");

        migrationBuilder.DropTable(
            name: "rule_documents");

        migrationBuilder.DropTable(
            name: "rule_sets");
    }
}
