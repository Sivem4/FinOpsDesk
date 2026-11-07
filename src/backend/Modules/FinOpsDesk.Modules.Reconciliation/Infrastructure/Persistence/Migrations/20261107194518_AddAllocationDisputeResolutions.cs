using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationDisputeResolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AllocationDisputes_OrganizationId_Id",
                schema: "reconciliation",
                table: "AllocationDisputes",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "AllocationDisputeResolutions",
                schema: "reconciliation",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationDisputeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorSource = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationDisputeResolutions", x => new { x.OrganizationId, x.AllocationDisputeId });
                    table.CheckConstraint("CK_AllocationDisputeResolutions_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_AllocationDisputeResolutions_ActorSource", "[ActorSource] IN (1, 2)");
                    table.CheckConstraint("CK_AllocationDisputeResolutions_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_AllocationDisputeResolutions_Outcome", "[Outcome] IN (1, 2)");
                    table.CheckConstraint("CK_AllocationDisputeResolutions_Reason", "DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND 1000");
                    table.ForeignKey(
                        name: "FK_AllocationDisputeResolutions_AllocationDisputes_OrganizationId_AllocationDisputeId",
                        columns: x => new { x.OrganizationId, x.AllocationDisputeId },
                        principalSchema: "reconciliation",
                        principalTable: "AllocationDisputes",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AllocationDisputeResolutions_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "AllocationDisputeResolutions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            // CREATE TRIGGER must be the first statement of its batch. EXEC keeps that
            // requirement inside both the ordinary script and the idempotent IF block.
            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER [reconciliation].[TR_AllocationDisputeResolutions_RejectMutation]
                ON [reconciliation].[AllocationDisputeResolutions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50004, ''Allocation dispute resolutions cannot be updated or deleted.'', 1;
                END');
                """);
        }

        /// <summary>
        /// Drops <c>AllocationDisputeResolutions</c> and the alternate key added for its foreign key.
        /// Stored resolutions are deleted. Existing allocation disputes remain.
        /// This is not a procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_AllocationDisputeResolutions_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "AllocationDisputeResolutions",
                schema: "reconciliation");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AllocationDisputes_OrganizationId_Id",
                schema: "reconciliation",
                table: "AllocationDisputes");
        }
    }
}
