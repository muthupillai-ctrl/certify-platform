using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddPaperCertificateImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<Guid>(
                name: "ImportBatchId",
                schema: "unresolved",
                table: "credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyCertificateNumber",
                schema: "unresolved",
                table: "credentials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyNumberKey",
                schema: "unresolved",
                table: "credentials",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "OriginalIssueDate",
                schema: "unresolved",
                table: "credentials",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_batches",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedRows = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_batches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_batches_certificate_templates_TemplateId",
                        column: x => x.TemplateId,
                        principalSchema: "unresolved",
                        principalTable: "certificate_templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_import_batches_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_import_batches_users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_ImportBatchId",
                schema: "unresolved",
                table: "credentials",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_LegacyNumberKey",
                schema: "unresolved",
                table: "credentials",
                column: "LegacyNumberKey",
                unique: true,
                filter: "\"LegacyNumberKey\" IS NOT NULL AND \"Status\" <> 1");

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_CreatedByUserId",
                schema: "unresolved",
                table: "import_batches",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_DecidedByUserId",
                schema: "unresolved",
                table: "import_batches",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_Status_CreatedAt",
                schema: "unresolved",
                table: "import_batches",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_import_batches_TemplateId",
                schema: "unresolved",
                table: "import_batches",
                column: "TemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_credentials_import_batches_ImportBatchId",
                schema: "unresolved",
                table: "credentials",
                column: "ImportBatchId",
                principalSchema: "unresolved",
                principalTable: "import_batches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_credentials_import_batches_ImportBatchId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropTable(
                name: "import_batches",
                schema: "unresolved");

            migrationBuilder.DropIndex(
                name: "IX_credentials_ImportBatchId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_LegacyNumberKey",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "ImportBatchId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "LegacyCertificateNumber",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "LegacyNumberKey",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "OriginalIssueDate",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
