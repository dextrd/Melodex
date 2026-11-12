using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class NavidromeCoverArtRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Navidrome cover art is gone: Melodex reads your files' own covers itself. Pictures it found that nobody
            // chose are found again, from your files or the other artwork plugins.
            migrationBuilder.Sql("""
                DELETE FROM Artwork
                WHERE Chosen = 0 AND PluginInstanceId IN (SELECT Id FROM PluginInstances WHERE PluginId = 'melodex.artwork.navidrome');
                DELETE FROM PluginInstances WHERE PluginId = 'melodex.artwork.navidrome';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
