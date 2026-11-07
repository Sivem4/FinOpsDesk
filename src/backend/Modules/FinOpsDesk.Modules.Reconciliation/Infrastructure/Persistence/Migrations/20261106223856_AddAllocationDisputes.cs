using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationDisputes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllocationDisputes",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Statement = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorSource = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationDisputes", x => x.Id);
                    table.CheckConstraint("CK_AllocationDisputes_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_AllocationDisputes_ActorSource", "[ActorSource] IN (1, 2)");
                    table.CheckConstraint("CK_AllocationDisputes_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_AllocationDisputes_Statement", "DATALENGTH([Statement]) = DATALENGTH(LTRIM(RTRIM([Statement]))) AND DATALENGTH([Statement]) BETWEEN 2 AND 1000");
                    table.ForeignKey(
                        name: "FK_AllocationDisputes_Allocations_OrganizationId_AllocationId",
                        columns: x => new { x.OrganizationId, x.AllocationId },
                        principalSchema: "reconciliation",
                        principalTable: "Allocations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDisputes_OrganizationId_AllocationId",
                schema: "reconciliation",
                table: "AllocationDisputes",
                columns: new[] { "OrganizationId", "AllocationId" });

            migrationBuilder.CreateIndex(
                name: "UX_AllocationDisputes_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "AllocationDisputes",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);
        }

        /// <summary>
        /// Drops <c>AllocationDisputes</c> and therefore deletes stored allocation disputes.
        /// This is not an automatic procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllocationDisputes",
                schema: "reconciliation");
        }
    }
}
