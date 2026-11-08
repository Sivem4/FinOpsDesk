using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationDisputeCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AllocationDisputes_OrganizationId_Id_AllocationId",
                schema: "reconciliation",
                table: "AllocationDisputes",
                columns: new[] { "OrganizationId", "Id", "AllocationId" });

            migrationBuilder.CreateTable(
                name: "AllocationDisputeCorrections",
                schema: "reconciliation",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationDisputeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorSource = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CorrectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationDisputeCorrections", x => new { x.OrganizationId, x.AllocationDisputeId });
                    table.CheckConstraint("CK_AllocationDisputeCorrections_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_AllocationDisputeCorrections_ActorSource", "[ActorSource] IN (1, 2)");
                    table.CheckConstraint("CK_AllocationDisputeCorrections_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_AllocationDisputeCorrections_Reason", "DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND 1000");
                    table.ForeignKey(
                        name: "FK_AllocationDisputeCorrections_AllocationDisputes_OrganizationId_AllocationDisputeId_AllocationId",
                        columns: x => new { x.OrganizationId, x.AllocationDisputeId, x.AllocationId },
                        principalSchema: "reconciliation",
                        principalTable: "AllocationDisputes",
                        principalColumns: new[] { "OrganizationId", "Id", "AllocationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AllocationDisputeCorrections_Allocations_OrganizationId_AllocationId",
                        columns: x => new { x.OrganizationId, x.AllocationId },
                        principalSchema: "reconciliation",
                        principalTable: "Allocations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDisputeCorrections_OrganizationId_AllocationDisputeId_AllocationId",
                schema: "reconciliation",
                table: "AllocationDisputeCorrections",
                columns: new[] { "OrganizationId", "AllocationDisputeId", "AllocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDisputeCorrections_OrganizationId_AllocationId",
                schema: "reconciliation",
                table: "AllocationDisputeCorrections",
                columns: new[] { "OrganizationId", "AllocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_AllocationDisputeCorrections_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "AllocationDisputeCorrections",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            // CREATE TRIGGER must be the first statement of its batch. EXEC keeps that
            // requirement inside both the ordinary script and the idempotent IF block.
            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER [reconciliation].[TR_AllocationDisputeCorrections_RejectMutation]
                ON [reconciliation].[AllocationDisputeCorrections]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50005, ''Allocation dispute corrections cannot be updated or deleted.'', 1;
                END');
                """);
        }

        /// <summary>
        /// Drops <c>AllocationDisputeCorrections</c>, its trigger, and the alternate key added for its foreign key.
        /// Stored corrections are deleted. Existing disputes, resolutions, and allocations remain.
        /// This is not a procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_AllocationDisputeCorrections_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "AllocationDisputeCorrections",
                schema: "reconciliation");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AllocationDisputes_OrganizationId_Id_AllocationId",
                schema: "reconciliation",
                table: "AllocationDisputes");
        }
    }
}
