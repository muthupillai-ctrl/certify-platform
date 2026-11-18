using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddStudentChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "student_changes",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Field = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_student_changes_students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "unresolved",
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_student_changes_StudentId_ChangedAt",
                schema: "unresolved",
                table: "student_changes",
                columns: new[] { "StudentId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "student_changes",
                schema: "unresolved");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
