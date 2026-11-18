using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddPartnerApiClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsServiceAccount",
                schema: "unresolved",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExternalReference",
                schema: "unresolved",
                table: "credentials",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_clients",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    AutoIssue = table.Column<bool>(type: "boolean", nullable: false),
                    ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_clients_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_clients_users_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_clients_users_ServiceUserId",
                        column: x => x.ServiceUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_RequestedByUserId_ExternalReference",
                schema: "unresolved",
                table: "credentials",
                columns: new[] { "RequestedByUserId", "ExternalReference" },
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_CreatedByUserId",
                schema: "unresolved",
                table: "api_clients",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_KeyPrefix",
                schema: "unresolved",
                table: "api_clients",
                column: "KeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_RevokedByUserId",
                schema: "unresolved",
                table: "api_clients",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_ServiceUserId",
                schema: "unresolved",
                table: "api_clients",
                column: "ServiceUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_clients",
                schema: "unresolved");

            migrationBuilder.DropIndex(
                name: "IX_credentials_RequestedByUserId_ExternalReference",
                schema: "unresolved",
                table: "credentials");

            migrationBuilder.DropColumn(
                name: "IsServiceAccount",
                schema: "unresolved",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ExternalReference",
                schema: "unresolved",
                table: "credentials");
        }
    }
}
