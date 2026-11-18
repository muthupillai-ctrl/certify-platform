using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddLedgerCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_checkpoints",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UpToSequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    CheckpointHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OtsProof = table.Column<byte[]>(type: "bytea", nullable: false),
                    BitcoinBlockHeight = table.Column<int>(type: "integer", nullable: true),
                    MerkleRootVerified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_checkpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_checkpoints_Status",
                schema: "unresolved",
                table: "ledger_checkpoints",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_checkpoints_UpToSequenceNumber",
                schema: "unresolved",
                table: "ledger_checkpoints",
                column: "UpToSequenceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_checkpoints",
                schema: "unresolved");
        }
    }
}
