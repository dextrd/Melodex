using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Upgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UpgradesFileId",
                table: "Requests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HeldFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    HeldPath = table.Column<string>(type: "TEXT", nullable: false),
                    ReplacedBy = table.Column<string>(type: "TEXT", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    BitDepth = table.Column<int>(type: "INTEGER", nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: true),
                    CutoffHz = table.Column<int>(type: "INTEGER", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    HeldAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeleteAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HeldFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_UpgradesFileId",
                table: "Requests",
                column: "UpgradesFileId");

            migrationBuilder.CreateIndex(
                name: "IX_HeldFiles_DeleteAt",
                table: "HeldFiles",
                column: "DeleteAt");

            migrationBuilder.CreateIndex(
                name: "IX_HeldFiles_LibraryFileId",
                table: "HeldFiles",
                column: "LibraryFileId");

            migrationBuilder.CreateIndex(
                name: "IX_HeldFiles_TrackId",
                table: "HeldFiles",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeldFiles");

            migrationBuilder.DropIndex(
                name: "IX_Requests_UpgradesFileId",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "UpgradesFileId",
                table: "Requests");
        }
    }
}
