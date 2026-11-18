using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiryEventQueuedAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretEncrypted",
                schema: "unresolved",
                table: "api_clients",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WebhookUpdatedAt",
                schema: "unresolved",
                table: "api_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookUrl",
                schema: "unresolved",
                table: "api_clients",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ApiClientId_CreatedAt",
                schema: "unresolved",
                table: "webhook_deliveries",
                columns: new[] { "ApiClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_State_NextAttemptAt",
                schema: "unresolved",
                table: "webhook_deliveries",
                columns: new[] { "State", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "unresolved");

            migrationBuilder.DropColumn(
                name: "ExpiryEventQueuedAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "WebhookSecretEncrypted",
                schema: "unresolved",
                table: "api_clients");

            migrationBuilder.DropColumn(
                name: "WebhookUpdatedAt",
                schema: "unresolved",
                table: "api_clients");

            migrationBuilder.DropColumn(
                name: "WebhookUrl",
                schema: "unresolved",
                table: "api_clients");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
