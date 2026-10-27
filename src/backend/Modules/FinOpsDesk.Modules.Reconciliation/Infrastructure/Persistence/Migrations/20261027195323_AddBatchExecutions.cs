using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.CreateTable(
                name: "BatchExecutions",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResultVersion = table.Column<int>(type: "int", nullable: true),
                    ResultPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchExecutions", x => x.Id);
                    table.CheckConstraint("CK_BatchExecutions_Shape", "([Status] = 1 AND [ResultVersion] IS NULL AND [ResultPayload] IS NULL AND [CompletedAt] IS NULL) OR ([Status] = 2 AND [ResultVersion] IS NOT NULL AND [ResultVersion] = 1 AND [ResultPayload] IS NOT NULL AND LEN([ResultPayload]) > 0 AND [CompletedAt] IS NOT NULL AND [CompletedAt] >= [CreatedAt])");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations",
                sql: "([IdempotencyKey] IS NULL AND [IdempotencyOperation] IS NULL) OR ([IdempotencyKey] IS NOT NULL AND [IdempotencyOperation] IS NOT NULL AND [IdempotencyOperation] IN (1, 2, 3))");

            migrationBuilder.CreateIndex(
                name: "UX_BatchExecutions_OrganizationId_IdempotencyKey",
                schema: "reconciliation",
                table: "BatchExecutions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchExecutions",
                schema: "reconciliation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Allocations_IdempotencyOperation",
                schema: "reconciliation",
                table: "Allocations",
                sql: "([IdempotencyKey] IS NULL AND [IdempotencyOperation] IS NULL) OR ([IdempotencyKey] IS NOT NULL AND [IdempotencyOperation] IS NOT NULL AND [IdempotencyOperation] IN (1, 2))");
        }
    }
}
