using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCaseDelegation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_AssignedAt",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_Operation",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.AddColumn<string>(
                name: "RecipientActorId",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_AssignedAt",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                sql: "(([Operation] IN (1, 3) AND [AssignedAt] IS NOT NULL) OR ([Operation] = 2 AND [AssignedAt] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_Operation",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                sql: "[Operation] IN (1, 2, 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_RecipientActorId",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                sql: "(([Operation] IN (1, 2) AND [RecipientActorId] IS NULL) OR ([Operation] = 3 AND [RecipientActorId] IS NOT NULL AND [RecipientActorId] COLLATE Latin1_General_100_BIN2 <> [ActorId] COLLATE Latin1_General_100_BIN2 AND DATALENGTH([RecipientActorId]) = DATALENGTH(LTRIM(RTRIM([RecipientActorId]))) AND DATALENGTH([RecipientActorId]) BETWEEN 2 AND 512))");
        }

        /// <summary>
        /// Restores the claim and release checks. A stored Delegate row refuses the downgrade
        /// before any schema change. This is not a procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM [reconciliation].[ReviewCaseAssignmentEntries]
                    WHERE [Operation] = 3)
                BEGIN
                    THROW 50006, 'Review case delegation history cannot be downgraded.', 1;
                END
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_AssignedAt",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_Operation",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_RecipientActorId",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.DropColumn(
                name: "RecipientActorId",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_AssignedAt",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                sql: "(([Operation] = 1 AND [AssignedAt] IS NOT NULL) OR ([Operation] = 2 AND [AssignedAt] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCaseAssignmentEntries_Operation",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                sql: "[Operation] IN (1, 2)");
        }
    }
}
