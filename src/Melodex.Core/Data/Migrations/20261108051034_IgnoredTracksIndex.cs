using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class IgnoredTracksIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Ignored",
                table: "Tracks",
                column: "Ignored",
                filter: "\"Ignored\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_Ignored",
                table: "Tracks");
        }
    }
}
