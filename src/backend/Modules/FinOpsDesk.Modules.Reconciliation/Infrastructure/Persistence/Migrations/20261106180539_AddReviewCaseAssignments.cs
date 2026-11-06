using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCaseAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedActorId",
                schema: "reconciliation",
                table: "ReviewCases",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedActorSource",
                schema: "reconciliation",
                table: "ReviewCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                schema: "reconciliation",
                table: "ReviewCases",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReviewCaseAssignmentEntries",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    ActorSource = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestConcurrencyToken = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCaseAssignmentEntries", x => x.Id);
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_ActorSource", "[ActorSource] IN (1, 2)");
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_AssignedAt", "(([Operation] = 1 AND [AssignedAt] IS NOT NULL) OR ([Operation] = 2 AND [AssignedAt] IS NULL))");
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_Operation", "[Operation] IN (1, 2)");
                    table.CheckConstraint("CK_ReviewCaseAssignmentEntries_RequestConcurrencyToken", "DATALENGTH([RequestConcurrencyToken]) = DATALENGTH(LTRIM(RTRIM([RequestConcurrencyToken]))) AND DATALENGTH([RequestConcurrencyToken]) = 24");
                    table.ForeignKey(
                        name: "FK_ReviewCaseAssignmentEntries_ReviewCases_OrganizationId_ReviewCaseId",
                        columns: x => new { x.OrganizationId, x.ReviewCaseId },
                        principalSchema: "reconciliation",
                        principalTable: "ReviewCases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewCases_Assignment",
                schema: "reconciliation",
                table: "ReviewCases",
                sql: "(([AssignedActorSource] IS NULL AND [AssignedActorId] IS NULL AND [AssignedAt] IS NULL) OR ([AssignedActorSource] IS NOT NULL AND [AssignedActorSource] IN (1, 2) AND [AssignedActorId] IS NOT NULL AND DATALENGTH([AssignedActorId]) = DATALENGTH(LTRIM(RTRIM([AssignedActorId]))) AND DATALENGTH([AssignedActorId]) BETWEEN 2 AND 512 AND [AssignedAt] IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseAssignmentEntries_OrganizationId_ReviewCaseId",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                columns: new[] { "OrganizationId", "ReviewCaseId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReviewCaseAssignmentEntries_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "ReviewCaseAssignmentEntries",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            // CREATE TRIGGER must be the first statement of its batch. EXEC keeps that
            // requirement inside both the ordinary script and the idempotent IF block.
            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER [reconciliation].[TR_ReviewCaseAssignmentEntries_RejectMutation]
                ON [reconciliation].[ReviewCaseAssignmentEntries]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50003, ''Review case assignment history cannot be updated or deleted.'', 1;
                END');
                """);
        }

        /// <summary>
        /// Drops <c>ReviewCaseAssignmentEntries</c> and the assignment columns on <c>ReviewCases</c>.
        /// Stored assignments and assignment history are deleted.
        /// This is not a procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_ReviewCaseAssignmentEntries_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "ReviewCaseAssignmentEntries",
                schema: "reconciliation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewCases_Assignment",
                schema: "reconciliation",
                table: "ReviewCases");

            migrationBuilder.DropColumn(
                name: "AssignedActorId",
                schema: "reconciliation",
                table: "ReviewCases");

            migrationBuilder.DropColumn(
                name: "AssignedActorSource",
                schema: "reconciliation",
                table: "ReviewCases");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                schema: "reconciliation",
                table: "ReviewCases");
        }
    }
}
