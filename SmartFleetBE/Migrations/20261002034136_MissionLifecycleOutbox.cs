using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartFleetBE.Migrations
{
    /// <inheritdoc />
    public partial class MissionLifecycleOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RobotCommandOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssignmentId = table.Column<long>(type: "bigint", nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RobotCommandOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RobotCommandOutbox_TaskAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "TaskAssignments",
                        principalColumn: "AssignmentID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RobotCommandOutbox_AssignmentId",
                table: "RobotCommandOutbox",
                column: "AssignmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RobotCommandOutbox_DeliveredAt",
                table: "RobotCommandOutbox",
                column: "DeliveredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RobotCommandOutbox");
        }
    }
}
