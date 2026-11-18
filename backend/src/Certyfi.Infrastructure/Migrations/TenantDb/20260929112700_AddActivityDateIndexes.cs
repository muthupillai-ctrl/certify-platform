using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddActivityDateIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_verification_events_OccurredAt",
                schema: "unresolved",
                table: "verification_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_OccurredAt",
                schema: "unresolved",
                table: "ledger_entries",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_verification_events_OccurredAt",
                schema: "unresolved",
                table: "verification_events");

            migrationBuilder.DropIndex(
                name: "IX_ledger_entries_OccurredAt",
                schema: "unresolved",
                table: "ledger_entries");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
