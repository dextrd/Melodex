using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

/// <summary>
/// Music of your own that no catalogue knows: excluded files with a title and artist, grouped into artists and albums
/// as their tags say. With no catalogue on, every file not matched before is listed this way too. An artist the library already has from the catalogue, by name, gets these albums too; anyone else
/// becomes a local artist. Their IDs come from their names, so they stay the same from scan to scan.
/// </summary>
public sealed class LocalMusic(MelodexDbContext db)
{
    /// <summary>The title of the album for songs whose tags name none.</summary>
    public const string NoAlbum = "Other songs";

    public sealed record Song(LibraryFile File, FileTags Tags)
    {
        public string Title => Tags.Title!;
        public string Artist => Tags.Artist ?? Tags.AlbumArtist!;
    }

    public sealed record Album(Guid Id, string Title, Guid ArtistId, string ArtistName, bool ArtistLocal, IReadOnlyList<Song> Songs)
    {
        public string? Date => Songs.Select(s => s.Tags.Date).Where(d => !string.IsNullOrEmpty(d)).Order(StringComparer.Ordinal).FirstOrDefault();

        public string CoverUrl => LocalMusic.CoverUrl(Songs[0].File.Id);

        public AlbumSummary Summary() => new(Id, ArtistId, Title, ArtistName, Title == NoAlbum ? AlbumType.Other : AlbumType.Album, [], Date,
            AlbumStatus.Complete, false, false, Songs.Count, Songs.Count, [], CoverUrl, Local: true,
            Quality: FileQuality.Lowest(Songs.Select(s => s.File.Quality)), MixedQuality: FileQuality.Mixed(Songs.Select(s => s.File.Quality)));
    }

    public sealed record Artist(Guid Id, string Name, IReadOnlyList<Album> Albums);

    /// <param name="Artists">Local artists only: those the catalogue artists in the library don't already cover.</param>
    /// <param name="Albums">Every album, under a local artist or a catalogue one.</param>
    public sealed record Snapshot(IReadOnlyList<Artist> Artists, IReadOnlyList<Album> Albums);

    /// <summary>The picture in a file, or beside it, served by the core.</summary>
    public static string CoverUrl(Guid fileId) => $"api/v1/library/files/{fileId}/cover";

    public static Guid ArtistId(string name) => NameId("artist", name.Trim().ToLowerInvariant());

    public static Guid AlbumId(string artist, string album) => NameId("album", $"{artist.Trim().ToLowerInvariant()}\n{album.Trim().ToLowerInvariant()}");

    public async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var plain = !await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Index && p.Enabled, ct);
        var files = await db.LibraryFiles.AsNoTracking()
            .Where(f => (f.ExcludedAt != null || plain && f.TrackId == null && f.DeferredAt == null) && f.HiddenAt == null).ToListAsync(ct);
        var songs = files.Select(f => new Song(f, FileTags.Parse(f.TagsJson))).Where(s => SetAsides.Usable(s.Tags)).ToList();
        if (songs.Count == 0)
            return new Snapshot([], []);
        var catalogue = (await db.Artists.Where(a => a.InLibrary).Select(a => new { a.Id, a.Name }).ToListAsync(ct))
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var albums = songs
            .GroupBy(s => (Artist: (s.Tags.AlbumArtist ?? s.Artist).Trim(), Album: s.Tags.Album?.Trim() is { Length: > 0 } album ? album : NoAlbum),
                Comparer.Instance)
            .Select(g =>
            {
                var local = !catalogue.TryGetValue(g.Key.Artist, out var artistId);
                if (local)
                    artistId = ArtistId(g.Key.Artist);
                var ordered = g.OrderBy(s => s.Tags.Disc ?? 1).ThenBy(s => s.Tags.Track ?? int.MaxValue).ThenBy(s => s.File.Path, StringComparer.Ordinal).ToList();
                return new Album(AlbumId(g.Key.Artist, g.Key.Album), g.Key.Album, artistId, g.Key.Artist, local, ordered);
            })
            .OrderBy(a => a.Title == NoAlbum).ThenBy(a => a.Date ?? "9999", StringComparer.Ordinal).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var artists = albums.Where(a => a.ArtistLocal).GroupBy(a => a.ArtistId)
            .Select(g => new Artist(g.Key, g.First().ArtistName, g.ToList()))
            .ToList();
        return new Snapshot(artists, albums);
    }

    public async Task<LocalArtistDetail?> GetArtistAsync(Guid id, CancellationToken ct) =>
        (await LoadAsync(ct)).Artists.FirstOrDefault(a => a.Id == id) is { } artist
            ? new LocalArtistDetail(artist.Id, artist.Name, artist.Albums.Select(a => a.Summary()).ToList(), artist.Albums[0].CoverUrl)
            : null;

    public async Task<LocalAlbumDetail?> GetAlbumAsync(Guid id, CancellationToken ct) =>
        (await LoadAsync(ct)).Albums.FirstOrDefault(a => a.Id == id) is { } album
            ? new LocalAlbumDetail(album.Id, album.Title, album.ArtistId, album.ArtistName, album.ArtistLocal, album.Date, album.CoverUrl,
                album.Songs.Select(s => new LocalTrack(s.File.Id, s.Tags.Disc, s.Tags.Track, s.Title, s.Artist, s.File.Duration?.TotalSeconds, s.File.Path,
                    s.File.Format, s.File.Quality)).ToList())
            : null;

    // A name-based ID, so the same artist or album keeps its ID however often the library is read.
    private static Guid NameId(string kind, string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"melodex-local-{kind}\n{key}"));
        // Mark it as a version 3 (name-based) UUID.
        hash[6] = (byte)(hash[6] & 0x0F | 0x30);
        hash[8] = (byte)(hash[8] & 0x3F | 0x80);
        return new Guid(hash, bigEndian: true);
    }

    private sealed class Comparer : IEqualityComparer<(string Artist, string Album)>
    {
        public static readonly Comparer Instance = new();

        public bool Equals((string Artist, string Album) x, (string Artist, string Album) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Artist, y.Artist) && StringComparer.OrdinalIgnoreCase.Equals(x.Album, y.Album);

        public int GetHashCode((string Artist, string Album) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Artist), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Album));
    }
}
