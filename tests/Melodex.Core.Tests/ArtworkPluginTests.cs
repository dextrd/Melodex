using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Plugins.Deezer;
using Melodex.Plugins.FanartTv;
using Melodex.Plugins.MusicBrainz;
using Melodex.Plugins.Wikimedia;

namespace Melodex.Core.Tests;

public sealed class ArtworkPluginTests
{
    [Fact]
    public void Fanart_small_pictures_come_from_the_preview_folder()
    {
        Assert.Equal("https://assets.fanart.tv/preview/music/abc/artistthumb/x.jpg",
            FanartTv.Preview("https://assets.fanart.tv/fanart/music/abc/artistthumb/x.jpg"));
        Assert.Equal("https://example.com/fanart/x.jpg", FanartTv.Preview("https://example.com/fanart/x.jpg"));
    }

    [Fact]
    public void Deezer_takes_the_best_known_artist_of_exactly_that_name_with_a_picture()
    {
        var search = JsonDocument.Parse("""
            { "data": [
              { "name": "Bjork Tribute Band", "nb_fan": 900000, "picture_big": "https://cdn-images.dzcdn.net/images/artist/aaa/500x500.jpg" },
              { "name": "Björk", "nb_fan": 12, "picture_big": "https://cdn-images.dzcdn.net/images/artist/bbb/500x500.jpg" },
              { "name": "BJORK", "nb_fan": 5000, "picture_big": "https://cdn-images.dzcdn.net/images/artist//500x500-000000-80-0-0.jpg" },
              { "name": "bjork ", "nb_fan": 400, "picture_big": "https://cdn-images.dzcdn.net/images/artist/ccc/500x500.jpg" }
            ], "total": 4 }
            """).RootElement;
        // The most followed exact match has only the placeholder, so the next one's picture is used.
        Assert.Equal("https://cdn-images.dzcdn.net/images/artist/ccc/500x500.jpg", Deezer.Pick(search, "Björk", "big"));
        Assert.Null(Deezer.Pick(search, "Sugarcubes", "big"));
        Assert.Null(Deezer.Pick(JsonDocument.Parse("""{ "data": [], "total": 0 }""").RootElement, "Björk", "big"));
    }

    [Fact]
    public void Wikimedia_prefers_preferred_images_and_skips_deprecated_ones()
    {
        var claims = JsonDocument.Parse("""
            { "claims": { "P18": [
              { "rank": "normal", "mainsnak": { "datavalue": { "value": "Band live 2008.jpg" } } },
              { "rank": "deprecated", "mainsnak": { "datavalue": { "value": "Wrong band.jpg" } } },
              { "rank": "preferred", "mainsnak": { "datavalue": { "value": "Band portrait.jpg" } } }
            ] } }
            """).RootElement;
        Assert.Equal(["Band portrait.jpg", "Band live 2008.jpg"], WikimediaCommons.Files(claims));
        Assert.Empty(WikimediaCommons.Files(JsonDocument.Parse("""{ "claims": {} }""").RootElement));
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Band_portrait%28b%29.jpg?width=500",
            WikimediaCommons.ImageUrl("Band portrait(b).jpg", 500));
    }

    [Fact]
    public async Task Default_artwork_plugins_are_each_set_up_once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var catalog = new PluginCatalog(Options.Create(new MelodexOptions { PluginsPath = Path.Combine(Path.GetTempPath(), "melodex-no-plugins") }),
            NullLogger<PluginCatalog>.Instance);

        // An install from before the newer defaults: the archive was offered, and its user has since removed it.
        db.Settings.Add(new Setting { Key = ArtworkPlugins.DefaultsSetting, ValueJson = "true" });
        db.PluginInstances.Add(new PluginInstance { PluginId = FanartTvFactory.PluginId, Type = PluginType.Artwork, Name = "fanart.tv", Priority = 4, ContractVersion = "1.0" });
        await db.SaveChangesAsync();

        await ArtworkPlugins.EnsureDefaultAsync(db, catalog);
        Assert.Equal([FanartTvFactory.PluginId, DeezerFactory.PluginId, WikimediaCommonsFactory.PluginId],
            await db.PluginInstances.Where(p => p.Type == PluginType.Artwork).OrderBy(p => p.Priority).Select(p => p.PluginId).ToListAsync());

        // Removing one sticks.
        db.PluginInstances.RemoveRange(db.PluginInstances.Where(p => p.PluginId == DeezerFactory.PluginId));
        await db.SaveChangesAsync();
        await ArtworkPlugins.EnsureDefaultAsync(db, catalog);
        Assert.DoesNotContain(await db.PluginInstances.Select(p => p.PluginId).ToListAsync(), id => id is DeezerFactory.PluginId or CoverArtArchiveFactory.PluginId);
    }
}
