using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentSignInIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "student_sign_in_index",
                schema: "platform",
                columns: table => new
                {
                    EmailHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_sign_in_index", x => new { x.EmailHash, x.TenantId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_student_sign_in_index_TenantId",
                schema: "platform",
                table: "student_sign_in_index",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "student_sign_in_index",
                schema: "platform");
        }
    }
}
