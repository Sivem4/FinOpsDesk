using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCaseAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReviewCases_OrganizationId_Id",
                schema: "reconciliation",
                table: "ReviewCases",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "ReviewCaseAuditEntries",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BankTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLabel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCaseAuditEntries", x => x.Id);
                    table.CheckConstraint("CK_ReviewCaseAuditEntries_Action", "([Action] IN (1, 2, 3))");
                    table.CheckConstraint("CK_ReviewCaseAuditEntries_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_ReviewCaseAuditEntries_DismissedShape", "([Action] <> 3 OR ([Reason] IS NULL AND [InvoiceId] IS NULL AND [BankTransactionId] IS NULL AND [SourceLabel] IS NULL AND [Note] IS NULL AND [Outcome] IS NULL AND [AllocationId] IS NULL AND ([ResolutionNote] IS NULL OR (DATALENGTH([ResolutionNote]) = DATALENGTH(LTRIM(RTRIM([ResolutionNote]))) AND DATALENGTH([ResolutionNote]) BETWEEN 2 AND 1000))))");
                    table.CheckConstraint("CK_ReviewCaseAuditEntries_OpenedShape", "([Action] <> 1 OR ([Reason] IS NOT NULL AND ([Reason] IN (1, 2)) AND (([Reason] = 1 AND [InvoiceId] IS NOT NULL AND [BankTransactionId] IS NOT NULL) OR ([Reason] = 2 AND (([InvoiceId] IS NOT NULL AND [BankTransactionId] IS NULL) OR ([InvoiceId] IS NULL AND [BankTransactionId] IS NOT NULL)))) AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ResolutionNote] IS NULL AND ([SourceLabel] IS NULL OR [SourceLabel] COLLATE Latin1_General_100_BIN2 IN (N'manual', N'exact-1', N'smart-1')) AND ([Note] IS NULL OR (DATALENGTH([Note]) = DATALENGTH(LTRIM(RTRIM([Note]))) AND DATALENGTH([Note]) BETWEEN 2 AND 1000))))");
                    table.CheckConstraint("CK_ReviewCaseAuditEntries_ResolvedShape", "([Action] <> 2 OR ([Reason] IS NULL AND [InvoiceId] IS NULL AND [BankTransactionId] IS NULL AND [SourceLabel] IS NULL AND [Note] IS NULL AND [Outcome] IS NOT NULL AND [Outcome] IN (1, 2, 3) AND (([Outcome] = 1 AND [AllocationId] IS NOT NULL) OR ([Outcome] IN (2, 3) AND [AllocationId] IS NULL)) AND ([ResolutionNote] IS NULL OR (DATALENGTH([ResolutionNote]) = DATALENGTH(LTRIM(RTRIM([ResolutionNote]))) AND DATALENGTH([ResolutionNote]) BETWEEN 2 AND 1000))))");
                    table.ForeignKey(
                        name: "FK_ReviewCaseAuditEntries_Allocations_OrganizationId_AllocationId",
                        columns: x => new { x.OrganizationId, x.AllocationId },
                        principalSchema: "reconciliation",
                        principalTable: "Allocations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewCaseAuditEntries_BankTransactions_OrganizationId_BankTransactionId",
                        columns: x => new { x.OrganizationId, x.BankTransactionId },
                        principalSchema: "reconciliation",
                        principalTable: "BankTransactions",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewCaseAuditEntries_Invoices_OrganizationId_InvoiceId",
                        columns: x => new { x.OrganizationId, x.InvoiceId },
                        principalSchema: "reconciliation",
                        principalTable: "Invoices",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewCaseAuditEntries_ReviewCases_OrganizationId_ReviewCaseId",
                        columns: x => new { x.OrganizationId, x.ReviewCaseId },
                        principalSchema: "reconciliation",
                        principalTable: "ReviewCases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseAuditEntries_OrganizationId_AllocationId",
                schema: "reconciliation",
                table: "ReviewCaseAuditEntries",
                columns: new[] { "OrganizationId", "AllocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseAuditEntries_OrganizationId_BankTransactionId",
                schema: "reconciliation",
                table: "ReviewCaseAuditEntries",
                columns: new[] { "OrganizationId", "BankTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseAuditEntries_OrganizationId_InvoiceId",
                schema: "reconciliation",
                table: "ReviewCaseAuditEntries",
                columns: new[] { "OrganizationId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReviewCaseAuditEntries_OrganizationId_ReviewCaseId_Action",
                schema: "reconciliation",
                table: "ReviewCaseAuditEntries",
                columns: new[] { "OrganizationId", "ReviewCaseId", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReviewCaseAuditEntries_OrganizationId_ReviewCaseId_Terminal",
                schema: "reconciliation",
                table: "ReviewCaseAuditEntries",
                columns: new[] { "OrganizationId", "ReviewCaseId" },
                unique: true,
                filter: "[Action] IN (2, 3)");

            // CREATE TRIGGER must be the first statement of its batch. EXEC keeps that
            // requirement inside both the ordinary script and the idempotent IF block.
            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER [reconciliation].[TR_ReviewCaseAuditEntries_RejectMutation]
                ON [reconciliation].[ReviewCaseAuditEntries]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50002, ''Review case audit entries cannot be updated or deleted.'', 1;
                END');
                """);
        }

        /// <summary>
        /// Drops <c>ReviewCaseAuditEntries</c> and therefore deletes stored review-case history.
        /// This is not an automatic procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_ReviewCaseAuditEntries_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "ReviewCaseAuditEntries",
                schema: "reconciliation");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ReviewCases_OrganizationId_Id",
                schema: "reconciliation",
                table: "ReviewCases");
        }
    }
}
