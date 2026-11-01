using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>
/// The missing view's choice for each kind of release, settings saved before there was one, songs owned through their
/// ISRCs, and the library's lists being sent only when they've changed.
/// </summary>
public sealed class ReleaseShowingTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ReleaseShowingTests() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("Melodex:DataPath", _dataPath));

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public void Settings_from_before_each_kind_had_a_choice_read_as_they_were()
    {
        var old = JsonSerializer.Deserialize<LibrarySettings>(
            """{"shownTypes":["album","ep","single","broadcast"],"hideSecondaryTypes":false,"hideUnofficial":true,"preferFileMetadata":true}""",
            MelodexJson.Options)!;
        Assert.Equal(ReleaseShowing.Show, old.ShowingOf(ReleaseKind.Broadcast));
        Assert.Equal(ReleaseShowing.Hide, old.ShowingOf(ReleaseKind.Other));
        Assert.Equal(ReleaseShowing.Show, old.ShowingOf(ReleaseKind.Live));
        Assert.Equal(ReleaseShowing.Hide, old.ShowingOf(ReleaseKind.Bootleg));
        Assert.True(old.PreferFileTags);
        Assert.True(old.PreferFileCovers);

        // Saved again, only the new settings are written, and they read back the same.
        var saved = old with { Showing = Enum.GetValues<ReleaseKind>().ToDictionary(k => k, old.ShowingOf) };
        var json = JsonSerializer.Serialize(saved, MelodexJson.Options);
        Assert.DoesNotContain("shownTypes", json);
        Assert.DoesNotContain("preferFileMetadata", json);
        var again = JsonSerializer.Deserialize<LibrarySettings>(json, MelodexJson.Options)!;
        Assert.All(Enum.GetValues<ReleaseKind>(), kind => Assert.Equal(old.ShowingOf(kind), again.ShowingOf(kind)));
        Assert.True(again.PreferFileTags && again.PreferFileCovers);

        // A release that's several kinds follows the strictest.
        var settings = new LibrarySettings { Showing = new Dictionary<ReleaseKind, ReleaseShowing> { [ReleaseKind.Live] = ReleaseShowing.NewSongsOnly } };
        Assert.Equal(ReleaseShowing.NewSongsOnly, settings.ShowingOf(ReleaseKinds.Of(AlbumType.Album, [AlbumSecondaryType.Live], true)));
        Assert.Equal(ReleaseShowing.Hide, settings.ShowingOf(ReleaseKinds.Of(AlbumType.Album, [AlbumSecondaryType.Live, AlbumSecondaryType.Compilation], true)));
        Assert.Equal(ReleaseShowing.Hide, settings.ShowingOf(ReleaseKinds.Of(AlbumType.Album, [AlbumSecondaryType.Live], false)));
    }

    [Fact]
    public async Task New_songs_only_lists_a_release_just_for_songs_found_nowhere_else()
    {
        Guid artistId, studioId, liveId, hitsId, bootlegId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var artist = new Artist { Name = "Band", InLibrary = true, AlbumsRefreshedAt = DateTime.UtcNow };
            db.Artists.Add(artist);
            var one = Track(artist, "Song One");
            var two = Track(artist, "Song Two");
            var studio = Album(artist, "Studio", [], (one, "Song One"), (two, "Song Two"));
            // A live take is another recording of the same song, so it's found by its title. A placeholder isn't a song.
            var live = Album(artist, "Live at the Hall", [AlbumSecondaryType.Live], (Track(artist, "Song One (Live)"), "Song One (Live)"),
                (Track(artist, "Live Only (Live)"), "Live Only (Live)"), (Track(artist, "[untitled]"), "[untitled]"));
            var hits = Album(artist, "Hits", [AlbumSecondaryType.Compilation], (one, "Song One"), (two, "Song Two"));
            var bootleg = Album(artist, "Rarities", [], (Track(artist, "Rare"), "Rare"));
            bootleg.HasOfficialRelease = false;
            await db.SaveChangesAsync();
            (artistId, studioId, liveId, hitsId, bootlegId) = (artist.Id, studio.Id, live.Id, hits.Id, bootleg.Id);

            await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(new LibrarySettings
            {
                Showing = new Dictionary<ReleaseKind, ReleaseShowing>
                {
                    [ReleaseKind.Live] = ReleaseShowing.NewSongsOnly,
                    [ReleaseKind.Compilation] = ReleaseShowing.NewSongsOnly,
                    [ReleaseKind.Bootleg] = ReleaseShowing.Show,
                },
            }, default);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            var albums = (await view.GetArtistAsync(artistId, default))!.Albums.ToDictionary(a => a.Id);
            Assert.False(albums[studioId].HiddenByDefault);
            Assert.Equal(2, albums[studioId].TotalTracks);

            // Listed for its one new song, which is all that counts as missing.
            Assert.False(albums[liveId].HiddenByDefault);
            Assert.Equal(1, albums[liveId].TotalTracks);
            var live = (await view.GetAlbumAsync(liveId, null, default))!;
            Assert.Equal(TrackStatus.Elsewhere, live.Tracks.Single(t => t.Title == "Song One (Live)").Status);
            Assert.Equal(TrackStatus.Elsewhere, live.Tracks.Single(t => t.Title == "[untitled]").Status);
            Assert.Equal(TrackStatus.Missing, live.Tracks.Single(t => t.Title == "Live Only (Live)").Status);

            // Nothing on it that isn't on the studio album.
            Assert.True(albums[hitsId].HiddenByDefault);
            Assert.False(albums[bootlegId].HiddenByDefault);

            var missing = await view.GetMissingAsync(default);
            Assert.Equal(new[] { "Live at the Hall", "Rarities", "Studio" }, missing.Select(m => m.Album.Title).Order());

            // Asked for on their own, as search results and a song's page do, they're judged the same.
            var few = await view.GetAlbumSummariesAsync([liveId, hitsId], default);
            Assert.All(few, a => Assert.Equal((albums[a.Id].HiddenByDefault, albums[a.Id].TotalTracks), (a.HiddenByDefault, a.TotalTracks)));
        }

        // With the defaults, only the studio album is listed.
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SettingsService>().SaveLibraryAsync(new LibrarySettings(), default);
            var missing = await scope.ServiceProvider.GetRequiredService<LibraryView>().GetMissingAsync(default);
            Assert.Equal("Studio", Assert.Single(missing).Album.Title);
        }
    }

    [Fact]
    public async Task An_artist_with_their_own_choices_follows_them_until_put_back_to_the_defaults()
    {
        Guid ownId, otherId, ownLiveId, otherLiveId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var own = new Artist { Name = "Own", InLibrary = true, AlbumsRefreshedAt = DateTime.UtcNow };
            var other = new Artist { Name = "Other", InLibrary = true, AlbumsRefreshedAt = DateTime.UtcNow };
            db.Artists.AddRange(own, other);
            var ownLive = Album(own, "Own Live", [AlbumSecondaryType.Live], (Track(own, "Encore"), "Encore"));
            var otherLive = Album(other, "Other Live", [AlbumSecondaryType.Live], (Track(other, "Encore"), "Encore"));
            await db.SaveChangesAsync();
            (ownId, otherId, ownLiveId, otherLiveId) = (own.Id, other.Id, ownLive.Id, otherLive.Id);

            // Live hidden for everyone but this one artist.
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SaveArtistShowingAsync(ownId, new Dictionary<ReleaseKind, ReleaseShowing> { [ReleaseKind.Live] = ReleaseShowing.Show }, default);
            // Saving the settings for everyone leaves the artist's own alone.
            await settings.SaveLibraryAsync(await settings.GetLibraryAsync(default) with { MatchThreshold = 0.9 }, default);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            var own = (await view.GetArtistAsync(ownId, default))!;
            Assert.True(own.OwnShowing);
            Assert.False(own.Albums.Single(a => a.Id == ownLiveId).HiddenByDefault);
            var other = (await view.GetArtistAsync(otherId, default))!;
            Assert.False(other.OwnShowing);
            Assert.True(other.Albums.Single(a => a.Id == otherLiveId).HiddenByDefault);
            Assert.Equal("Own Live", Assert.Single(await view.GetMissingAsync(default)).Album.Title);

            // Every kind is written out: the ones not chosen are as the defaults were.
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var library = await settings.GetLibraryAsync(default);
            Assert.Equal(Enum.GetValues<ReleaseKind>().Length, library.ArtistShowing[ownId].Count);
            Assert.Equal(ReleaseShowing.Hide, library.ShowingOf(ownId, ReleaseKind.Compilation));
            Assert.Equal(0.9, library.MatchThreshold);

            await settings.SaveArtistShowingAsync(ownId, null, default);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            var own = (await view.GetArtistAsync(ownId, default))!;
            Assert.False(own.OwnShowing);
            Assert.True(own.Albums.Single(a => a.Id == ownLiveId).HiddenByDefault);
            Assert.Empty(await view.GetMissingAsync(default));
        }
    }

    [Fact]
    public async Task A_song_is_owned_when_a_recording_sharing_its_ISRC_is_in_the_library()
    {
        Guid singleId, otherId, takeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var artist = new Artist { Name = "Band", InLibrary = true, AlbumsRefreshedAt = DateTime.UtcNow };
            db.Artists.Add(artist);
            var song = Track(artist, "Song");
            var take = Track(artist, "Song");
            var studio = Album(artist, "Studio", [], (song, "Song"));
            var single = Album(artist, "Song", [], (take, "Song"), (Track(artist, "B-side"), "B-side"));
            var other = Album(artist, "Other", [], (Track(artist, "Song"), "Song"));
            await db.SaveChangesAsync();
            db.Crosswalk.AddRange(
                new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = song.Id, Provider = Providers.Isrc, Value = "GBAAA0000001" },
                new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = take.Id, Provider = Providers.Isrc, Value = "GBAAA0000001" });
            db.LibraryFiles.Add(new LibraryFile
            {
                Path = "Band/Studio/01 Song.flac", Format = "flac", TrackId = song.Id, EditionId = studio.Editions[0].Id, ArtistId = artist.Id,
                MatchedAt = DateTime.UtcNow, MatchMethod = MatchMethod.MusicBrainzTags,
            });
            await db.SaveChangesAsync();
            (singleId, otherId, takeId) = (single.Id, other.Id, take.Id);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            // The album's page reads only its own songs' files, and still finds it owned through the ISRC.
            var single = (await view.GetAlbumAsync(singleId, null, default))!;
            Assert.Equal(TrackStatus.OwnedElsewhere, single.Tracks.Single(t => t.TrackId == takeId).Status);
            Assert.Equal(TrackStatus.Missing, single.Tracks.Single(t => t.Title == "B-side").Status);
            Assert.Equal(1, single.Album.OwnedTracks);
            // Another recording of the same name without the ISRC is a song of its own.
            Assert.Equal(TrackStatus.Missing, Assert.Single((await view.GetAlbumAsync(otherId, null, default))!.Tracks).Status);

            // A few albums at once, as search results and a song's page ask, say the same as the artist's page.
            var artist = (await view.GetArtistAsync(single.Album.ArtistId, default))!.Albums.ToDictionary(a => a.Id);
            var few = await view.GetAlbumSummariesAsync([otherId, singleId], default);
            Assert.Equal([otherId, singleId], few.Select(a => a.Id));
            Assert.All(few, a => Assert.Equal((artist[a.Id].Status, artist[a.Id].OwnedTracks, artist[a.Id].TotalTracks), (a.Status, a.OwnedTracks, a.TotalTracks)));
        }
    }

    [Fact]
    public async Task Library_lists_unchanged_since_the_client_last_had_them_are_not_sent_again()
    {
        var http = _factory.CreateSignedInClient();
        using var first = await http.GetAsync("api/v1/library/albums");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = first.Headers.ETag!.ToString();

        using var again = new HttpRequestMessage(HttpMethod.Get, "api/v1/library/albums");
        again.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var second = await http.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        // Kept for after a restart.
        Assert.True(File.Exists(Path.Combine(_dataPath, "cache", "albums.json")));

        // The client keeps what it was sent, and gets the same list back.
        var api = new MelodexClient(_factory.CreateSignedInClient());
        Assert.Same(await api.GetLibraryAlbumsAsync(), await api.GetLibraryAlbumsAsync());
    }

    [Theory]
    [InlineData("Glory Box", "glory box")]
    [InlineData("Glory Box (Live)", "glory box")]
    [InlineData("It’s a Fire [2014 remaster] (live)", "it's a fire")]
    [InlineData("Harder, Better, Faster, Stronger - Alive 2007", "harder, better, faster, stronger")]
    [InlineData("Applause (Faded in)", "applause")]
    [InlineData("Rollin' & Scratchin' (Live in Los Angeles)", "rollin' and scratchin'")]
    [InlineData("[untitled]", "")]
    public void Songs_are_compared_without_notes_on_the_version(string title, string key) => Assert.Equal(key, LibraryView.SongKey(title));

    private static Track Track(Artist artist, string title) => new() { Artist = artist, Title = title, ArtistCredit = artist.Name };

    private static Album Album(Artist artist, string title, List<AlbumSecondaryType> secondary, params (Track Track, string Title)[] tracks)
    {
        var album = new Album
        {
            Artist = artist, Title = title, ArtistCredit = artist.Name, Type = AlbumType.Album, SecondaryTypes = secondary,
            HasOfficialRelease = true, EditionsRefreshedAt = DateTime.UtcNow,
        };
        var edition = new Edition { Album = album, Title = title, Status = "Official", TrackCount = tracks.Length };
        edition.Tracks.AddRange(tracks.Select((t, i) => new EditionTrack
        {
            Edition = edition, Track = t.Track, Disc = 1, Position = i + 1, Number = (i + 1).ToString(), Title = t.Title,
        }));
        album.Editions.Add(edition);
        artist.Albums.Add(album);
        return album;
    }
}
