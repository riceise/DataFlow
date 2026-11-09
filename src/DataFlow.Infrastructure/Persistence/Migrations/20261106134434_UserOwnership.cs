using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class UserOwnership : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "owner_user_id",
            table: "data_sources",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AspNetRoles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                concurrency_stamp = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoles", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: true),
                security_stamp = table.Column<string>(type: "text", nullable: true),
                concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                phone_number = table.Column<string>(type: "text", nullable: true),
                phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                access_failed_count = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetRoleClaims",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_type = table.Column<string>(type: "text", nullable: true),
                claim_value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoleClaims", x => x.id);
                table.ForeignKey(
                    name: "FK_AspNetRoleClaims_AspNetRoles_role_id",
                    column: x => x.role_id,
                    principalTable: "AspNetRoles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_type = table.Column<string>(type: "text", nullable: true),
                claim_value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                login_provider = table.Column<string>(type: "text", nullable: false),
                provider_key = table.Column<string>(type: "text", nullable: false),
                provider_display_name = table.Column<string>(type: "text", nullable: true),
                user_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.login_provider, x.provider_key });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserRoles", x => new { x.user_id, x.role_id });
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetRoles_role_id",
                    column: x => x.role_id,
                    principalTable: "AspNetRoles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetUsers_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                login_provider = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.user_id, x.login_provider, x.name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_user_id",
                    column: x => x.user_id,
                    principalTable: "AspNetUsers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AspNetRoleClaims_role_id",
            table: "AspNetRoleClaims",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "normalized_name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_user_id",
            table: "AspNetUserClaims",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_user_id",
            table: "AspNetUserLogins",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserRoles_role_id",
            table: "AspNetUserRoles",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "normalized_email");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "normalized_user_name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "owner_user_id",
            table: "data_sources");
    }
}
