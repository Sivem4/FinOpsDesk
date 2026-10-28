using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_BatchExecutions_OrganizationId_Id",
                schema: "reconciliation",
                table: "BatchExecutions",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Allocations_OrganizationId_Id",
                schema: "reconciliation",
                table: "Allocations",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "FinancialAuditEntries",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyOperation = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAuditEntries", x => x.Id);
                    table.CheckConstraint("CK_FinancialAuditEntries_Action", "([Action] = 1)");
                    table.CheckConstraint("CK_FinancialAuditEntries_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_FinancialAuditEntries_Amount", "([Amount] > 0)");
                    table.CheckConstraint("CK_FinancialAuditEntries_BatchReference", "(([IdempotencyOperation] = 3 AND [BatchExecutionId] IS NOT NULL) OR ([IdempotencyOperation] IN (1, 2) AND [BatchExecutionId] IS NULL))");
                    table.CheckConstraint("CK_FinancialAuditEntries_IdempotencyOperation", "([IdempotencyOperation] IN (1, 2, 3))");
                    table.ForeignKey(
                        name: "FK_FinancialAuditEntries_Allocations_OrganizationId_AllocationId",
                        columns: x => new { x.OrganizationId, x.AllocationId },
                        principalSchema: "reconciliation",
                        principalTable: "Allocations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialAuditEntries_BankTransactions_OrganizationId_BankTransactionId",
                        columns: x => new { x.OrganizationId, x.BankTransactionId },
                        principalSchema: "reconciliation",
                        principalTable: "BankTransactions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialAuditEntries_BatchExecutions_OrganizationId_BatchExecutionId",
                        columns: x => new { x.OrganizationId, x.BatchExecutionId },
                        principalSchema: "reconciliation",
                        principalTable: "BatchExecutions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FinancialAuditEntries_Invoices_OrganizationId_InvoiceId",
                        columns: x => new { x.OrganizationId, x.InvoiceId },
                        principalSchema: "reconciliation",
                        principalTable: "Invoices",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEntries_OrganizationId_BankTransactionId",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                columns: new[] { "OrganizationId", "BankTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEntries_OrganizationId_BatchExecutionId",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                columns: new[] { "OrganizationId", "BatchExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAuditEntries_OrganizationId_InvoiceId",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                columns: new[] { "OrganizationId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "UX_FinancialAuditEntries_OrganizationId_AllocationId_Action",
                schema: "reconciliation",
                table: "FinancialAuditEntries",
                columns: new[] { "OrganizationId", "AllocationId", "Action" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER [reconciliation].[TR_FinancialAuditEntries_RejectMutation]
                ON [reconciliation].[FinancialAuditEntries]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50001, 'Financial audit entries cannot be updated or deleted.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_FinancialAuditEntries_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "FinancialAuditEntries",
                schema: "reconciliation");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_BatchExecutions_OrganizationId_Id",
                schema: "reconciliation",
                table: "BatchExecutions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Allocations_OrganizationId_Id",
                schema: "reconciliation",
                table: "Allocations");
        }
    }
}
