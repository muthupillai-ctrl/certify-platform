using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddFinalApprovalAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalApprovedAt",
                schema: "unresolved",
                table: "import_batches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FinalApprovedByUserId",
                schema: "unresolved",
                table: "import_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstApprovedAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FirstApprovedByUserId",
                schema: "unresolved",
                table: "credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresFinalApproval",
                schema: "unresolved",
                table: "certificate_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AutoIssueConfirmedByUserId",
                schema: "unresolved",
                table: "api_clients",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "control_requests",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_control_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "institution_audit_log",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_institution_audit_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_control_requests_Kind_TargetId",
                schema: "unresolved",
                table: "control_requests",
                columns: new[] { "Kind", "TargetId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_control_requests_Status_RequestedAt",
                schema: "unresolved",
                table: "control_requests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_institution_audit_log_At",
                schema: "unresolved",
                table: "institution_audit_log",
                column: "At");

            // The audit log is append-only, like the certificate ledger.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION unresolved.institution_audit_log_append_only_guard() RETURNS trigger AS $trigger_fn$
BEGIN
    RAISE EXCEPTION 'institution_audit_log is append-only: % is not permitted', TG_OP;
END;
$trigger_fn$ LANGUAGE plpgsql;
");
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_institution_audit_log_append_only ON unresolved.institution_audit_log;
CREATE TRIGGER trg_institution_audit_log_append_only
    BEFORE UPDATE OR DELETE ON unresolved.institution_audit_log
    FOR EACH ROW EXECUTE FUNCTION unresolved.institution_audit_log_append_only_guard();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_institution_audit_log_append_only ON unresolved.institution_audit_log;
DROP FUNCTION IF EXISTS unresolved.institution_audit_log_append_only_guard();
");
            migrationBuilder.DropTable(
                name: "control_requests",
                schema: "unresolved");

            migrationBuilder.DropTable(
                name: "institution_audit_log",
                schema: "unresolved");

            migrationBuilder.DropColumn(
                name: "FinalApprovedAt",
                schema: "unresolved",
                table: "import_batches");

            migrationBuilder.DropColumn(
                name: "FinalApprovedByUserId",
                schema: "unresolved",
                table: "import_batches");

            migrationBuilder.DropColumn(
                name: "FirstApprovedAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "FirstApprovedByUserId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "RequiresFinalApproval",
                schema: "unresolved",
                table: "certificate_templates");

            migrationBuilder.DropColumn(
                name: "AutoIssueConfirmedByUserId",
                schema: "unresolved",
                table: "api_clients");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
