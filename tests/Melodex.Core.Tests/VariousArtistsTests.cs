using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Various Artists in the library: only the compilations you have are its albums, and its discography isn't fetched.</summary>
public sealed class VariousArtistsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public VariousArtistsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Melodex:DataPath", _dataPath));
        _ = _factory.Server;
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    [Fact]
    public async Task Only_the_compilations_you_have_count()
    {
        Guid artistId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var artist = new Artist { Name = "Various Artists", InLibrary = true };
            db.Artists.Add(artist);
            db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = artist.Id, Provider = VariousArtists.MusicBrainz.Provider, Value = VariousArtists.MusicBrainz.Value });
            foreach (var (title, owned) in new[] { ("Yours", true), ("Not yours", false), ("Nor this", false) })
            {
                var album = new Album { ArtistId = artist.Id, Title = title, ArtistCredit = artist.Name, EditionsRefreshedAt = DateTime.UtcNow };
                var edition = new Edition { AlbumId = album.Id, Title = title, Status = "Official", TrackCount = 1 };
                var track = new Track { ArtistId = artist.Id, Title = $"{title} song", ArtistCredit = "Someone" };
                db.AddRange(album, edition, track, new EditionTrack { EditionId = edition.Id, Disc = 1, Position = 1, Number = "1", Title = track.Title, TrackId = track.Id });
                if (owned)
                    db.LibraryFiles.Add(new LibraryFile
                    {
                        Path = "music/yours.flac", TrackId = track.Id, EditionId = edition.Id, ArtistId = artist.Id, MatchedAt = DateTime.UtcNow,
                        MatchMethod = MatchMethod.MusicBrainzTags, Format = "flac",
                    });
            }
            await db.SaveChangesAsync();
            artistId = artist.Id;

            // Asked for again, it's still not fetched.
            await scope.ServiceProvider.GetRequiredService<CatalogueService>().RefreshDiscographyAsync(artist, true, default);
            Assert.Null(artist.AlbumsRefreshedAt);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            var listed = Assert.Single(await view.GetLibraryArtistsAsync(default));
            Assert.Equal((1, 0), (listed.OwnedAlbums, listed.MissingAlbums));
            Assert.Equal("Yours", Assert.Single((await view.GetArtistAsync(artistId, default))!.Albums).Title);
        }
    }
}
