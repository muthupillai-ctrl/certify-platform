using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCredentialSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Signature",
                schema: "unresolved",
                table: "credentials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatureKeyId",
                schema: "unresolved",
                table: "credentials",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Signature",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "SignatureKeyId",
                schema: "unresolved",
                table: "credentials");
        }
    }
}
