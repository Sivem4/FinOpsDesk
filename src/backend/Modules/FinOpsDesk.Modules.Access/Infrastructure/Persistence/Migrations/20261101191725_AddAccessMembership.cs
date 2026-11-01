using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "ApplicationUsers",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentityKey = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationUsers", x => x.Id);
                    table.CheckConstraint("CK_ApplicationUsers_DisplayNameLength", "[DisplayName] IS NULL OR (DATALENGTH([DisplayName]) BETWEEN 2 AND 512 AND DATALENGTH([DisplayName]) % 2 = 0)");
                    table.CheckConstraint("CK_ApplicationUsers_IdentityKeyLength", "DATALENGTH([IdentityKey]) = 32");
                    table.CheckConstraint("CK_ApplicationUsers_IssuerLength", "DATALENGTH([Issuer]) BETWEEN 2 AND 1000 AND DATALENGTH([Issuer]) % 2 = 0");
                    table.CheckConstraint("CK_ApplicationUsers_Status", "[Status] IN (1, 2)");
                    table.CheckConstraint("CK_ApplicationUsers_SubjectLength", "DATALENGTH([Subject]) BETWEEN 2 AND 512 AND DATALENGTH([Subject]) % 2 = 0");
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.CheckConstraint("CK_Organizations_DisplayNameLength", "DATALENGTH([DisplayName]) BETWEEN 2 AND 400 AND DATALENGTH([DisplayName]) % 2 = 0");
                    table.CheckConstraint("CK_Organizations_Status", "[Status] IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "OrganizationMemberships",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMemberships", x => x.Id);
                    table.CheckConstraint("CK_OrganizationMemberships_RevokedAt", "([Status] = 1 AND [RevokedAt] IS NULL) OR ([Status] = 2 AND [RevokedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_OrganizationMemberships_Status", "[Status] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_ApplicationUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "access",
                        principalTable: "ApplicationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "access",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ApplicationUsers_IdentityKey",
                schema: "access",
                table: "ApplicationUsers",
                column: "IdentityKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_OrganizationId",
                schema: "access",
                table: "OrganizationMemberships",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "UX_OrganizationMemberships_UserId_OrganizationId",
                schema: "access",
                table: "OrganizationMemberships",
                columns: new[] { "UserId", "OrganizationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationMemberships",
                schema: "access");

            migrationBuilder.DropTable(
                name: "ApplicationUsers",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "access");
        }
    }
}
