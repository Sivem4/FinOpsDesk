using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationReversalPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_Action",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_BatchReference",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_IdempotencyOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalIdempotencyKey",
                schema: "reconciliation",
                table: "Allocations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "reconciliation",
                table: "Allocations",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_Action",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "([Action] IN (1, 2))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_ActionOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "(([Action] = 1 AND [IdempotencyOperation] IN (1, 2, 3)) OR ([Action] = 2 AND [IdempotencyOperation] = 4))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_BatchReference",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "(([IdempotencyOperation] = 3 AND [BatchExecutionId] IS NOT NULL) OR ([IdempotencyOperation] IN (1, 2, 4) AND [BatchExecutionId] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_IdempotencyOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "([IdempotencyOperation] IN (1, 2, 3, 4))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_Reason",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "(([Action] = 1 AND [Reason] IS NULL) OR ([Action] = 2 AND [Reason] IS NOT NULL AND DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND 1000))");

            migrationBuilder.CreateIndex(
                name: "UX_Allocations_OrganizationId_ReversalIdempotencyKey",
                schema: "reconciliation",
                table: "Allocations",
                columns: new[] { "OrganizationId", "ReversalIdempotencyKey" },
                unique: true,
                filter: "[ReversalIdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM [reconciliation].[FinancialAuditEntries]
                    WHERE [Action] = 2)
                   OR EXISTS (
                    SELECT 1
                    FROM [reconciliation].[Allocations]
                    WHERE [ReversalIdempotencyKey] IS NOT NULL)
                BEGIN
                    THROW 50002, 'Cannot downgrade AddAllocationReversalPersistence while reversal audit rows or reversal idempotency keys exist.', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_Action",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_ActionOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_BatchReference",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_IdempotencyOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FinancialAuditEntries_Reason",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropIndex(
                name: "UX_Allocations_OrganizationId_ReversalIdempotencyKey",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "reconciliation",
                table: "FinancialAuditEntries");

            migrationBuilder.DropColumn(
                name: "ReversalIdempotencyKey",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_Action",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "([Action] = 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_BatchReference",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "(([IdempotencyOperation] = 3 AND [BatchExecutionId] IS NOT NULL) OR ([IdempotencyOperation] IN (1, 2) AND [BatchExecutionId] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FinancialAuditEntries_IdempotencyOperation",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                sql: "([IdempotencyOperation] IN (1, 2, 3))");
        }
    }
}
