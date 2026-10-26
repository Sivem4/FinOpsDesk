using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                schema: "reconciliation",
                table: "Allocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Allocations_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "Allocations",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Allocations_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "reconciliation",
                table: "Allocations");
        }
    }
}
