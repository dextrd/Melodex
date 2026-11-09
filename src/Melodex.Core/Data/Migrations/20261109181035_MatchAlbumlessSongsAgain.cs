using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchAlbumlessSongsAgain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Songs matched on their own, with no album, though their files name one: its name wasn't the catalogue's
            // (#26). The next scan matches them again, now finding the album by the songs' recordings. Songs matched by hand
            // stay as they were.
            migrationBuilder.Sql(
                """
                UPDATE LibraryFiles SET MatchedAt = NULL
                WHERE TrackId IS NOT NULL AND EditionId IS NULL AND MatchMethod <> 'Manual' AND ExcludedAt IS NULL AND HiddenAt IS NULL
                  AND json_extract(TagsJson, '$.album') IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
