using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                schema: "platform",
                table: "platform_admins",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByAdminId",
                schema: "platform",
                table: "platform_admins",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                schema: "platform",
                table: "platform_admins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "platform",
                table: "platform_admins",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PasswordChangedAt",
                schema: "platform",
                table: "platform_admins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionVersion",
                schema: "platform",
                table: "platform_admins",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "platform_admin_changes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Field = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OldValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_admin_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_admin_changes_PlatformAdminId_ChangedAt",
                schema: "platform",
                table: "platform_admin_changes",
                columns: new[] { "PlatformAdminId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_admin_changes",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "CreatedByAdminId",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAt",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "SessionVersion",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                schema: "platform",
                table: "platform_admins",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
