using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddFieldSchemaSnapshotToCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FieldSchemaJson",
                schema: "unresolved",
                table: "credentials",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            // Backfill existing credentials — this migration ships in the
            // same deployment as AchievementsController.Update (the first
            // moment an achievement's field schema could ever change), so
            // at this exact instant every existing credential's currently-
            // joined achievement schema IS still the schema that was in
            // effect when it was drafted. Same literal "unresolved" schema
            // qualifier convention as AddLedgerEntries — EnsureTenantSchemaMigratedAsync
            // does a blind text replace of that placeholder.
            migrationBuilder.Sql(@"
UPDATE unresolved.credentials c SET ""FieldSchemaJson"" = a.""FieldSchemaJson""
FROM unresolved.achievements a WHERE a.""Id"" = c.""AchievementId"";
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FieldSchemaJson",
                schema: "unresolved",
                table: "credentials");
        }
    }
}
