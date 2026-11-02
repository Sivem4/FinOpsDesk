using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationUserSessionStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SessionStamp",
                schema: "access",
                table: "ApplicationUsers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWSEQUENTIALID()");

            // Existing rows receive a stamp. Later inserts always send one.
            migrationBuilder.AlterColumn<Guid>(
                name: "SessionStamp",
                schema: "access",
                table: "ApplicationUsers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldDefaultValueSql: "NEWSEQUENTIALID()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionStamp",
                schema: "access",
                table: "ApplicationUsers");
        }
    }
}
