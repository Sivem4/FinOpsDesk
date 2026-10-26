using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationIdempotencyOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyOperation] = 1
                WHERE [IdempotencyKey] IS NOT NULL
                  AND [IdempotencyOperation] IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations",
                sql: "([IdempotencyKey] IS NULL AND [IdempotencyOperation] IS NULL) OR ([IdempotencyKey] IS NOT NULL AND [IdempotencyOperation] IS NOT NULL AND [IdempotencyOperation] IN (1, 2))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.DropColumn(
                name: "IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations");
        }
    }
}
