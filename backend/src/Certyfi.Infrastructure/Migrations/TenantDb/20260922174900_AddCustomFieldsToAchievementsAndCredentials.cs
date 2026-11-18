using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCustomFieldsToAchievementsAndCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FieldValuesJson",
                schema: "unresolved",
                table: "credentials",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "FieldSchemaJson",
                schema: "unresolved",
                table: "achievements",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FieldValuesJson",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "FieldSchemaJson",
                schema: "unresolved",
                table: "achievements");
        }
    }
}
