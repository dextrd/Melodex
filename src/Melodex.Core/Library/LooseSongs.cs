using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Melodex.Core.Library;

/// <summary>
/// Where a song matched on its own shows. A file matched as a song, rather than as part of an album, has no release of
/// its own, yet it's on albums: the one its album tag names when the catalogue has it, or else its first official album.
/// Nothing's written; formatting files them properly.
/// </summary>
public static class LooseSongs
{
    public sealed record Place(Guid AlbumId, string AlbumTitle, Guid EditionId);

    /// <param name="songs">Each song, with its file's album tag.</param>
    public static async Task<Dictionary<Guid, Place>> PlacesAsync(MelodexDbContext db, IReadOnlyDictionary<Guid, string?> songs, CancellationToken ct)
    {
        if (songs.Count == 0)
            return [];
        var ids = songs.Keys.ToList();
        var on = await db.EditionTracks.AsNoTracking().Where(et => ids.Contains(et.TrackId))
            .Select(et => new
            {
                et.TrackId, et.EditionId, et.Edition.AlbumId, Title = et.Edition.Album.Title, et.Edition.Album.Type, et.Edition.Album.SecondaryTypes,
                et.Edition.Status, Date = et.Edition.ReleaseDate ?? et.Edition.Album.FirstReleaseDate,
            })
            .ToListAsync(ct);
        return on.GroupBy(x => x.TrackId).ToDictionary(g => g.Key, g =>
        {
            var tag = songs[g.Key] is { Length: > 0 } album ? SongNames.TitleKey(album) : null;
            var best = g
                .OrderByDescending(x => tag is not null && SongNames.TitleKey(x.Title) == tag)
                .ThenByDescending(x => x.Type == AlbumType.Album && x.SecondaryTypes.Count == 0)
                .ThenByDescending(x => x.Status == "Official")
                .ThenBy(x => x.Date ?? "9999")
                .ThenBy(x => x.EditionId)
                .First();
            return new Place(best.AlbumId, best.Title, best.EditionId);
        });
    }
}
