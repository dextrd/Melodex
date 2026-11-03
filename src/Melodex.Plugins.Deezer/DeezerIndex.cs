using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Index;

namespace Melodex.Plugins.Deezer;

/// <summary>Entry point for the Deezer catalogue plugin.</summary>
public sealed class DeezerIndexFactory : IPluginFactory
{
    public const string PluginId = "melodex.index.deezer";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Deezer",
        Version = "0.1.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Catalogue data from Deezer's public catalogue. No account needed. Strong on music that's only on streaming services; after MusicBrainz, it fills in what MusicBrainz doesn't have.",
        Homepage = "https://www.deezer.com",
        Capabilities = [IndexCapabilities.IsrcLookup, IndexCapabilities.UpcLookup],
        Provider = Providers.Deezer,
        Access = new PluginAccess { Network = ["api.deezer.com"] },
        // Deezer allows 50 requests every 5 seconds.
        MaxRequestsPerSecond = 8,
    };

    public IPlugin Create(PluginContext context) => new DeezerIndex(context);
}

/// <summary>
/// Deezer as a catalogue. Each Deezer album is one album with one edition, under the same ID; record types map to
/// album, EP and single, with compilations as albums of the compilation type.
/// </summary>
public sealed class DeezerIndex(PluginContext context) : IIndexPlugin, IIsrcLookup, IUpcLookup
{
    private static readonly Uri Api = new("https://api.deezer.com/");

    // Deezer's error codes: over the request quota, and nothing with that ID.
    private const int QuotaExceeded = 4;
    private const int NoData = 800;

