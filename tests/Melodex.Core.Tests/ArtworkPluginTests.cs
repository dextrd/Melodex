using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;
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
    public async Task Deezer_tells_artists_with_the_same_name_apart()
    {
        const string uk = "https://cdn-images.dzcdn.net/images/artist/uk/500x500.jpg";
        const string jp = "https://cdn-images.dzcdn.net/images/artist/jp/500x500.jpg";
        const string fr = "https://cdn-images.dzcdn.net/images/artist/fr/500x500.jpg";
        var deezer = new Deezer(new PluginContext
        {
            Settings = JsonDocument.Parse("{}").RootElement,
            HttpClient = new HttpClient(new DeezerStandIn(new()
            {
                ["/artist/1"] = $$"""{"id": 1, "name": "Muse", "picture_big": "{{uk}}"}""",
                ["/artist/2"] = $$"""{"id": 2, "name": "Muse", "picture_big": "{{jp}}"}""",
                ["/artist/3"] = $$"""{"id": 3, "name": "Muse", "picture_big": "{{fr}}"}""",
                ["/search/artist?limit=25&q=Muse"] = $$"""
                    {"data": [{"id": 1, "name": "Muse", "nb_fan": 9000000, "picture_big": "{{uk}}"},
                              {"id": 2, "name": "MUSE", "nb_fan": 300, "picture_big": "{{jp}}"},
                              {"id": 3, "name": "Muse", "nb_fan": 40, "picture_big": "{{fr}}"},
                              {"id": 4, "name": "Muse Tribute", "nb_fan": 50, "picture_big": "https://cdn-images.dzcdn.net/images/artist/x/500x500.jpg"}]}
                    """,
                ["/search/artist?limit=25&q=Bjork"] = """
                    {"data": [{"id": 9, "name": "Björk", "nb_fan": 12, "picture_big": "https://cdn-images.dzcdn.net/images/artist//500x500-000000-80-0-0.jpg"}]}
                    """,
                ["/artist/1/albums?limit=100"] = """{"data": [{"title": "The Resistance"}, {"title": "Absolution"}]}""",
                ["/artist/2/albums?limit=100"] = """{"data": [{"title": "Hikari"}]}""",
                ["/artist/3/albums?limit=100"] = """{"data": [{"title": "Nuages lents"}]}""",
                // Wikidata: the French Muse's MusicBrainz ID leads to their Deezer ID.
                ["/w/api.php?format=json&action=query&list=search&srnamespace=0&srlimit=1&srsearch=haswbstatement%3AP434%3Dmb-fr"] =
                    """{"query": {"search": [{"title": "Q3"}]}}""",
                ["/w/api.php?format=json&action=wbgetclaims&property=P2722&entity=Q3"] =
                    """{"claims": {"P2722": [{"rank": "normal", "mainsnak": {"datavalue": {"value": "3"}}}]}}""",
            })),
            LoggerFactory = NullLoggerFactory.Instance,
            DataDirectory = Path.GetTempPath(),
        });
        async Task<string?> PictureAsync(string name, ProviderId[] ids, params string[] albums) =>
            (await deezer.FindAsync(new ArtworkQuery(ArtworkSubject.Artist, name, null, ids, [], [], albums), CancellationToken.None)).SingleOrDefault()?.Key;

        // By their Deezer ID, even when the best known Muse has the same name.
        Assert.Equal(jp, await PictureAsync("Muse", [new(Providers.Deezer, "2")]));
        // By their MusicBrainz ID, through Wikidata.
        Assert.Equal(fr, await PictureAsync("Muse", [new(Providers.MusicBrainz, "mb-fr")]));
        // By name, only when one of them has one of their albums.
        Assert.Equal(uk, await PictureAsync("Muse", [new(Providers.MusicBrainz, "mb-uk")], "The Resistance"));
        Assert.Equal(jp, await PictureAsync("Muse", [], "hikari"));
        Assert.Null(await PictureAsync("Muse", [], "Something Else"));
        Assert.Null(await PictureAsync("Muse", []));
        // The only artist of that name, but with Deezer's placeholder, has none.
        Assert.Null(await PictureAsync("Bjork", []));
    }

    /// <summary>Deezer and Wikidata answering only what the test gives them.</summary>
    private sealed class DeezerStandIn(Dictionary<string, string> answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            var body = answers.GetValueOrDefault(key) ?? (request.RequestUri.Host == "api.deezer.com"
                ? """{"error": {"type": "DataException", "message": "no data", "code": 800}}"""
                : """{"query": {"search": []}}""");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
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
