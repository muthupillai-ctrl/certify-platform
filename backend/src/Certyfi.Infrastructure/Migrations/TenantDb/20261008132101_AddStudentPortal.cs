using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddStudentPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                schema: "unresolved",
                table: "credentials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StudentId",
                schema: "unresolved",
                table: "credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresPayment",
                schema: "unresolved",
                table: "api_clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "students",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PasswordSetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignInVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_students", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_StudentId",
                schema: "unresolved",
                table: "credentials",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_students_Email",
                schema: "unresolved",
                table: "students",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_students_FullName_trgm",
                schema: "unresolved",
                table: "students",
                column: "FullName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_students_StudentNumber",
                schema: "unresolved",
                table: "students",
                column: "StudentNumber",
                unique: true,
                filter: "\"StudentNumber\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_credentials_students_StudentId",
                schema: "unresolved",
                table: "credentials",
                column: "StudentId",
                principalSchema: "unresolved",
                principalTable: "students",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Existing certificates: one student per recipient email (named
            // as on their latest certificate), then link each certificate.
            // Certificates without an email, or erased, stay unlinked.
            migrationBuilder.Sql(@"
INSERT INTO ""unresolved"".students (""Id"", ""FullName"", ""Email"", ""Source"", ""CreatedAt"", ""SignInVersion"")
SELECT gen_random_uuid(), x.name, x.email, 0, x.first_at, 0
FROM (
    SELECT DISTINCT ON (lower(c.""RecipientEmail""))
        lower(c.""RecipientEmail"") AS email,
        left(c.""RecipientName"", 200) AS name,
        min(c.""CreatedAt"") OVER (PARTITION BY lower(c.""RecipientEmail"")) AS first_at
    FROM ""unresolved"".credentials c
    WHERE c.""RecipientEmail"" <> '' AND c.""PersonalDataErasedAt"" IS NULL
    ORDER BY lower(c.""RecipientEmail""), c.""CreatedAt"" DESC
) x;

UPDATE ""unresolved"".credentials c
SET ""StudentId"" = s.""Id""
FROM ""unresolved"".students s
WHERE s.""Email"" = lower(c.""RecipientEmail"") AND c.""RecipientEmail"" <> '' AND c.""PersonalDataErasedAt"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_credentials_students_StudentId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropTable(
                name: "students",
                schema: "unresolved");

            migrationBuilder.DropIndex(
                name: "IX_credentials_StudentId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "StudentId",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "RequiresPayment",
                schema: "unresolved",
                table: "api_clients");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
