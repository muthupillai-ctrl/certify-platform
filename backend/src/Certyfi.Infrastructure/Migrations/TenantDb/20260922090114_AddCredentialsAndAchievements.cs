using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certyfi.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddCredentialsAndAchievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "achievements",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CriteriaNarrative = table.Column<string>(type: "text", nullable: true),
                    ValidityPeriodMonths = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                schema: "unresolved",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AchievementId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuspendedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuspendedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuspensionReason = table.Column<string>(type: "text", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "text", nullable: true),
                    CanonicalHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_credentials_achievements_AchievementId",
                        column: x => x.AchievementId,
                        principalSchema: "unresolved",
                        principalTable: "achievements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credentials_users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credentials_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credentials_users_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credentials_users_SuspendedByUserId",
                        column: x => x.SuspendedByUserId,
                        principalSchema: "unresolved",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credentials_AchievementId",
                schema: "unresolved",
                table: "credentials",
                column: "AchievementId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_DecidedByUserId",
                schema: "unresolved",
                table: "credentials",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_RequestedByUserId",
                schema: "unresolved",
                table: "credentials",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_RevokedByUserId",
                schema: "unresolved",
                table: "credentials",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_Status",
                schema: "unresolved",
                table: "credentials",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_credentials_SuspendedByUserId",
                schema: "unresolved",
                table: "credentials",
                column: "SuspendedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credentials",
                schema: "unresolved");

            migrationBuilder.DropTable(
                name: "achievements",
                schema: "unresolved");
        }
    }
}
