using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCertificateListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_CreatedAt",
                schema: "unresolved",
                table: "credentials",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_ExpiresOn",
                schema: "unresolved",
                table: "credentials",
                column: "ExpiresOn");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_FieldValuesJson_trgm",
                schema: "unresolved",
                table: "credentials",
                column: "FieldValuesJson")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_IssuedAt",
                schema: "unresolved",
                table: "credentials",
                column: "IssuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_RecipientEmail_trgm",
                schema: "unresolved",
                table: "credentials",
                column: "RecipientEmail")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_RecipientName_trgm",
                schema: "unresolved",
                table: "credentials",
                column: "RecipientName")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_credentials_CreatedAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_ExpiresOn",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_FieldValuesJson_trgm",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_IssuedAt",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_RecipientEmail_trgm",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropIndex(
                name: "IX_credentials_RecipientName_trgm",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:public.pg_trgm", ",,");
        }
    }
}