    private readonly HttpClient _http = context.HttpClient;

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetAsync("search/artist?limit=1&q=Portishead", cancellationToken);
            return HealthReport.Healthy();
        }
        catch (Exception e) when (e is HttpRequestException or IndexUnavailableException)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach Deezer: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "Deezer didn't answer in time.");
        }
    }

    public async Task<IndexSearchResults> SearchAsync(IndexSearchQuery query, CancellationToken cancellationToken)
    {
        bool Wants(SearchKind kind) => query.Kinds is null || query.Kinds.Count == 0 || query.Kinds.Contains(kind);
        var q = $"limit={query.Limit}&q={Uri.EscapeDataString(query.Text)}";
        var artists = Wants(SearchKind.Artist) ? GetAsync($"search/artist?{q}", cancellationToken) : null;
        var albums = Wants(SearchKind.Album) ? GetAsync($"search/album?{q}", cancellationToken) : null;
        var tracks = Wants(SearchKind.Track) ? GetAsync($"search/track?{q}", cancellationToken) : null;
        return new IndexSearchResults(
            artists is null ? [] : Data(await artists).Select(MapArtist).ToList(),
            albums is null ? [] : Data(await albums).Select(a => MapAlbum(a, null)).ToList(),
            tracks is null ? [] : Data(await tracks).Select(t => MapTrack(t, null)).ToList());
    }

    public async Task<IndexArtist?> GetArtistAsync(string artistId, CancellationToken cancellationToken) =>
        IsId(artistId) && await GetOrNullAsync($"artist/{artistId}", cancellationToken) is { } artist ? MapArtist(artist) : null;

    public async Task<IReadOnlyList<IndexAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken)
    {
        if (!IsId(artistId) || await GetOrNullAsync($"artist/{artistId}", cancellationToken) is not { } artist)
            return [];
        var owner = new ArtistRef(Id(artist), Name(artist));
        var albums = new List<IndexAlbum>();
        await foreach (var album in PagesAsync($"artist/{artistId}/albums?limit=100", cancellationToken))
            albums.Add(MapAlbum(album, owner));
        return albums;
    }

    public async Task<IndexAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken) =>
        IsId(albumId) && await GetOrNullAsync($"album/{albumId}", cancellationToken) is { } album
            ? await DetailAsync(album, cancellationToken)
            : null;

    public async Task<IReadOnlyList<IndexTrack>> LookupIsrcAsync(string isrc, CancellationToken cancellationToken) =>
        await GetOrNullAsync($"track/isrc:{Uri.EscapeDataString(isrc)}", cancellationToken) is { } track && track.TryGetProperty("id", out _)
            ? [MapTrack(track, null)]
            : [];

    public async Task<IReadOnlyList<IndexAlbumDetail>> LookupUpcAsync(string upc, CancellationToken cancellationToken) =>
        await GetOrNullAsync($"album/upc:{Uri.EscapeDataString(upc)}", cancellationToken) is { } album && album.TryGetProperty("id", out _)
            ? [await DetailAsync(album, cancellationToken)]
            : [];

    /// <summary>The album's one edition, with every track: the album answer lists only some, so they're read from its track list.</summary>
    private async Task<IndexAlbumDetail> DetailAsync(JsonElement album, CancellationToken ct)
    {
        var summary = MapAlbum(album, null);
        var tracks = new List<JsonElement>();
        await foreach (var track in PagesAsync($"album/{Id(album).Value}/tracks?limit=100", ct))
            tracks.Add(track);
        var media = tracks
            .GroupBy(t => Int(t, "disk_number") ?? 1)
            .OrderBy(g => g.Key)
            .Select(g => new IndexMedium(g.Key, "Digital Media", g
                .OrderBy(t => Int(t, "track_position") ?? 0)
                .Select((t, i) =>
                {
                    var position = Int(t, "track_position") ?? i + 1;
                    var track = MapTrack(t, summary);
                    return new IndexTrackListing(position, position.ToString(CultureInfo.InvariantCulture), track.Title, track.Duration, track);
                })
                .ToList()))
            .ToList();
        var edition = new IndexEdition(Id(album), summary.Title, summary.FirstReleaseDate, null, "Official", null,
            String(album, "upc") is { Length: > 0 } upc ? upc : null, media);
        return new IndexAlbumDetail(summary, [edition]);
    }

    // ---- Mapping ----

    public static IndexArtist MapArtist(JsonElement artist) => new(Id(artist), Name(artist));

    /// <param name="owner">The artist whose discography this is, when the answer doesn't name one.</param>
    public static IndexAlbum MapAlbum(JsonElement album, ArtistRef? owner)
    {
        var artist = album.TryGetProperty("artist", out var a) ? new ArtistRef(Id(a), Name(a)) : owner ?? new ArtistRef(new ProviderId(Providers.Deezer, ""), "");
        var main = album.TryGetProperty("contributors", out var contributors) && contributors.ValueKind == JsonValueKind.Array
            ? contributors.EnumerateArray().Where(c => String(c, "role") is null or "Main").Select(Name).Where(n => n.Length > 0).ToList()
            : [];
        var (type, secondary) = String(album, "record_type") switch
        {
            "single" => (AlbumType.Single, Array.Empty<AlbumSecondaryType>()),
            "ep" => (AlbumType.Ep, []),
            "compile" => (AlbumType.Album, [AlbumSecondaryType.Compilation]),
            _ => (AlbumType.Album, []),
        };
        return new IndexAlbum(Id(album), String(album, "title") ?? "", artist, main.Count > 1 ? string.Join(" & ", main) : artist.Name,
            type, secondary, Date(String(album, "release_date")), HasOfficialRelease: true);
    }

    /// <param name="album">The album the track was listed on, when it's read from an album.</param>
    public static IndexTrack MapTrack(JsonElement track, IndexAlbum? album)
    {
        var artist = track.TryGetProperty("artist", out var a) ? new ArtistRef(Id(a), Name(a)) : album?.Artist ?? new ArtistRef(new ProviderId(Providers.Deezer, ""), "");
        var main = track.TryGetProperty("contributors", out var contributors) && contributors.ValueKind == JsonValueKind.Array
            ? contributors.EnumerateArray().Where(c => String(c, "role") is null or "Main").Select(Name).Where(n => n.Length > 0).ToList()
            : [];
        var seconds = Int(track, "duration");
        var from = album ?? (track.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object && al.TryGetProperty("id", out _)
            ? MapAlbum(al, artist)
            : null);
        return new IndexTrack(Id(track), String(track, "title") ?? "", artist, main.Count > 1 ? string.Join(" & ", main) : artist.Name,
            seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null,
            String(track, "isrc") is { Length: > 0 } isrc ? [isrc] : [],
            Album: album is null ? from : null);
    }

    private static ProviderId Id(JsonElement e) =>
        new(Providers.Deezer, e.TryGetProperty("id", out var id) ? id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(CultureInfo.InvariantCulture) : id.GetString() ?? "" : "");

    private static string Name(JsonElement e) => String(e, "name") ?? "";

    private static string? String(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;

    // Deezer writes an unknown date as 0000-00-00.
    private static string? Date(string? date) => date is null or "" || date.StartsWith("0000", StringComparison.Ordinal) ? null : date;

    private static bool IsId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    private static IEnumerable<JsonElement> Data(JsonElement page) =>
        page.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    // ---- Requests ----

    private async IAsyncEnumerable<JsonElement> PagesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = path;
        for (var pages = 0; next is not null && pages < 50; pages++)
        {
            var page = await GetAsync(next, ct);
            foreach (var item in Data(page))
                yield return item;
            next = String(page, "next") is { } url && url.StartsWith(Api.OriginalString, StringComparison.Ordinal) ? url[Api.OriginalString.Length..] : null;
        }
    }

    private async Task<JsonElement?> GetOrNullAsync(string path, CancellationToken ct)
    {
        try
        {
            return await GetAsync(path, ct);
        }
        catch (DeezerNotFoundException)
        {
            return null;
        }
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await _http.GetFromJsonAsync<JsonElement>(new Uri(Api, path), ct);
        // Errors, including going over the request quota, come back as 200 with an error object.
        if (body.TryGetProperty("error", out var error))
        {
            var message = String(error, "message") ?? "unknown error";
            throw Int(error, "code") switch
            {
                QuotaExceeded => new IndexUnavailableException($"Deezer is busy: {message}"),
                NoData => new DeezerNotFoundException(),
                _ => new HttpRequestException($"Deezer refused the request: {message}"),
            };
        }
        return body;
    }

    private sealed class DeezerNotFoundException : Exception;
}
