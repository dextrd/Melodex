using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepQualityOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualityOverride",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityOverride",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityOverride",
                table: "Albums",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityOverride",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "QualityOverride",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "QualityOverride",
                table: "Albums");
        }
    }
}
