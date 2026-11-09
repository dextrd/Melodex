using System.Net;
using MetaBrainz.Common;
using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Index;

namespace Melodex.Plugins.MusicBrainz;

/// <summary>An index plugin instance talking to one MusicBrainz server.</summary>
public sealed class MusicBrainzIndex : IIndexPlugin, IIsrcLookup, IRecordingAlbums
{
    private const int PageSize = 100;

    // A stable, always-present artist ("Various Artists") for health checks.
    private static readonly Guid HealthCheckArtist = new("89ad4ac3-39f7-470e-963a-56509c546377");

    private readonly Query _query;

    public MusicBrainzIndex(PluginContext context)
    {
        _query = new Query(context.HttpClient, takeOwnership: false);
        if (context.Settings.TryGetProperty("server", out var server) && Uri.TryCreate(server.GetString(), UriKind.Absolute, out var uri))
        {
            _query.Server = uri.Host;
            _query.Port = uri.Port;
            _query.UrlScheme = uri.Scheme;
        }
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _query.LookupArtistAsync(HealthCheckArtist, cancellationToken: cancellationToken);
            return HealthReport.Healthy();
        }
        catch (Exception e) when (e is HttpRequestException or HttpError or TaskCanceledException)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach {_query.UrlScheme}://{_query.Server}: {e.Message}");
        }
    }

    public Task<IndexSearchResults> SearchAsync(IndexSearchQuery query, CancellationToken cancellationToken) =>
        Guarded(() => SearchAsyncCore(query, cancellationToken));

    private async Task<IndexSearchResults> SearchAsyncCore(IndexSearchQuery query, CancellationToken cancellationToken)
    {
        bool Wants(SearchKind kind) => query.Kinds is null || query.Kinds.Count == 0 || query.Kinds.Contains(kind);
        var text = EscapeLucene(query.Text);

        // All at once: the rate limit still spaces them out, but their answers come back together rather than one after another.
        var artists = Wants(SearchKind.Artist)
            ? _query.FindArtistsAsync(text, query.Limit, simple: true, cancellationToken: cancellationToken)
            : null;
        var albums = Wants(SearchKind.Album)
            ? _query.FindReleaseGroupsAsync(text, query.Limit, simple: true, cancellationToken: cancellationToken)
            : null;
        var tracks = Wants(SearchKind.Track)
            ? _query.FindRecordingsAsync(text, query.Limit, simple: true, cancellationToken: cancellationToken)
            : null;
        return new IndexSearchResults(
            artists is null ? [] : (await artists).Results.Select(r => MapArtist(r.Item)).ToList(),
            albums is null ? [] : (await albums).Results.Select(r => MapAlbum(r.Item, null)).ToList(),
            tracks is null ? [] : (await tracks).Results.Select(r => MapTrack(r.Item) with { Album = BestKnownAlbum(r.Item) }).ToList());
    }

    public Task<IndexArtist?> GetArtistAsync(string artistId, CancellationToken cancellationToken) =>
        Guarded(() => GetArtistAsyncCore(artistId, cancellationToken));

    private async Task<IndexArtist?> GetArtistAsyncCore(string artistId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(artistId, out var id))
            return null;
        try
        {
            return MapArtist(await _query.LookupArtistAsync(id, cancellationToken: cancellationToken));
        }
        catch (HttpError e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<IndexAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken) =>
        Guarded(() => GetArtistAlbumsAsyncCore(artistId, cancellationToken));

    private async Task<IReadOnlyList<IndexAlbum>> GetArtistAlbumsAsyncCore(string artistId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(artistId, out var id))
            return [];

        var albums = new List<IndexAlbum>();
        await foreach (var group in _query.BrowseAllArtistReleaseGroups(id, PageSize, inc: Include.ArtistCredits).WithCancellation(cancellationToken))
            // Release groups carry no status; whether one is bootleg-only shows once its editions are fetched.
            albums.Add(MapAlbum(group, null));
        return albums;
    }

    public Task<IndexAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken) =>
        Guarded(() => GetAlbumAsyncCore(albumId, cancellationToken));

    private async Task<IndexAlbumDetail?> GetAlbumAsyncCore(string albumId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(albumId, out var id))
            return null;

        // One browse returns every edition with its track listing, and the release group with each release, so an
        // album costs a single request. Only a release group with no releases needs a separate lookup.
        IReleaseGroup? group = null;
        var editions = new List<IndexEdition>();
        await foreach (var release in _query.BrowseAllReleaseGroupReleases(id, PageSize,
                           inc: Include.Recordings | Include.Isrcs | Include.ArtistCredits | Include.Media | Include.ReleaseGroups)
                           .WithCancellation(cancellationToken))
        {
            group ??= release.ReleaseGroup;
            editions.Add(MapEdition(release));
        }

        if (group?.ArtistCredit is null)
        {
            try
            {
                group = await _query.LookupReleaseGroupAsync(id, Include.ArtistCredits, cancellationToken: cancellationToken);
            }
            catch (HttpError e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }
        }

        var hasOfficial = editions.Count == 0 ? (bool?)null : editions.Any(e => string.Equals(e.Status, "Official", StringComparison.OrdinalIgnoreCase));
        return new IndexAlbumDetail(MapAlbum(group, hasOfficial), editions);
    }

    public Task<IReadOnlyList<IndexTrack>> LookupIsrcAsync(string isrc, CancellationToken cancellationToken) =>
        Guarded(() => LookupIsrcAsyncCore(isrc, cancellationToken));

    private async Task<IReadOnlyList<IndexTrack>> LookupIsrcAsyncCore(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _query.LookupIsrcAsync(isrc, Include.ArtistCredits | Include.Isrcs, cancellationToken);
            return result.Recordings?.Select(MapTrack).ToList() ?? [];
        }
        catch (HttpError e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }
    }

    public Task<IReadOnlyList<IndexAlbum>> GetRecordingAlbumsAsync(string recordingId, CancellationToken cancellationToken) =>
        Guarded(() => GetRecordingAlbumsAsyncCore(recordingId, cancellationToken));

    private async Task<IReadOnlyList<IndexAlbum>> GetRecordingAlbumsAsyncCore(string recordingId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(recordingId, out var id))
            return [];
        try
        {
            var recording = await _query.LookupRecordingAsync(id, Include.Releases | Include.ReleaseGroups | Include.ArtistCredits, cancellationToken: cancellationToken);
            return (recording.Releases ?? [])
                .Where(r => r.ReleaseGroup is not null)
                .GroupBy(r => r.ReleaseGroup!.Id)
                .Select(g => MapAlbum(g.First().ReleaseGroup!, g.Any(r => string.Equals(r.Status, "Official", StringComparison.OrdinalIgnoreCase)) ? true : null) with
                {
                    // A release-group from a recording comes without its artist; its releases' or the recording's is it.
                    Artist = PrimaryArtist(g.First().ReleaseGroup!.ArtistCredit ?? g.First().ArtistCredit ?? recording.ArtistCredit),
                    ArtistCredit = Credit(g.First().ReleaseGroup!.ArtistCredit ?? g.First().ArtistCredit ?? recording.ArtistCredit),
                })
                .ToList();
        }
        catch (HttpError e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }
    }

    // MusicBrainz answers 503 when it's overloaded or rate-limiting. The core's HTTP client has already retried by the
    // time it gets here, so this tells the core to come back later rather than fail.
    private static async Task<T> Guarded<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpError e) when ((int)e.Status >= 500 || e.Status == HttpStatusCode.TooManyRequests)
        {
            throw new IndexUnavailableException($"MusicBrainz is busy or unavailable ({(int)e.Status}): {e.Message.Trim()}", e);
        }
    }

    // The contract takes free text, so searches use MusicBrainz's simple mode, which looks across title, artist and
    // release instead of the title alone. Lucene special characters are escaped all the same.
    private static string EscapeLucene(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"([+\-!(){}\[\]^""~*?:\\/]|&&|\|\|)", @"\$1");

    private static ProviderId Mbid(Guid id) => new(Providers.MusicBrainz, id.ToString());

    private static IndexArtist MapArtist(IArtist artist) => new(
        Mbid(artist.Id),
        artist.Name ?? "",
        artist.SortName,
        string.IsNullOrEmpty(artist.Disambiguation) ? null : artist.Disambiguation,
        artist.Country);

    private static ArtistRef PrimaryArtist(IReadOnlyList<INameCredit>? credits) =>
        credits is [{ Artist: { } artist } first, ..]
            ? new ArtistRef(Mbid(artist.Id), first.Name ?? artist.Name ?? "")
            : new ArtistRef(new ProviderId(Providers.MusicBrainz, ""), "");

    private static string Credit(IReadOnlyList<INameCredit>? credits) =>
        credits is null ? "" : string.Concat(credits.Select(c => (c.Name ?? c.Artist?.Name) + c.JoinPhrase));

    private static string? Date(PartialDate? date) => date is null || date.IsEmpty ? null : date.ToString();

    private static IndexAlbum MapAlbum(IReleaseGroup group, bool? hasOfficialRelease) => new(
        Mbid(group.Id),
        group.Title ?? "",
        PrimaryArtist(group.ArtistCredit),
        Credit(group.ArtistCredit),
        MapType(group.PrimaryType),
        group.SecondaryTypes?.Select(MapSecondaryType).OfType<AlbumSecondaryType>().ToList() ?? [],
        Date(group.FirstReleaseDate),
        HasOfficialRelease: hasOfficialRelease);

    private static IndexEdition MapEdition(IRelease release) => new(
        Mbid(release.Id),
        release.Title ?? "",
        Date(release.Date),
        release.Country,
        release.Status,
        string.IsNullOrEmpty(release.Disambiguation) ? null : release.Disambiguation,
        string.IsNullOrEmpty(release.Barcode) ? null : release.Barcode,
        release.Media?.Select(medium => new IndexMedium(
            medium.Position,
            medium.Format,
            medium.Tracks?.Select(track => new IndexTrackListing(
                track.Position ?? 0,
                track.Number ?? "",
                track.Title ?? track.Recording?.Title ?? "",
                track.Length ?? track.Recording?.Length,
                track.Recording is { } recording
                    ? MapTrack(recording)
                    : new IndexTrack(new ProviderId(Providers.MusicBrainz, ""), track.Title ?? "", PrimaryArtist(track.ArtistCredit), Credit(track.ArtistCredit), track.Length, [])))
                .ToList() ?? [])).ToList() ?? []);

    private static IndexTrack MapTrack(IRecording recording) => new(
        Mbid(recording.Id),
        recording.Title ?? "",
        PrimaryArtist(recording.ArtistCredit),
        Credit(recording.ArtistCredit),
        recording.Length,
        recording.Isrcs?.ToList() ?? []);

    /// <summary>
    /// Of the releases a search lists for a recording, the album it's best known from: an official release before
    /// others, an album before EPs and singles, a studio album before compilations and live albums, then the earliest.
    /// </summary>
    private static IndexAlbum? BestKnownAlbum(IRecording recording)
    {
        var releases = recording.Releases?.Where(r => r.ReleaseGroup is not null).ToList() ?? [];
        var best = releases
            .OrderBy(r => string.Equals(r.Status, "Official", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => MapType(r.ReleaseGroup!.PrimaryType) switch { AlbumType.Album => 0, AlbumType.Ep => 1, AlbumType.Single => 2, _ => 3 })
            .ThenBy(r => r.ReleaseGroup!.SecondaryTypes?.Count ?? 0)
            .ThenBy(r => Date(r.Date) ?? "9999", StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
            return null;

        var group = best.ReleaseGroup!;
        var sameGroup = releases.Where(r => r.ReleaseGroup!.Id == group.Id).ToList();
        // Search results leave out a release's artist when it's the recording's.
        var credit = best.ArtistCredit ?? group.ArtistCredit ?? recording.ArtistCredit;
        return MapAlbum(group, sameGroup.Any(r => string.Equals(r.Status, "Official", StringComparison.OrdinalIgnoreCase)) ? true : null) with
        {
            Artist = PrimaryArtist(credit),
            ArtistCredit = Credit(credit),
            FirstReleaseDate = Date(group.FirstReleaseDate) ?? sameGroup.Select(r => Date(r.Date)).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault(),
        };
    }

    private static AlbumType MapType(string? type) => type?.ToLowerInvariant() switch
    {
        "album" => AlbumType.Album,
        "ep" => AlbumType.Ep,
        "single" => AlbumType.Single,
        "broadcast" => AlbumType.Broadcast,
        _ => AlbumType.Other,
    };

    private static AlbumSecondaryType? MapSecondaryType(string type) => type.ToLowerInvariant() switch
    {
        "compilation" => AlbumSecondaryType.Compilation,
        "soundtrack" => AlbumSecondaryType.Soundtrack,
        "spokenword" => AlbumSecondaryType.Spokenword,
        "interview" => AlbumSecondaryType.Interview,
        "audiobook" => AlbumSecondaryType.Audiobook,
        "audio drama" => AlbumSecondaryType.AudioDrama,
        "live" => AlbumSecondaryType.Live,
        "remix" => AlbumSecondaryType.Remix,
        "dj-mix" => AlbumSecondaryType.DjMix,
        "mixtape/street" => AlbumSecondaryType.Mixtape,
        "demo" => AlbumSecondaryType.Demo,
        "field recording" => AlbumSecondaryType.FieldRecording,
        _ => null,
    };
}
