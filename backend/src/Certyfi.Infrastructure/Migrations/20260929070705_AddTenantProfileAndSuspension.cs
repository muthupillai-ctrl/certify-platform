using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantProfileAndSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "About",
                schema: "platform",
                table: "tenants",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccreditationBody",
                schema: "platform",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                schema: "platform",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                schema: "platform",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "platform",
                table: "tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhone",
                schema: "platform",
                table: "tenants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                schema: "platform",
                table: "tenants",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                schema: "platform",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProfileUpdatedAt",
                schema: "platform",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNumber",
                schema: "platform",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                schema: "platform",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuspensionReason",
                schema: "platform",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteUrl",
                schema: "platform",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "About",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AccreditationBody",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "City",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ContactPhone",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "LegalName",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProfileUpdatedAt",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Region",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RegistrationNumber",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "SuspensionReason",
                schema: "platform",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "WebsiteUrl",
                schema: "platform",
                table: "tenants");
        }
    }
}
