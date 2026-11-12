using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Melodex.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaylistFoldersPerServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Playlist folders' separator moves from each person's look to the media servers that sync playlists: they keep
            // the admin's, or everyone's look's, so playlists' folders stay as they were.
            const string separator = """
                COALESCE(
                    (SELECT json_extract(Appearance, '$.playlistFolders') FROM Users WHERE IsAdmin AND json_valid(Appearance)),
                    (SELECT json_extract(ValueJson, '$.playlistFolders') FROM Settings WHERE "Key" = 'appearance.default' AND json_valid(ValueJson)))
                """;
            migrationBuilder.Sql($"""
                UPDATE PluginInstances SET SettingsJson = json_set(SettingsJson, '$.playlistFolders', {separator})
                WHERE Type = 'Connector' AND PluginId IN ('melodex.connector.navidrome', 'melodex.connector.jellyfin')
                  AND json_valid(SettingsJson) AND json_extract(SettingsJson, '$.playlistFolders') IS NULL AND {separator} IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
