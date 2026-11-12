using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaServerLogins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaServerLogins",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaServerLogins", x => new { x.UserId, x.PluginInstanceId });
                    table.ForeignKey(
                        name: "FK_MediaServerLogins_PluginInstances_PluginInstanceId",
                        column: x => x.PluginInstanceId,
                        principalTable: "PluginInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaServerLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaServerLogins_PluginInstanceId",
                table: "MediaServerLogins",
                column: "PluginInstanceId");

            // The media server in use was the enabled one first in the order: its users' logins are kept for it, and it
            // stays the only one enabled.
            const string active = "SELECT Id FROM PluginInstances WHERE Type = 'Connector' AND Enabled ORDER BY Priority, CreatedAt LIMIT 1";
            migrationBuilder.Sql($"""
                INSERT INTO MediaServerLogins (UserId, PluginInstanceId, Username, Password)
                SELECT Id, ({active}), MediaServerUsername, MediaServerPassword FROM Users
                WHERE MediaServerUsername IS NOT NULL AND MediaServerPassword IS NOT NULL AND ({active}) IS NOT NULL
                """);
            migrationBuilder.Sql($"UPDATE PluginInstances SET Enabled = 0 WHERE Type = 'Connector' AND Enabled AND Id <> ({active})");

            migrationBuilder.DropColumn(
                name: "MediaServerPassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MediaServerUsername",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaServerLogins");

            migrationBuilder.AddColumn<string>(
                name: "MediaServerPassword",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaServerUsername",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }
    }
}
