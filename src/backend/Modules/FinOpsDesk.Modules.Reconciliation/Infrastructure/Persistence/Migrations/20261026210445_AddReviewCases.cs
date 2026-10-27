using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewCases",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubjectKey = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: false),
                    SourceLabel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCases", x => x.Id);
                    table.CheckConstraint("CK_ReviewCases_ClosedAt", "[ClosedAt] IS NULL OR [ClosedAt] >= [OpenedAt]");
                    table.CheckConstraint("CK_ReviewCases_ReasonShape", "([Reason] IN (1, 2)) AND (([Reason] = 1 AND [InvoiceId] IS NOT NULL AND [BankTransactionId] IS NOT NULL) OR ([Reason] = 2 AND (([InvoiceId] IS NOT NULL AND [BankTransactionId] IS NULL) OR ([InvoiceId] IS NULL AND [BankTransactionId] IS NOT NULL))))");
                    table.CheckConstraint("CK_ReviewCases_SourceLabel", "([SourceLabel] IS NULL OR [SourceLabel] IN (N'manual', N'exact-1', N'smart-1'))");
                    table.CheckConstraint("CK_ReviewCases_StatusShape", "([Status] IN (1, 2, 3)) AND ([Outcome] IS NULL OR [Outcome] IN (1, 2, 3)) AND (([Status] = 1 AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ResolutionNote] IS NULL AND [ClosedAt] IS NULL) OR ([Status] = 2 AND [Outcome] IS NOT NULL AND [ClosedAt] IS NOT NULL AND (([Outcome] = 1 AND [AllocationId] IS NOT NULL) OR ([Outcome] IN (2, 3) AND [AllocationId] IS NULL))) OR ([Status] = 3 AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ClosedAt] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_ReviewCases_BankTransactions_OrganizationId_BankTransactionId",
                        columns: x => new { x.OrganizationId, x.BankTransactionId },
                        principalSchema: "reconciliation",
                        principalTable: "BankTransactions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewCases_Invoices_OrganizationId_InvoiceId",
                        columns: x => new { x.OrganizationId, x.InvoiceId },
                        principalSchema: "reconciliation",
                        principalTable: "Invoices",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCases_OrganizationId_BankTransactionId",
                schema: "reconciliation",
                table: "ReviewCases",
                columns: new[] { "OrganizationId", "BankTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCases_OrganizationId_InvoiceId",
                schema: "reconciliation",
                table: "ReviewCases",
                columns: new[] { "OrganizationId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReviewCases_OrganizationId_SubjectKey_Open",
                schema: "reconciliation",
                table: "ReviewCases",
                columns: new[] { "OrganizationId", "SubjectKey" },
                unique: true,
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewCases",
                schema: "reconciliation");
        }
    }
}
