using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeezerArtistPicturesAgain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deezer found artists by name, so same-named ones shared a picture. Those it found are looked for again,
            // now by ID; pictures someone chose stay.
            migrationBuilder.Sql("""
                DELETE FROM Artwork
                WHERE EntityKind = 'Artist' AND Chosen = 0
                  AND PluginInstanceId IN (SELECT Id FROM PluginInstances WHERE PluginId = 'melodex.artwork.deezer');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
