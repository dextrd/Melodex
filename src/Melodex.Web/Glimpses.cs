using Melodex.Client;

namespace Melodex.Web;

/// <summary>What a list already showed of an album: its head, and the songs of it you have.</summary>
public sealed record AlbumGlimpse(AlbumSummary Album, IReadOnlyList<LibraryTrack> Owned);

/// <summary>What a list already showed of a song.</summary>
public sealed record TrackGlimpse(string Title, string ArtistCredit, Guid? ArtistId, double? DurationSeconds, string? CoverUrl, bool Owned);

/// <summary>What a list already showed of an artist: their name and picture, and the albums of theirs you have.</summary>
public sealed record ArtistGlimpse(string Name, string? ImageUrl, IReadOnlyList<AlbumSummary> Owned);

/// <summary>
/// What the lists seen this session (the library's, search results, artists' and albums' pages) already say about an
/// artist, album or song, so its page can show that the moment it opens, while the page itself is asked for.
/// </summary>
public sealed class Glimpses(LibraryStore library)
{
    private const int Most = 5000;

    private readonly Dictionary<Guid, AlbumSummary> _albums = [];
    private readonly Dictionary<Guid, TrackGlimpse> _tracks = [];
    private readonly Dictionary<Guid, (string Name, string? ImageUrl)> _artists = [];

    public void Saw(IEnumerable<AlbumSummary> albums)
    {
        foreach (var album in albums.Where(a => !a.Local))
            Put(_albums, album.Id, album);
    }

    public void Saw(SearchResults results)
    {
        Saw(results.Albums);
        foreach (var artist in results.Artists)
            Put(_artists, artist.Id, (artist.Name, artist.ImageUrl));
        foreach (var track in results.Tracks)
            Put(_tracks, track.Id, new TrackGlimpse(track.Title, track.ArtistCredit, track.ArtistId, track.DurationSeconds, track.CoverUrl, track.Owned));
    }

    public void Saw(AlbumDetail album)
    {
        Saw([album.Album]);
        Put(_artists, album.Album.ArtistId, (album.ArtistName, _artists.GetValueOrDefault(album.Album.ArtistId).ImageUrl));
        foreach (var track in album.Tracks)
            Put(_tracks, track.TrackId, new TrackGlimpse(track.Title, track.ArtistCredit, null, track.DurationSeconds, album.Album.CoverUrl,
                track.Status is TrackStatus.Owned or TrackStatus.OwnedElsewhere));
    }

    public void Saw(ArtistDetail artist)
    {
        Saw(artist.Albums);
        Put(_artists, artist.Id, (artist.Name, artist.ImageUrl));
    }

    public AlbumGlimpse? Album(Guid id)
    {
        var album = library.Albums?.FirstOrDefault(a => a.Album.Id == id)?.Album ?? _albums.GetValueOrDefault(id);
        return album is null ? null : new AlbumGlimpse(album, library.Tracks?.Where(t => t.AlbumId == id && !t.Local).ToList() ?? []);
    }

    public TrackGlimpse? Track(Guid id) =>
        library.Tracks?.FirstOrDefault(t => t.TrackId == id && !t.Local) is { } owned
            ? new TrackGlimpse(owned.Title, owned.ArtistCredit, owned.ArtistId, owned.DurationSeconds, owned.CoverUrl, true)
            : _tracks.GetValueOrDefault(id);

    public ArtistGlimpse? Artist(Guid id)
    {
        var theirs = library.Albums?.Where(a => a.Album.ArtistId == id && !a.Album.Local).ToList() ?? [];
        var owned = theirs.Select(a => a.Album).OrderBy(a => a.FirstReleaseDate ?? "9999").ThenBy(a => a.Title).ToList();
        // Every artist's picture is served at the same place, a 404 when there's none.
        var image = $"api/v1/artwork/artist/{id}";
        if (library.Artists?.FirstOrDefault(a => a.Id == id) is { } known)
            return new ArtistGlimpse(known.Name, known.ImageUrl ?? image, owned);
        if (_artists.TryGetValue(id, out var seen))
            return new ArtistGlimpse(seen.Name, seen.ImageUrl ?? image, owned);
        return theirs.Count > 0 ? new ArtistGlimpse(theirs[0].ArtistName, image, owned) : null;
    }

    // Only so many are kept.
    private static void Put<T>(Dictionary<Guid, T> seen, Guid id, T value)
    {
        seen.Remove(id);
        seen[id] = value;
        if (seen.Count > Most)
            seen.Remove(seen.Keys.First());
    }
}
