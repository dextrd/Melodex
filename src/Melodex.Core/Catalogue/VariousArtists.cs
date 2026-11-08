using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;

namespace Melodex.Core.Catalogue;

/// <summary>
/// "Various Artists", whom compilations are credited to. Its discography is every compilation ever released, hundreds of
/// thousands of albums, so it's never fetched: only the compilations you have are known, and only they count as its
/// albums in the library.
/// </summary>
public static class VariousArtists
{
    public static readonly ProviderId MusicBrainz = new(Providers.MusicBrainz, "89ad4ac3-39f7-470e-963a-56509c546377");

    private const string Name = "Various Artists";

    /// <summary>The artists that are Various Artists: MusicBrainz's, and any other catalogue's by that name.</summary>
    public static async Task<HashSet<Guid>> IdsAsync(MelodexDbContext db, CancellationToken ct)
    {
        var ids = await db.Crosswalk.Where(m => m.EntityKind == EntityKind.Artist && m.Provider == MusicBrainz.Provider && m.Value == MusicBrainz.Value)
            .Select(m => m.EntityId).ToHashSetAsync(ct);
        ids.UnionWith(await db.Artists.Where(a => a.Name == Name).Select(a => a.Id).ToListAsync(ct));
        return ids;
    }

    public static async Task<bool> IsAsync(MelodexDbContext db, Guid artistId, CancellationToken ct) =>
        (await IdsAsync(db, ct)).Contains(artistId);
}
