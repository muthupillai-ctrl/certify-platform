using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddPaymentDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<bool>(
                name: "CancelledForNonPayment",
                schema: "unresolved",
                table: "credentials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentDueAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaymentReminderSentAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_credentials_Status_PaymentDueAt",
                schema: "unresolved",
                table: "credentials",
                columns: new[] { "Status", "PaymentDueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_credentials_Status_PaymentDueAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "CancelledForNonPayment",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "PaymentDueAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "PaymentReminderSentAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
