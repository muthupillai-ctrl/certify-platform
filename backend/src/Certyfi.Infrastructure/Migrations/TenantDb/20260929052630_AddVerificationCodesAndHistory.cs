using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddVerificationCodesAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfflineProof",
                schema: "unresolved",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationCode",
                schema: "unresolved",
                table: "credentials",
                type: "character(12)",
                fixedLength: true,
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            // Existing credentials get their own random code before the
            // unique index is created. The correlated reference to c."Id"
            // makes Postgres generate a fresh code per row; random() is
            // fine for a one-off backfill (new codes come from
            // VerificationCodes.Generate, a CSPRNG). "unresolved" is replaced
            // with each tenant's schema, like the rest of this migration.
            migrationBuilder.Sql(@"
UPDATE ""unresolved"".credentials c
SET ""VerificationCode"" = (
    SELECT string_agg(substr('23456789ABCDEFGHJKMNPQRSTWXYZ', 1 + floor(random() * 29)::int, 1), '')
    FROM generate_series(1, 12)
    WHERE c.""Id"" IS NOT NULL)
WHERE ""VerificationCode"" = '';");

            migrationBuilder.CreateTable(
                name: "verification_events",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    Channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Result = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_VerificationCode",
                schema: "unresolved",
                table: "credentials",
                column: "VerificationCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_events_CredentialId_OccurredAt",
                schema: "unresolved",
                table: "verification_events",
                columns: new[] { "CredentialId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_events",
                schema: "unresolved");

            migrationBuilder.DropIndex(
                name: "IX_credentials_VerificationCode",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "OfflineProof",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "VerificationCode",
                schema: "unresolved",
                table: "credentials");
        }
    }
}
