using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalSecurityEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationalSecurityEvents",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<byte>(type: "tinyint", nullable: false),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetOrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeclaredOperator = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalSecurityEvents", x => x.Id);
                    table.CheckConstraint("CK_OperationalSecurityEvents_DeclaredOperator", "LEN(LTRIM(RTRIM([DeclaredOperator]))) > 0 AND LEN([DeclaredOperator]) <= 128");
                    table.CheckConstraint("CK_OperationalSecurityEvents_Operation", "[Operation] IN (1, 2, 3)");
                    table.CheckConstraint("CK_OperationalSecurityEvents_Outcome", "[Outcome] IN (1, 2)");
                    table.CheckConstraint("CK_OperationalSecurityEvents_Targets", "([Operation] = 1 AND [TargetUserId] IS NOT NULL AND [TargetOrganizationId] IS NULL AND [TargetMembershipId] IS NULL)\r\nOR ([Operation] = 2 AND [TargetUserId] IS NOT NULL AND [TargetOrganizationId] IS NOT NULL AND [TargetMembershipId] IS NOT NULL)\r\nOR ([Operation] = 3 AND [TargetUserId] IS NULL AND [TargetOrganizationId] IS NOT NULL AND [TargetMembershipId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_OperationalSecurityEvents_ApplicationUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalSchema: "access",
                        principalTable: "ApplicationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalSecurityEvents_OrganizationMemberships_TargetMembershipId",
                        column: x => x.TargetMembershipId,
                        principalSchema: "access",
                        principalTable: "OrganizationMemberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationalSecurityEvents_Organizations_TargetOrganizationId",
                        column: x => x.TargetOrganizationId,
                        principalSchema: "access",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalSecurityEvents_TargetMembershipId",
                schema: "access",
                table: "OperationalSecurityEvents",
                column: "TargetMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalSecurityEvents_TargetOrganizationId_OccurredAt",
                schema: "access",
                table: "OperationalSecurityEvents",
                columns: new[] { "TargetOrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalSecurityEvents_TargetUserId_OccurredAt",
                schema: "access",
                table: "OperationalSecurityEvents",
                columns: new[] { "TargetUserId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationalSecurityEvents",
                schema: "access");
        }
    }
}
