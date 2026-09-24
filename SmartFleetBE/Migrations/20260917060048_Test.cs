using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFleetBE.Migrations
{
    /// <inheritdoc />
    public partial class Test : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Roles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Roles",
                type: "datetime2(3)",
                precision: 3,
                nullable: false,
                defaultValueSql: "(sysutcdatetime())");
        }
    }
}
