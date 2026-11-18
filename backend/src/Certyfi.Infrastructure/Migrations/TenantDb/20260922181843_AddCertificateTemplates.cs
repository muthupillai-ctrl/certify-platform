using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCertificateTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                schema: "unresolved",
                table: "achievements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TemplateKind",
                schema: "unresolved",
                table: "achievements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "certificate_templates",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    FieldSchemaJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certificate_templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_achievements_TemplateId",
                schema: "unresolved",
                table: "achievements",
                column: "TemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_achievements_certificate_templates_TemplateId",
                schema: "unresolved",
                table: "achievements",
                column: "TemplateId",
                principalSchema: "unresolved",
                principalTable: "certificate_templates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_achievements_certificate_templates_TemplateId",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropTable(
                name: "certificate_templates",
                schema: "unresolved");

            migrationBuilder.DropIndex(
                name: "IX_achievements_TemplateId",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                schema: "unresolved",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "TemplateKind",
                schema: "unresolved",
                table: "achievements");
        }
    }
}
