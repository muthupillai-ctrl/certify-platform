using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddLedgerEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "unresolved",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventDataJson = table.Column<string>(type: "text", nullable: false),
                    PreviousEntryHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EntryHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.SequenceNumber);
                    table.ForeignKey(
                        name: "FK_ledger_entries_credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalSchema: "unresolved",
                        principalTable: "credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ledger_entries_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_ActorUserId",
                schema: "unresolved",
                table: "ledger_entries",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_CredentialId",
                schema: "unresolved",
                table: "ledger_entries",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_EntryHash",
                schema: "unresolved",
                table: "ledger_entries",
                column: "EntryHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_Id",
                schema: "unresolved",
                table: "ledger_entries",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_PreviousEntryHash",
                schema: "unresolved",
                table: "ledger_entries",
                column: "PreviousEntryHash",
                unique: true);

            // Append-only enforcement. Every identifier below is
            // schema-qualified with the literal placeholder "unresolved" —
            // TenantDbContextFactory.EnsureTenantSchemaMigratedAsync does a
            // blind text .Replace("unresolved", schemaName) over the whole
            // generated script, so an unqualified identifier here would
            // resolve against search_path instead (almost certainly the
            // wrong schema — nothing in this codebase ever SETs
            // search_path). Do not use the word "unresolved" anywhere else
            // in this SQL (e.g. an error message) — the replace has no
            // syntax awareness and would corrupt it too.
            //
            // This stops the application, and any non-superuser DB
            // connection, from mutating history — a Postgres superuser could
            // still ALTER TABLE ... DISABLE TRIGGER, tamper, and re-enable
            // it. That's the correct scope: tamper-EVIDENCE, not an
            // unforgeable external root of trust.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION unresolved.ledger_entries_append_only_guard() RETURNS trigger AS $trigger_fn$
BEGIN
    RAISE EXCEPTION 'ledger_entries is append-only: % is not permitted (SequenceNumber=%)',
        TG_OP, COALESCE(OLD.""SequenceNumber"", NULL);
END;
$trigger_fn$ LANGUAGE plpgsql;
");

            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_ledger_entries_append_only ON unresolved.ledger_entries;
CREATE TRIGGER trg_ledger_entries_append_only
    BEFORE UPDATE OR DELETE ON unresolved.ledger_entries
    FOR EACH ROW EXECUTE FUNCTION unresolved.ledger_entries_append_only_guard();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_ledger_entries_append_only ON unresolved.ledger_entries;
DROP FUNCTION IF EXISTS unresolved.ledger_entries_append_only_guard();
");
            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "unresolved");
        }
    }
}
