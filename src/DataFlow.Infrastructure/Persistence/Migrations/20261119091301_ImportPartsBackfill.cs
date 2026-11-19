using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataFlow.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class ImportPartsBackfill : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(ImportPartBackfill.Sql);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data backfill is intentionally not destructive on rollback. ImportPartsExpand.Down
        // removes the new schema after ImportPartsEnforce.Down restores the legacy contracts.
    }
}
