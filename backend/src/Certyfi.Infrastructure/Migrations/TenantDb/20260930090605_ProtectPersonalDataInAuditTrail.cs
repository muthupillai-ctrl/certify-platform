using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class ProtectPersonalDataInAuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonalDataErasedAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalDataSalt",
                schema: "unresolved",
                table: "credentials",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ledger_entry_details",
                schema: "unresolved",
                columns: table => new
                {
                    LedgerEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entry_details", x => x.LedgerEntryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entry_details_CredentialId",
                schema: "unresolved",
                table: "ledger_entry_details",
                column: "CredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entry_details",
                schema: "unresolved");

            migrationBuilder.DropColumn(
                name: "PersonalDataErasedAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "PersonalDataSalt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
