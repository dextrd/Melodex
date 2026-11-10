using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

/// <summary>
/// Artists', albums' and tracks' own quality settings, and which applies: the narrowest that has one (track, then
/// album, then artist), or the main profile's cutoff.
/// </summary>
public sealed class QualitySettings(MelodexDbContext db, SettingsService settings)
{
    public const string Never = "never";

    /// <summary>The setting that applies to a track, as on the album and by the artist given (or its own artist).</summary>
    public async Task<QualitySetting> ForTrackAsync(Guid trackId, Guid? albumId, CancellationToken ct)
    {
        var track = await db.Tracks.Where(t => t.Id == trackId).Select(t => new { t.Title, t.QualityOverride, t.ArtistId }).FirstOrDefaultAsync(ct);
        if (track is null)
            return await ResolveAsync([], ct);
        albumId ??= await db.LibraryFiles.Where(f => f.TrackId == trackId && f.EditionId != null).Select(f => (Guid?)f.Edition!.AlbumId).FirstOrDefaultAsync(ct);
        var album = albumId is { } a ? await db.Albums.Where(x => x.Id == a).Select(x => new { x.Title, x.QualityOverride, x.ArtistId }).FirstOrDefaultAsync(ct) : null;
        var artistId = album?.ArtistId ?? track.ArtistId;
        var artist = artistId is { } id ? await db.Artists.Where(x => x.Id == id).Select(x => new { x.Name, x.QualityOverride }).FirstOrDefaultAsync(ct) : null;
        return await ResolveAsync(
        [
            ("this song", track.QualityOverride),
            (album is null ? "" : $"the album {album.Title}", album?.QualityOverride),
            (artist?.Name ?? "", artist?.QualityOverride),
        ], ct);
    }

    public async Task<QualitySetting> ForAlbumAsync(Guid albumId, CancellationToken ct)
    {
        var album = await db.Albums.Where(x => x.Id == albumId).Select(x => new { x.QualityOverride, x.Artist.Name, Artist = x.Artist.QualityOverride }).FirstOrDefaultAsync(ct);
        return await ResolveAsync(album is null ? [] : [("this album", album.QualityOverride), (album.Name, album.Artist)], ct);
    }

    public async Task<QualitySetting> ForArtistAsync(Guid artistId, CancellationToken ct)
    {
        var own = await db.Artists.Where(x => x.Id == artistId).Select(x => x.QualityOverride).FirstOrDefaultAsync(ct);
        return await ResolveAsync([("this artist", own)], ct);
    }

    /// <summary>Sets an artist's, album's or track's own setting. False if there's no such thing, or the cutoff isn't a step.</summary>
    public async Task<bool> SaveAsync(EntityKind kind, Guid id, QualityOverride value, CancellationToken ct)
    {
        string? stored = value.Kind switch
        {
            QualityOverrideKind.NeverUpgrade => Never,
            QualityOverrideKind.Cutoff when QualityStep.Parse(value.Cutoff) is { } step => step.Key,
            QualityOverrideKind.Cutoff => throw new ArgumentException("That cutoff isn't a quality step.", nameof(value)),
            _ => null,
        };
        var rows = kind switch
        {
            EntityKind.Artist => await db.Artists.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.QualityOverride, stored), ct),
            EntityKind.Album => await db.Albums.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.QualityOverride, stored), ct),
            EntityKind.Track => await db.Tracks.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.QualityOverride, stored), ct),
            _ => 0,
        };
        return rows > 0;
    }

    // The first of the settings, narrowest first, that isn't inherited; else the main profile's.
    private async Task<QualitySetting> ResolveAsync(IReadOnlyList<(string From, string? Override)> chain, CancellationToken ct)
    {
        var (ownKind, ownCutoff) = chain.Count > 0 ? Read(chain[0].Override) : (QualityOverrideKind.Inherit, null);
        foreach (var (from, value) in chain)
        {
            var (kind, cutoff) = Read(value);
            if (kind != QualityOverrideKind.Inherit)
                return new QualitySetting(ownKind, ownCutoff, kind, cutoff, from);
        }
        var profile = await settings.GetQualityAsync(ct);
        return new QualitySetting(ownKind, ownCutoff, QualityOverrideKind.Cutoff, profile.Cutoff, "the main profile");
    }

    private static (QualityOverrideKind Kind, string? Cutoff) Read(string? value) => value switch
    {
        null => (QualityOverrideKind.Inherit, null),
        Never => (QualityOverrideKind.NeverUpgrade, null),
        _ when QualityStep.Parse(value) is not null => (QualityOverrideKind.Cutoff, value),
        _ => (QualityOverrideKind.Inherit, null),
    };
}
