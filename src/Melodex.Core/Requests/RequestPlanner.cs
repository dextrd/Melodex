using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Requests;

/// <summary>
/// Turns a request into work. An artist request becomes one album request per album the library counts as missing or
/// partly owned. An album request settles on an edition, then either stays whole (when every track of it is wanted
/// and a retrieval plugin can fetch albums) or becomes one track request per wanted track. A track request settles on
/// an edition too, so its file lands with the rest of that album.
/// </summary>
public sealed class RequestPlanner(MelodexDbContext db, CatalogueService catalogue, LibraryView view, SettingsService settings)
{
    /// <summary>The outcome of planning one request.</summary>
    /// <param name="Fetch">The request itself fetches files: a track, or a whole album.</param>
    /// <param name="Children">Requests to create under it.</param>
    /// <param name="Done">Nothing to fetch; why.</param>
    public sealed record Plan(bool Fetch, IReadOnlyList<Request> Children, string? Done);

    public async Task<Plan> PlanAsync(Request request, bool albumFetchAvailable, CancellationToken ct) => request.TargetKind switch
    {
        EntityKind.Artist => await PlanArtistAsync(request, ct),
        EntityKind.Album => await PlanAlbumAsync(request, albumFetchAvailable, ct),
        EntityKind.Track => await PlanTrackAsync(request, ct),
        _ => throw new InvalidOperationException($"{request.TargetKind} can't be requested."),
    };

    private async Task<Plan> PlanArtistAsync(Request request, CancellationToken ct)
    {
        var artist = await db.Artists.FindAsync([request.TargetId], ct) ?? throw new InvalidOperationException("The artist isn't in the catalogue.");
        await catalogue.RefreshDiscographyAsync(artist, false, ct);
        var albums = (await view.GetArtistAlbumsAsync(artist.Id, ct))!;

        // What the "only missing" view shows: missing albums the filters list, and owned ones with tracks still missing.
        var wanted = albums.Where(a => !a.Ignored && (a.Status == AlbumStatus.Missing && !a.HiddenByDefault || a.Status == AlbumStatus.Partial)).ToList();
        if (wanted.Count == 0)
            return new Plan(false, [], $"Nothing missing for {artist.Name}");
        return new Plan(false, wanted.Select(a => Child(request, EntityKind.Album, a.Id, null, $"{a.Title} · {a.ArtistCredit}")).ToList(), null);
    }

    private async Task<Plan> PlanAlbumAsync(Request request, bool albumFetchAvailable, CancellationToken ct)
    {
        var album = await db.Albums.FindAsync([request.TargetId], ct) ?? throw new InvalidOperationException("The album isn't in the catalogue.");
        await catalogue.RefreshEditionsAsync(album, false, ct);

        var detail = (await view.GetAlbumAsync(album.Id, null, ct))!;
        request.EditionId ??= detail.OwnedEditionId ?? await ChooseEditionAsync(album.Id, ct);
        if (request.EditionId is null)
            return new Plan(false, [], "MusicBrainz lists no editions of this album, so there's nothing to fetch");
        if (request.EditionId != detail.ShownEditionId)
            detail = (await view.GetAlbumAsync(album.Id, request.EditionId, ct))!;

        var wanted = detail.Tracks.Where(t => t.Status == TrackStatus.Missing).ToList();
        if (wanted.Count == 0)
            return new Plan(false, [], "You have every track of this album");

        // Whole when nothing of it is owned, so a plugin that fetches albums gets one consistent source.
        if (albumFetchAvailable && detail.OwnedEditionId is null && wanted.Count == detail.Tracks.Count)
            return new Plan(true, [], null);
        return new Plan(false, wanted.Select(t => Child(request, EntityKind.Track, t.TrackId, request.EditionId, $"{t.Title} · {t.ArtistCredit}")).ToList(), null);
    }

