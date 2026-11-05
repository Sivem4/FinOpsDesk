using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCaseOpenAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewCaseOpenAttempts",
                schema: "reconciliation",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLabel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCaseOpenAttempts", x => new { x.OrganizationId, x.IdempotencyKey });
                    table.CheckConstraint("CK_ReviewCaseOpenAttempts_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ReviewCaseOpenAttempts_ReasonShape", "([Reason] IN (1, 2)) AND (([Reason] = 1 AND [InvoiceId] IS NOT NULL AND [BankTransactionId] IS NOT NULL) OR ([Reason] = 2 AND (([InvoiceId] IS NOT NULL AND [BankTransactionId] IS NULL) OR ([InvoiceId] IS NULL AND [BankTransactionId] IS NOT NULL))))");
                    table.CheckConstraint("CK_ReviewCaseOpenAttempts_SourceLabel", "([SourceLabel] IS NULL OR [SourceLabel] COLLATE Latin1_General_100_BIN2 IN (N'manual', N'exact-1', N'smart-1'))");
                    table.ForeignKey(
                        name: "FK_ReviewCaseOpenAttempts_ReviewCases_OrganizationId_ReviewCaseId",
                        columns: x => new { x.OrganizationId, x.ReviewCaseId },
                        principalSchema: "reconciliation",
                        principalTable: "ReviewCases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseOpenAttempts_OrganizationId_ReviewCaseId",
                schema: "reconciliation",
                table: "ReviewCaseOpenAttempts",
                columns: new[] { "OrganizationId", "ReviewCaseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewCaseOpenAttempts",
                schema: "reconciliation");
        }
    }
}
