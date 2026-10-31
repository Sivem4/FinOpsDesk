using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Allocations_OrganizationId_ConfirmedAt_Id",
                schema: "reconciliation",
                table: "Allocations",
                columns: new[] { "OrganizationId", "ConfirmedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Allocations_OrganizationId_ConfirmedAt_Id",
                schema: "reconciliation",
                table: "Allocations");
        }
    }
}
