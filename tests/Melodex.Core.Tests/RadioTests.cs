using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Radio (#4): songs like a song, album, artist or playlist, without end, from the discovery plugins and the library.</summary>
public sealed class RadioTests : IDisposable
{
    private const string Portishead = "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11";
    private const string MassiveAttack = "10adbe5e-a2c0-4bf3-8249-2b4cbf6e6ca8";
    private const string Tricky = "9a8b6a4a-4d64-4c31-8f5a-7e1b2a1a1a01";
    private const string GloryBox = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a01";
    private const string Teardrop = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a02";
    private const string Overcome = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a03";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly MelodexClient _api;

    public RadioTests()
    {
        DiscoveryCache.Clear();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new FakeListenBrainz();
                http.RateLimit = false;
            })));
        _http = _factory.CreateSignedInClient();
        _api = new MelodexClient(_http);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
        DiscoveryCache.Clear();
    }

    [Fact]
    public async Task A_radio_from_a_song_plays_it_then_songs_like_it_with_your_share_of_ones_you_dont_have()
    {
        var s = await SeedAsync();
        Assert.Equal(25, (await _api.GetFetchSettingsAsync()).RadioMissingPercent);
        Assert.Equal(new RadioSettings(true), await _api.GetRadioSettingsAsync());

        // Only songs you have: Glory Box first, as it was started from, then Massive Attack's, found like it or by an artist like Portishead.
        await ShareAsync(0);
        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox)))!;
        Assert.Equal(("Glory Box", RadioSeedKind.Song), (batch.Name, batch.Kind));
        Assert.Equal(s.GloryBox, batch.Songs[0].TrackId);
        Assert.Equal(new[] { s.Angel, s.Teardrop }.Order(), batch.Songs.Skip(1).Select(x => x.TrackId).Order());
        Assert.All(batch.Songs.Skip(1), x => Assert.True(x.Owned));

        // All songs you don't have: Overcome, then, with nothing else like it, songs you have.
        await ShareAsync(100);
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox])))!;
        Assert.Equal(s.Overcome, batch.Songs[0].TrackId);
        Assert.False(batch.Songs[0].Owned);
        Assert.DoesNotContain(batch.Songs, x => x.TrackId == s.GloryBox);
        // Unless only those you have are asked for, as Subsonic players do.
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox], OwnedOnly: true)))!;
        Assert.DoesNotContain(batch.Songs, x => x.TrackId == s.Overcome);

        // What it's queued doesn't come again.
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox, s.Overcome, s.Teardrop])))!;
        Assert.Equal([s.Angel], batch.Songs.Select(x => x.TrackId));

        Assert.Null(await _api.NextRadioAsync(new RadioNext(Guid.NewGuid())));
        var invalid = await _http.PutAsJsonAsync("api/v1/settings/fetch", await _api.GetFetchSettingsAsync() with { RadioMissingPercent = 150 },
            MelodexJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await _api.SaveRadioSettingsAsync(new RadioSettings(false));
        Assert.Equal(new RadioSettings(false), await _api.GetRadioSettingsAsync());
    }

    [Fact]
    public async Task The_share_is_a_target_over_the_whole_radio()
    {
        var s = await SeedAsync();
        await ShareAsync(50);

        // Two songs played, both ones you have: the next one is one you don't, to make up for them.
        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox, s.Teardrop, s.Angel], Take: 1)))!;
        Assert.Equal([s.Overcome], batch.Songs.Select(x => x.TrackId));
    }

    [Fact]
    public async Task A_song_skipped_quickly_isnt_played_again_and_its_artist_comes_up_less()
    {
        var s = await SeedAsync();
        await ShareAsync(0);
        await _api.SkipRadioSongAsync(s.Teardrop);

        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox])))!;
        Assert.Equal([s.Angel], batch.Songs.Select(x => x.TrackId));
        using var scope = _factory.Services.CreateScope();
        var skip = scope.ServiceProvider.GetRequiredService<MelodexDbContext>().RadioSkips.Single();
        Assert.Equal((s.Teardrop, s.Massive), (skip.TrackId, skip.ArtistId));
    }

    [Fact]
    public async Task Radios_start_from_albums_artists_and_your_playlists_and_subsonic_players_mix_from_them()
    {
        var s = await SeedAsync();
        await ShareAsync(0);

        var album = (await _api.NextRadioAsync(new RadioNext(s.Mezzanine)))!;
        Assert.Equal(("Mezzanine", RadioSeedKind.Album), (album.Name, album.Kind));
        Assert.NotEmpty(album.Songs);
        var artist = (await _api.NextRadioAsync(new RadioNext(s.Massive)))!;
        Assert.Equal(("Massive Attack", RadioSeedKind.Artist), (artist.Name, artist.Kind));
        Assert.NotEmpty(artist.Songs);

        var playlist = await _api.CreatePlaylistAsync(new CreatePlaylist("Night", Songs: [new PlaylistItem(s.Teardrop)]));
        var fromPlaylist = (await _api.NextRadioAsync(new RadioNext(playlist.Playlist.Id)))!;
        Assert.Equal(("Night", RadioSeedKind.Playlist), (fromPlaylist.Name, fromPlaylist.Kind));
        Assert.Contains(fromPlaylist.Songs, x => x.TrackId == s.Angel);

        // A Subsonic player's instant mix, from a song by its file: songs you have only, not the song itself.
        var file = await FileOfAsync(s.Teardrop);
        var key = (await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key;
        var answer = JsonNode.Parse(await _factory.CreateClient().GetStringAsync($"rest/getSimilarSongs?id={file}&count=10&f=json&v=1.16.1&c=test&apiKey={key}"))!;
        var songs = answer["subsonic-response"]!["similarSongs"]!["song"]!.AsArray().Select(n => n!["title"]!.GetValue<string>()).ToList();
        Assert.Contains("Angel", songs);
        Assert.DoesNotContain("Teardrop", songs);
        Assert.DoesNotContain("Overcome", songs);
    }

    [Fact]
    public void The_share_of_songs_you_dont_have_comes_out_right_over_time()
    {
        Assert.Equal(1, RadioService.Share(4, 0.25));
        Assert.Equal(0, RadioService.Share(5, 0));
        Assert.Equal(5, RadioService.Share(5, 1));
        var total = Enumerable.Range(0, 2000).Sum(_ => RadioService.Share(5, 0.25));
        Assert.InRange(total / 2000.0, 1.15, 1.35);
    }

    private async Task ShareAsync(int percent) =>
        await _api.SaveFetchSettingsAsync(await _api.GetFetchSettingsAsync() with { RadioMissingPercent = percent });

    private sealed record Seeded(Guid Portishead, Guid Massive, Guid GloryBox, Guid Teardrop, Guid Angel, Guid Overcome, Guid Mezzanine);

    /// <summary>
    /// Portishead's Glory Box, not in the library; Massive Attack's Teardrop and Angel, on Mezzanine, in it; and Tricky's
    /// Overcome, which isn't.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var portishead = new Artist { Name = "Portishead", InLibrary = false };
        var massive = new Artist { Name = "Massive Attack", InLibrary = true };
        var tricky = new Artist { Name = "Tricky" };
        var gloryBox = new Track { Artist = portishead, Title = "Glory Box", ArtistCredit = "Portishead" };
        var teardrop = new Track { Artist = massive, Title = "Teardrop", ArtistCredit = "Massive Attack" };
        var angel = new Track { Artist = massive, Title = "Angel", ArtistCredit = "Massive Attack" };
        var overcome = new Track { Artist = tricky, Title = "Overcome", ArtistCredit = "Tricky" };
        var mezzanine = new Album { Artist = massive, Title = "Mezzanine", ArtistCredit = "Massive Attack", Type = AlbumType.Album };
        var edition = new Edition { Album = mezzanine, Title = "Mezzanine" };
        db.AddRange(portishead, massive, tricky, gloryBox, teardrop, angel, overcome, mezzanine, edition);
        db.EditionTracks.AddRange(new EditionTrack { Edition = edition, Track = angel, Number = "1", Position = 1, Title = "Angel" }, new EditionTrack { Edition = edition, Track = teardrop, Number = "3", Position = 3, Title = "Teardrop" });
        db.Crosswalk.AddRange(
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = portishead.Id, Provider = Providers.MusicBrainz, Value = Portishead },
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = massive.Id, Provider = Providers.MusicBrainz, Value = MassiveAttack },
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = tricky.Id, Provider = Providers.MusicBrainz, Value = Tricky },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = gloryBox.Id, Provider = Providers.MusicBrainz, Value = GloryBox },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = teardrop.Id, Provider = Providers.MusicBrainz, Value = Teardrop },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = overcome.Id, Provider = Providers.MusicBrainz, Value = Overcome });
        db.LibraryFiles.AddRange(
            new LibraryFile { Path = "Massive Attack/Mezzanine/03 Teardrop.flac", TrackId = teardrop.Id, EditionId = edition.Id },
            new LibraryFile { Path = "Massive Attack/Mezzanine/01 Angel.flac", TrackId = angel.Id, EditionId = edition.Id });
        await db.SaveChangesAsync();
        return new Seeded(portishead.Id, massive.Id, gloryBox.Id, teardrop.Id, angel.Id, overcome.Id, mezzanine.Id);
    }

    private async Task<Guid> FileOfAsync(Guid track)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MelodexDbContext>().LibraryFiles.Single(f => f.TrackId == track).Id;
    }

    /// <summary>
    /// Stands in for ListenBrainz: Teardrop and Overcome are like Glory Box, and Glory Box like anything else; Massive
    /// Attack are like Portishead.
    /// </summary>
    private sealed class FakeListenBrainz : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(url.Query);
            object? answer = (url.Host, url.AbsolutePath) switch
            {
                ("labs.api.listenbrainz.org", "/similar-artists/json") => new object[]
                {
                    new { artist_mbid = MassiveAttack, name = "Massive Attack", score = 11000 },
                },
                ("labs.api.listenbrainz.org", "/similar-recordings/json") when query["recording_mbids"] == GloryBox => new object[]
                {
                    new { recording_mbid = Teardrop, recording_name = "Teardrop", artist_credit_name = "Massive Attack", score = 200 },
                    new { recording_mbid = Overcome, recording_name = "Overcome", artist_credit_name = "Tricky", score = 100 },
                },
                ("labs.api.listenbrainz.org", "/similar-recordings/json") => new object[]
                {
                    new { recording_mbid = GloryBox, recording_name = "Glory Box", artist_credit_name = "Portishead", score = 100 },
                },
                ("labs.api.listenbrainz.org", "/acr-lookup/json") => Array.Empty<object>(),
                ("api.listenbrainz.org", "/1/metadata/recording/") => query["recording_mbids"]!.Split(',').ToDictionary(m => m, m => (object)new
                {
                    artist = new
                    {
                        name = Name(m).Artist,
                        artists = new[] { new { artist_mbid = Name(m).ArtistMbid, name = Name(m).Artist } },
                    },
                    recording = new { name = Name(m).Title, length = 300000 },
                }),
                _ => null,
            };
            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(answer), System.Text.Encoding.UTF8, "application/json"),
                });
        }

        private static (string Title, string Artist, string ArtistMbid) Name(string mbid) => mbid switch
        {
            Teardrop => ("Teardrop", "Massive Attack", MassiveAttack),
            Overcome => ("Overcome", "Tricky", Tricky),
            _ => ("Glory Box", "Portishead", Portishead),
        };
    }
}
