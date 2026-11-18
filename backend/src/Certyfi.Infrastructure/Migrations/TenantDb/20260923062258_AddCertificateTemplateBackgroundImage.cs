using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCertificateTemplateBackgroundImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackgroundImageUrl",
                schema: "unresolved",
                table: "certificate_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateBackgroundImageUrl",
                schema: "unresolved",
                table: "achievements",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackgroundImageUrl",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "TemplateBackgroundImageUrl",
                schema: "unresolved",
                table: "achievements");
        }
    }
}
