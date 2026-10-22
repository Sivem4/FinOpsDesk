using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSourceIdentityAndBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                schema: "reconciliation",
                table: "Invoices",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                schema: "reconciliation",
                table: "Invoices",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                schema: "reconciliation",
                table: "BankTransactions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "SourceSystem",
                schema: "reconciliation",
                table: "BankTransactions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                schema: "reconciliation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportType = table.Column<int>(type: "int", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FileHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    ImportedRows = table.Column<int>(type: "int", nullable: false),
                    AlreadyExistsRows = table.Column<int>(type: "int", nullable: false),
                    RejectedRows = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                    table.UniqueConstraint("AK_ImportBatches_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ImportBatches_CompletedAt", "[CompletedAt] >= [CreatedAt]");
                    table.CheckConstraint("CK_ImportBatches_Counters", "[TotalRows] >= 1 AND [ImportedRows] >= 0 AND [AlreadyExistsRows] >= 0 AND [RejectedRows] >= 0 AND [TotalRows] = ([ImportedRows] + [AlreadyExistsRows] + [RejectedRows])");
                    table.CheckConstraint("CK_ImportBatches_StatusMatchesCounters", "([Status] = 2 AND [RejectedRows] = 0) OR ([Status] = 3 AND ([ImportedRows] + [AlreadyExistsRows]) > 0 AND [RejectedRows] > 0) OR ([Status] = 4 AND [ImportedRows] = 0 AND [AlreadyExistsRows] = 0 AND [RejectedRows] = [TotalRows])");
                    table.CheckConstraint("CK_ImportBatches_TerminalStatus", "[Status] IN (2, 3, 4)");
                });

            migrationBuilder.CreateTable(
                name: "ImportRowErrors",
                schema: "reconciliation",
                columns: table => new
                {
                    DataRowIndex = table.Column<int>(type: "int", nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLineNumber = table.Column<int>(type: "int", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRowErrors", x => new { x.ImportBatchId, x.DataRowIndex });
                    table.CheckConstraint("CK_ImportRowErrors_DataRowIndex", "[DataRowIndex] >= 1");
                    table.CheckConstraint("CK_ImportRowErrors_SourceLineNumber", "[SourceLineNumber] IS NULL OR [SourceLineNumber] >= 1");

                    // Hand-written. SQL Server stores RESTRICT as ON DELETE NO ACTION.
                    // The ownership snapshot can only round-trip Cascade.
                    table.ForeignKey(
                        name: "FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId",
                        columns: x => new { x.OrganizationId, x.ImportBatchId },
                        principalSchema: "reconciliation",
                        principalTable: "ImportBatches",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Invoices_SourceIdentity_NullPair",
                schema: "reconciliation",
                table: "Invoices",
                sql: "([SourceSystem] IS NULL AND [ExternalId] IS NULL) OR ([SourceSystem] IS NOT NULL AND [ExternalId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BankTransactions_SourceIdentity_NullPair",
                schema: "reconciliation",
                table: "BankTransactions",
                sql: "([SourceSystem] IS NULL AND [ExternalId] IS NULL) OR ([SourceSystem] IS NOT NULL AND [ExternalId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash",
                schema: "reconciliation",
                table: "ImportBatches",
                columns: new[] { "OrganizationId", "ImportType", "SourceSystem", "FileHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportRowErrors_OrganizationId_ImportBatchId",
                schema: "reconciliation",
                table: "ImportRowErrors",
                columns: new[] { "OrganizationId", "ImportBatchId" });

            // EF Core 10 cannot index through a complex-type property.
            // These filtered indexes are intentionally absent from the model snapshot.
            migrationBuilder.CreateIndex(
                name: "UX_Invoices_OrganizationId_SourceSystem_ExternalId",
                schema: "reconciliation",
                table: "Invoices",
                columns: new[] { "OrganizationId", "SourceSystem", "ExternalId" },
                unique: true,
                filter: "[SourceSystem] IS NOT NULL AND [ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BankTransactions_OrganizationId_SourceSystem_ExternalId",
                schema: "reconciliation",
                table: "BankTransactions",
                columns: new[] { "OrganizationId", "SourceSystem", "ExternalId" },
                unique: true,
                filter: "[SourceSystem] IS NOT NULL AND [ExternalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Invoices_OrganizationId_SourceSystem_ExternalId",
                schema: "reconciliation",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "UX_BankTransactions_OrganizationId_SourceSystem_ExternalId",
                schema: "reconciliation",
                table: "BankTransactions");

            migrationBuilder.DropTable(
                name: "ImportRowErrors",
                schema: "reconciliation");

            migrationBuilder.DropTable(
                name: "ImportBatches",
                schema: "reconciliation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Invoices_SourceIdentity_NullPair",
                schema: "reconciliation",
                table: "Invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BankTransactions_SourceIdentity_NullPair",
                schema: "reconciliation",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                schema: "reconciliation",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                schema: "reconciliation",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                schema: "reconciliation",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "SourceSystem",
                schema: "reconciliation",
                table: "BankTransactions");
        }
    }
}
