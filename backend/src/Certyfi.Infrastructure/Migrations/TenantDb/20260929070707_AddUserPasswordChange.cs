using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddUserPasswordChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "unresolved",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PasswordChangedAt",
                schema: "unresolved",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "unresolved",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAt",
                schema: "unresolved",
                table: "users");
        }
    }
}
