using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCheckpointCheckTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckAttempts",
                schema: "unresolved",
                table: "ledger_checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckResult",
                schema: "unresolved",
                table: "ledger_checkpoints",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCheckedAt",
                schema: "unresolved",
                table: "ledger_checkpoints",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckAttempts",
                schema: "unresolved",
                table: "ledger_checkpoints");

            migrationBuilder.DropColumn(
                name: "LastCheckResult",
                schema: "unresolved",
                table: "ledger_checkpoints");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                schema: "unresolved",
                table: "ledger_checkpoints");
        }
    }
}