    private async Task<Plan> PlanTrackAsync(Request request, CancellationToken ct)
    {
        request.EditionId ??= await ChooseEditionForTrackAsync(request.TargetId, ct);
        if (request.EditionId is null)
            return new Plan(false, [], "This track isn't on any edition in the catalogue, so it has nowhere to go");

        var albumId = await db.Editions.Where(e => e.Id == request.EditionId).Select(e => e.AlbumId).FirstAsync(ct);
        var detail = (await view.GetAlbumAsync(albumId, request.EditionId, ct))!;
        var track = detail.Tracks.FirstOrDefault(t => t.TrackId == request.TargetId);
        // An upgrade is for a track that's owned, by its nature.
        if (request.UpgradesFileId is not null)
            return new Plan(true, [], null);
        return track?.Status switch
        {
            TrackStatus.Owned or TrackStatus.OwnedElsewhere => new Plan(false, [], "You already have this track"),
            TrackStatus.Ignored => new Plan(false, [], "This track is ignored; unignore it to fetch it"),
            _ => new Plan(true, [], null),
        };
    }

    /// <summary>
    /// The edition to fetch for an album nobody owns: an official one with the usual track listing (the count most
    /// editions share, so not a bonus-laden deluxe), the earliest of those, preferring plain ones over pressings
    /// MusicBrainz has to tell apart with a comment.
    /// </summary>
    public async Task<Guid?> ChooseEditionAsync(Guid albumId, CancellationToken ct)
    {
        var editions = await db.Editions.Where(e => e.AlbumId == albumId && e.TrackCount > 0).ToListAsync(ct);
        var official = editions.Where(e => string.Equals(e.Status, "Official", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = official.Count > 0 ? official : editions;
        var usual = pool.GroupBy(e => e.TrackCount).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        return usual?.OrderBy(e => e.Disambiguation is { Length: > 0 }).ThenBy(e => DateKey(e.ReleaseDate)).ThenBy(e => e.Id).First().Id;
    }

    /// <summary>
    /// Sorts release dates earliest first, with a partial date at the end of its period: "2005" says less than
    /// "2005-03-14", and is usually a later pressing whose day nobody recorded.
    /// </summary>
    public static string DateKey(string? date) => date switch
    {
        null or "" => "9999",
        { Length: 4 } => date + "-99-99",
        { Length: 7 } => date + "-99",
        _ => date,
    };

    /// <summary>
    /// The edition a track request files under: one you already own part of, so it joins those files; otherwise one
    /// from the artist's own albums before EPs and singles (or singles first, when the settings keep songs with the
    /// smallest release), studio releases before compilations and live ones, official first, earliest first.
    /// </summary>
    public async Task<Guid?> ChooseEditionForTrackAsync(Guid trackId, CancellationToken ct)
    {
        var appearances = await db.EditionTracks.Where(et => et.TrackId == trackId)
            .Select(et => new { et.EditionId, et.Edition.Status, et.Edition.ReleaseDate, et.Edition.Disambiguation, et.Edition.Album.Type, Secondary = et.Edition.Album.SecondaryTypes })
            .ToListAsync(ct);
        if (appearances.Count == 0)
            return null;
        var smallestFirst = (await settings.GetLibraryAsync(ct)).DuplicateTracks == DuplicateTracks.KeepInSingle;
        var editionIds = appearances.Select(a => a.EditionId).ToList();
        var owned = await db.LibraryFiles.Where(f => f.EditionId != null && editionIds.Contains(f.EditionId.Value))
            .Select(f => f.EditionId!.Value).Distinct().ToListAsync(ct);
        return appearances
            .OrderByDescending(a => owned.Contains(a.EditionId))
            .ThenBy(a => a.Secondary.Count > 0)
            .ThenBy(a => a.Type switch { AlbumType.Album => smallestFirst ? 2 : 0, AlbumType.Ep => 1, AlbumType.Single => smallestFirst ? 0 : 2, _ => 3 })
            .ThenByDescending(a => string.Equals(a.Status, "Official", StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Disambiguation is { Length: > 0 })
            .ThenBy(a => DateKey(a.ReleaseDate))
            .First().EditionId;
    }

    private static Request Child(Request parent, EntityKind kind, Guid targetId, Guid? editionId, string title) => new()
    {
        TargetKind = kind,
        TargetId = targetId,
        EditionId = editionId,
        Title = title,
        Mode = parent.Mode,
        Requester = parent.Requester,
        ParentRequestId = parent.Id,
        QueueOrder = parent.QueueOrder,
        Paused = parent.Paused,
    };
}
