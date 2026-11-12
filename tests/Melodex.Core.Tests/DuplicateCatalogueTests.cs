using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Plugins.Deezer;

namespace Melodex.Core.Tests;

/// <summary>
/// With Deezer filling gaps after MusicBrainz, an artist Deezer names is the one MusicBrainz has, when it's clear which,
/// rather than a second one of the same name; and so is an album it has.
/// </summary>
public sealed class DuplicateCatalogueTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public DuplicateCatalogueTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Melodex:DataPath", _dataPath));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        db.PluginInstances.Add(new PluginInstance
        {
            PluginId = DeezerIndexFactory.PluginId, Type = PluginType.Index, Name = "Deezer", Priority = 10, ContractVersion = ContractVersions.Index.ToString(),
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private static Artist MusicBrainzArtist(MelodexDbContext db, string name, string mbid, bool inLibrary = false, params string[] albums)
    {
        var artist = new Artist { Name = name, InLibrary = inLibrary };
        db.Artists.Add(artist);
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = artist.Id, Provider = Providers.MusicBrainz, Value = mbid });
        foreach (var title in albums)
            db.Albums.Add(new Album { ArtistId = artist.Id, Title = title, ArtistCredit = name });
        return artist;
    }

    private static IndexAlbum DeezerAlbum(string id, string title, string artistId, string artist) =>
        new(new ProviderId(Providers.Deezer, id), title, new ArtistRef(new ProviderId(Providers.Deezer, artistId), artist), artist, AlbumType.Album, []);

    private static Task<List<Guid>> ArtistsNamedAsync(MelodexDbContext db, string name) =>
        db.Artists.Where(a => a.Name == name).Select(a => a.Id).ToListAsync();

    [Fact]
    public async Task An_album_from_Deezer_is_by_the_artist_MusicBrainz_has()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        var radiohead = MusicBrainzArtist(db, "Radiohead", "a74b1b7f", inLibrary: true, "OK Computer");
        await db.SaveChangesAsync();

        var album = await catalogue.ImportAlbumAsync(DeezerAlbum("1", "Kid A Mnesia", "399", "radiohead"), null, default);
        await db.SaveChangesAsync();

        Assert.Equal(radiohead.Id, album.ArtistId);
        Assert.Equal([radiohead.Id], await db.Artists.Where(a => a.Name.ToLower() == "radiohead").Select(a => a.Id).ToListAsync());
        Assert.Equal(radiohead.Id, await catalogue.FindAsync(EntityKind.Artist, new ProviderId(Providers.Deezer, "399"), default));
    }

    [Fact]
    public async Task Artists_with_the_same_name_are_told_apart_by_their_albums_or_not_at_all()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        MusicBrainzArtist(db, "Muse", "mb-uk", inLibrary: true, "The Resistance");
        var japanese = MusicBrainzArtist(db, "Muse", "mb-jp", inLibrary: false, "Hikari");
        MusicBrainzArtist(db, "Muse", "mb-fr", inLibrary: true, "Nuages lents");
        await db.SaveChangesAsync();

        // An album of theirs tells which Muse it is.
        Assert.Equal(japanese.Id, (await catalogue.ImportAlbumAsync(DeezerAlbum("1", "Hikari", "22", "Muse"), null, default)).ArtistId);
        // Two Muses are in the library and neither has this album: it's another Muse, rather than a guess.
        var other = await catalogue.ImportAlbumAsync(DeezerAlbum("2", "Something Else", "33", "Muse"), null, default);
        await db.SaveChangesAsync();
        Assert.Equal(4, (await ArtistsNamedAsync(db, "Muse")).Count);
        Assert.DoesNotContain(await db.Crosswalk.Where(m => m.EntityId == other.ArtistId).Select(m => m.Provider).ToListAsync(), p => p == Providers.MusicBrainz);
    }

    [Fact]
    public async Task An_artist_added_twice_before_is_merged_into_the_one_MusicBrainz_has()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        var radiohead = MusicBrainzArtist(db, "Radiohead", "a74b1b7f", inLibrary: true, "OK Computer");
        // The second Radiohead, as imports made it: only Deezer's ID, with an album of Deezer's.
        var stub = new Artist { Name = "Radiohead" };
        db.Artists.Add(stub);
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = stub.Id, Provider = Providers.Deezer, Value = "399" });
        var album = new Album { ArtistId = stub.Id, Title = "Kid A Mnesia", ArtistCredit = "Radiohead" };
        db.Albums.Add(album);
        db.Requests.Add(new Request { TargetKind = EntityKind.Artist, TargetId = stub.Id, Title = "Radiohead", Requester = "admin", Status = RequestStatus.InLibrary });
        // Another artist only Deezer has stays.
        var alaskan = new Artist { Name = "Alaskan Tapes" };
        db.Artists.Add(alaskan);
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = alaskan.Id, Provider = Providers.Deezer, Value = "7852146" });
        await db.SaveChangesAsync();

        await catalogue.MergeDuplicateArtistsAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal([radiohead.Id], await ArtistsNamedAsync(db, "Radiohead"));
        Assert.Equal(radiohead.Id, (await db.Albums.SingleAsync(a => a.Id == album.Id)).ArtistId);
        Assert.Equal(radiohead.Id, await catalogue.FindAsync(EntityKind.Artist, new ProviderId(Providers.Deezer, "399"), default));
        Assert.Equal(radiohead.Id, (await db.Requests.SingleAsync()).TargetId);
        Assert.NotNull(await db.Artists.FindAsync(alaskan.Id));
    }

    [Fact]
    public async Task An_album_from_Deezer_is_the_one_MusicBrainz_has_by_the_same_artist()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        var muse = MusicBrainzArtist(db, "Muse", "mb-uk", inLibrary: true, "The Resistance");
        await db.SaveChangesAsync();
        var resistance = await db.Albums.SingleAsync(a => a.ArtistId == muse.Id);
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Album, EntityId = resistance.Id, Provider = Providers.MusicBrainz, Value = "rg-resistance" });
        await db.SaveChangesAsync();

        var album = await catalogue.ImportAlbumAsync(DeezerAlbum("1", "The Resistance", "705", "Muse"), null, default);
        await db.SaveChangesAsync();

        Assert.Equal(resistance.Id, album.Id);
        Assert.Single(await db.Albums.Where(a => a.Title == "The Resistance").ToListAsync());
        Assert.Equal(resistance.Id, await catalogue.FindAsync(EntityKind.Album, new ProviderId(Providers.Deezer, "1"), default));
    }

    [Fact]
    public async Task An_album_added_twice_before_is_merged_with_its_editions_and_files()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var catalogue = scope.ServiceProvider.GetRequiredService<CatalogueService>();
        var muse = MusicBrainzArtist(db, "Muse", "mb-uk", inLibrary: true, "The Resistance");
        await db.SaveChangesAsync();
        var resistance = await db.Albums.SingleAsync(a => a.ArtistId == muse.Id);
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Album, EntityId = resistance.Id, Provider = Providers.MusicBrainz, Value = "rg-resistance" });
        // The Deezer copy, with its one edition and a file matched to it.
        var copy = new Album { ArtistId = muse.Id, Title = "The Resistance", ArtistCredit = "Muse" };
        var edition = new Edition { AlbumId = copy.Id, Title = "The Resistance", TrackCount = 1 };
        var track = new Track { ArtistId = muse.Id, Title = "Uprising", ArtistCredit = "Muse" };
        db.AddRange(copy, edition, track, new EditionTrack { EditionId = edition.Id, Disc = 1, Position = 1, Number = "1", Title = "Uprising", TrackId = track.Id });
        db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Album, EntityId = copy.Id, Provider = Providers.Deezer, Value = "1" });
        db.LibraryFiles.Add(new LibraryFile { Path = "Muse/The Resistance/01 Uprising.flac", TrackId = track.Id, EditionId = edition.Id, ArtistId = muse.Id, MatchedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await catalogue.MergeDuplicateAlbumsAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal([resistance.Id], await db.Albums.Where(a => a.Title == "The Resistance").Select(a => a.Id).ToListAsync());
        Assert.Equal(resistance.Id, (await db.Editions.SingleAsync(e => e.Id == edition.Id)).AlbumId);
        Assert.Equal(edition.Id, (await db.LibraryFiles.SingleAsync()).EditionId);
        Assert.Equal(resistance.Id, await catalogue.FindAsync(EntityKind.Album, new ProviderId(Providers.Deezer, "1"), default));
    }
}
