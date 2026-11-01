using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Xunit.Abstractions;

namespace Melodex.Core.Tests;

/// <summary>
/// How long the library views take on a library about the size of a real one. Not run by default: set BENCHMARK=1.
/// </summary>
public sealed class LibraryViewBenchmark(ITestOutputHelper output) : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task Library_views_on_a_big_library()
    {
        if (Environment.GetEnvironmentVariable("BENCHMARK") != "1")
            return;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Melodex:DataPath", _dataPath));
        _ = factory.Server;
        using (var scope = factory.Services.CreateScope())
            await SeedAsync(scope.ServiceProvider.GetRequiredService<MelodexDbContext>(), artists: 300, albums: 30, editions: 3, tracks: 10, files: 3500);

        foreach (var round in new[] { "cold", "warm" })
        {
            using var scope = factory.Services.CreateScope();
            var view = scope.ServiceProvider.GetRequiredService<LibraryView>();
            var watch = Stopwatch.StartNew();
            var a = await view.GetLibraryArtistsAsync(default);
            output.WriteLine($"{round} artists: {watch.ElapsedMilliseconds} ms ({a.Count})");
            watch.Restart();
            var b = await view.GetLibraryAlbumsAsync(default);
            output.WriteLine($"{round} albums: {watch.ElapsedMilliseconds} ms ({b.Count})");
            watch.Restart();
            var c = await view.GetLibraryTracksAsync(default);
            output.WriteLine($"{round} tracks: {watch.ElapsedMilliseconds} ms ({c.Count})");
            watch.Restart();
            var found = await scope.ServiceProvider.GetRequiredService<Catalogue.CatalogueSearch>().SearchAsync("Song 12-3", null, local: true, default);
            output.WriteLine($"{round} local search: {watch.ElapsedMilliseconds} ms ({found.Artists.Count} artists, {found.Albums.Count} albums, {found.Tracks.Count} tracks)");

            // A page: an artist's, one of their albums', and a song's.
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var album = await db.Albums.OrderBy(x => x.Title).Skip(round == "cold" ? 100 : 200).FirstAsync();
            var song = await db.EditionTracks.Where(et => et.Edition.AlbumId == album.Id).Select(et => et.TrackId).FirstAsync();
            watch.Restart();
            await view.GetArtistAsync(album.ArtistId, default);
            output.WriteLine($"{round} artist page: {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            await view.GetAlbumAsync(album.Id, null, default);
            output.WriteLine($"{round} album page: {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            await view.GetTrackAsync(song, default);
            output.WriteLine($"{round} song page: {watch.ElapsedMilliseconds} ms");
        }
    }

    private static async Task SeedAsync(MelodexDbContext db, int artists, int albums, int editions, int tracks, int files)
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var random = new Random(1);
        var allEditions = new List<(Guid Edition, Guid[] Tracks, Guid Artist)>();
        for (var ar = 0; ar < artists; ar++)
        {
            var artist = new Artist { Name = $"Artist {ar}", InLibrary = true };
            db.Artists.Add(artist);
            for (var al = 0; al < albums; al++)
            {
                var album = new Album { ArtistId = artist.Id, Title = $"Album {ar}-{al}", ArtistCredit = artist.Name, FirstReleaseDate = $"{1970 + al}", EditionsRefreshedAt = DateTime.UtcNow };
                db.Albums.Add(album);
                var trackIds = new Guid[tracks];
                for (var t = 0; t < tracks; t++)
                {
                    var track = new Track { ArtistId = artist.Id, Title = $"Song {ar}-{al}-{t}", ArtistCredit = artist.Name, Duration = TimeSpan.FromMinutes(3) };
                    trackIds[t] = track.Id;
                    db.Tracks.Add(track);
                    db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = track.Id, Provider = Providers.Isrc, Value = $"ISRC{ar:000}{al:00}{t:00}" });
                }
                for (var e = 0; e < editions; e++)
                {
                    var edition = new Edition { AlbumId = album.Id, Title = album.Title, Status = "Official", TrackCount = tracks };
                    db.Editions.Add(edition);
                    for (var t = 0; t < tracks; t++)
                        db.EditionTracks.Add(new EditionTrack { EditionId = edition.Id, Disc = 1, Position = t + 1, Number = $"{t + 1}", Title = $"Song {t}", TrackId = trackIds[t] });
                    allEditions.Add((edition.Id, trackIds, artist.Id));
                }
            }
            if (ar % 20 == 19)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        for (var f = 0; f < files; f++)
        {
            var (edition, trackIds, artist) = allEditions[random.Next(allEditions.Count)];
            db.LibraryFiles.Add(new LibraryFile
            {
                Path = $"music/{f}.flac", TrackId = trackIds[random.Next(trackIds.Length)], EditionId = edition, ArtistId = artist,
                MatchedAt = DateTime.UtcNow, MatchMethod = MatchMethod.MusicBrainzTags, Format = "flac",
            });
        }
        await db.SaveChangesAsync();
    }
}
