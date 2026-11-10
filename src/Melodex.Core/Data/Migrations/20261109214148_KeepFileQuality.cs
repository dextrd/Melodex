using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepFileQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BitDepth",
                table: "LibraryFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BitrateKbps",
                table: "LibraryFiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Codec",
                table: "LibraryFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SampleRate",
                table: "LibraryFiles",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BitDepth",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "BitrateKbps",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "Codec",
                table: "LibraryFiles");

            migrationBuilder.DropColumn(
                name: "SampleRate",
                table: "LibraryFiles");
        }
    }
}
