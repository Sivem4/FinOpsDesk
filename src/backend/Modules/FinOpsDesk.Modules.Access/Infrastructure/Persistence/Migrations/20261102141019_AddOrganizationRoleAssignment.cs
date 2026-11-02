using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationRoleAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationRoleAssignments",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantActorKind = table.Column<byte>(type: "tinyint", nullable: false),
                    GrantActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GrantOperatorName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokeActorKind = table.Column<byte>(type: "tinyint", nullable: true),
                    RevokeActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokeOperatorName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationRoleAssignments", x => x.Id);
                    table.CheckConstraint("CK_OrganizationRoleAssignments_GrantActor", "([GrantActorKind] = 1 AND [GrantActorUserId] IS NOT NULL AND [GrantOperatorName] IS NULL)\r\nOR ([GrantActorKind] = 2 AND [GrantActorUserId] IS NULL AND [GrantOperatorName] IS NOT NULL AND LEN(LTRIM(RTRIM([GrantOperatorName]))) > 0 AND LEN([GrantOperatorName]) <= 128)");
                    table.CheckConstraint("CK_OrganizationRoleAssignments_RevokeActor", "([RevokedAt] IS NULL AND [RevokeActorKind] IS NULL AND [RevokeActorUserId] IS NULL AND [RevokeOperatorName] IS NULL)\r\nOR ([RevokedAt] IS NOT NULL AND [RevokedAt] > [GrantedAt] AND (([RevokeActorKind] = 1 AND [RevokeActorUserId] IS NOT NULL AND [RevokeOperatorName] IS NULL) OR ([RevokeActorKind] = 2 AND [RevokeActorUserId] IS NULL AND [RevokeOperatorName] IS NOT NULL AND LEN(LTRIM(RTRIM([RevokeOperatorName]))) > 0 AND LEN([RevokeOperatorName]) <= 128)))");
                    table.CheckConstraint("CK_OrganizationRoleAssignments_Role", "[Role] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_OrganizationRoleAssignments_ApplicationUsers_GrantActorUserId",
                        column: x => x.GrantActorUserId,
                        principalSchema: "access",
                        principalTable: "ApplicationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationRoleAssignments_ApplicationUsers_RevokeActorUserId",
                        column: x => x.RevokeActorUserId,
                        principalSchema: "access",
                        principalTable: "ApplicationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationRoleAssignments_OrganizationMemberships_MembershipId",
                        column: x => x.MembershipId,
                        principalSchema: "access",
                        principalTable: "OrganizationMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationRoleAssignments_GrantActorUserId",
                schema: "access",
                table: "OrganizationRoleAssignments",
                column: "GrantActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationRoleAssignments_MembershipId",
                schema: "access",
                table: "OrganizationRoleAssignments",
                column: "MembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationRoleAssignments_RevokeActorUserId",
                schema: "access",
                table: "OrganizationRoleAssignments",
                column: "RevokeActorUserId");

            migrationBuilder.CreateIndex(
                name: "UX_OrganizationRoleAssignments_MembershipId_Role_Active",
                schema: "access",
                table: "OrganizationRoleAssignments",
                columns: new[] { "MembershipId", "Role" },
                unique: true,
                filter: "[RevokedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationRoleAssignments",
                schema: "access");
        }
    }
}
