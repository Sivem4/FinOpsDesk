using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCaseComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewCaseComments",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCaseComments", x => x.Id);
                    table.CheckConstraint("CK_ReviewCaseComments_ActorId", "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND 512");
                    table.CheckConstraint("CK_ReviewCaseComments_IdempotencyKey", "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ReviewCaseComments_Text", "DATALENGTH([Text]) = DATALENGTH(LTRIM(RTRIM([Text]))) AND DATALENGTH([Text]) BETWEEN 2 AND 1000");
                    table.ForeignKey(
                        name: "FK_ReviewCaseComments_ReviewCases_OrganizationId_ReviewCaseId",
                        columns: x => new { x.OrganizationId, x.ReviewCaseId },
                        principalSchema: "reconciliation",
                        principalTable: "ReviewCases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCaseComments_OrganizationId_ReviewCaseId_RecordedAt_Id",
                schema: "reconciliation",
                table: "ReviewCaseComments",
                columns: new[] { "OrganizationId", "ReviewCaseId", "RecordedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_ReviewCaseComments_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "ReviewCaseComments",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            // CREATE TRIGGER must be the first statement of its batch. EXEC keeps that
            // requirement inside both the ordinary script and the idempotent IF block.
            migrationBuilder.Sql(
                """
                EXEC(N'CREATE TRIGGER [reconciliation].[TR_ReviewCaseComments_RejectMutation]
                ON [reconciliation].[ReviewCaseComments]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50003, ''Review case comments cannot be updated or deleted.'', 1;
                END');
                """);
        }

        /// <summary>
        /// Drops <c>ReviewCaseComments</c> and therefore deletes stored review-case comments.
        /// This is not an automatic procedure for reversing a production deployment.
        /// </summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER [reconciliation].[TR_ReviewCaseComments_RejectMutation];
                """);

            migrationBuilder.DropTable(
                name: "ReviewCaseComments",
                schema: "reconciliation");
        }
    }
}
