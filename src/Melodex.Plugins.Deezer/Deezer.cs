using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;

namespace Melodex.Plugins.Deezer;

/// <summary>Entry point for the Deezer artwork plugin.</summary>
public sealed class DeezerFactory : IPluginFactory
{
    public const string PluginId = "melodex.artwork.deezer";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Deezer",
        Version = "0.2.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "Artist pictures and album covers from Deezer's public catalogue. No account needed. An artist is found by their Deezer ID, through Wikidata by their MusicBrainz ID, or by name when only one Deezer artist has it or one has their albums; covers are for albums Deezer's catalogue knows.",
        Homepage = "https://www.deezer.com",
        Capabilities = [ArtworkCapabilities.Artists, ArtworkCapabilities.Albums, ArtworkCapabilities.Editions],
        Access = new PluginAccess { Network = ["api.deezer.com", "cdn-images.dzcdn.net", "e-cdns-images.dzcdn.net", "www.wikidata.org"] },
        // Deezer allows 50 requests every 5 seconds.
        MaxRequestsPerSecond = 5,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "size": {
                  "type": "string",
                  "enum": ["medium", "big", "xl"],
                  "default": "big",
                  "title": "Image size",
                  "description": "Medium pictures and covers are 250 pixels wide, big 500 and xl 1000."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new Deezer(context);
}

/// <summary>
/// The picture of the subject's own Deezer artist, and the covers of albums by their Deezer IDs. The artist is the one
/// with their Deezer ID, or the one Wikidata names for their MusicBrainz ID; failing those, the Deezer artist with
/// exactly their name (ignoring case and accents) when there's only one, or the one with an album of the same title as
/// one of theirs. Otherwise there's no picture rather than someone else's. Deezer gives a blank placeholder for artists
/// and albums without one, which isn't offered.
/// </summary>
public sealed class Deezer(PluginContext context) : IArtworkPlugin
{
    private static readonly Uri Api = new("https://api.deezer.com/");
    private static readonly Uri Wikidata = new("https://www.wikidata.org/w/api.php");

    // Same-named artists whose albums are looked at, most followed first.
    private const int AlbumChecks = 5;

    private readonly HttpClient _http = context.HttpClient;
    private readonly string _size = context.Settings.TryGetProperty("size", out var size) && size.GetString() is "medium" or "xl" ? size.GetString()! : "big";

    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        if (query.Subject != ArtworkSubject.Artist)
            return await CoversAsync(query, cancellationToken);
        var url = await ByIdAsync(query, cancellationToken) ?? await ByNameAsync(query, cancellationToken);
        return url is null ? [] : [new ArtworkCandidate(url, "photo")];
    }

    private async Task<string?> ByIdAsync(ArtworkQuery query, CancellationToken ct)
    {
        var ids = query.Ids.Where(i => i.Provider == Providers.Deezer).Select(i => i.Value).ToList();
        foreach (var mbid in query.Ids.Where(i => i.Provider == Providers.MusicBrainz).Select(i => i.Value))
            if (await WikidataDeezerIdAsync(mbid, ct) is { } id)
                ids.Add(id);
        foreach (var id in ids.Distinct().Where(IsId))
            if (Picture(await GetAsync($"artist/{id}", ct, missing: true), _size) is { } url)
                return url;
        return null;
    }

    private async Task<string?> ByNameAsync(ArtworkQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Title))
            return null;
        var found = await GetAsync($"search/artist?limit=25&q={Uri.EscapeDataString(query.Title)}", ct);
        var named = Named(found, query.Title);
        if (named.Count == 1)
            return Picture(named[0], _size);
        var titles = (query.AlbumTitles ?? []).Select(Normalise).Where(t => t.Length > 0).ToHashSet();
        if (titles.Count == 0)
            return null;
        foreach (var artist in named.Take(AlbumChecks))
        {
            if (!artist.TryGetProperty("id", out var id))
                continue;
            var albums = await GetAsync($"artist/{id.GetRawText()}/albums?limit=100", ct, missing: true);
            if (albums.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(a => a.TryGetProperty("title", out var t) && titles.Contains(Normalise(t.GetString()))))
                return Picture(artist, _size);
        }
        return null;
    }

    /// <summary>The Deezer artist ID (P2722) of the Wikidata item with this MusicBrainz artist ID (P434), if it has one.</summary>
    private async Task<string?> WikidataDeezerIdAsync(string mbid, CancellationToken ct)
    {
        var search = await _http.GetFromJsonAsync<JsonElement>(new Uri(Wikidata,
            $"?format=json&action=query&list=search&srnamespace=0&srlimit=1&srsearch={Uri.EscapeDataString($"haswbstatement:P434={mbid}")}"), ct);
        if (!search.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var hits) || hits.GetArrayLength() == 0
            || hits[0].GetProperty("title").GetString() is not { } item)
            return null;
        var claims = await _http.GetFromJsonAsync<JsonElement>(new Uri(Wikidata, $"?format=json&action=wbgetclaims&property=P2722&entity={Uri.EscapeDataString(item)}"), ct);
        return DeezerIds(claims).FirstOrDefault();
    }

    /// <summary>The Deezer artist IDs in a <c>wbgetclaims</c> answer for P2722, preferred rank first and deprecated ones left out.</summary>
    public static IReadOnlyList<string> DeezerIds(JsonElement claims)
    {
        if (!claims.TryGetProperty("claims", out var all) || !all.TryGetProperty("P2722", out var ids))
            return [];
        return ids.EnumerateArray()
            .Select(c => (Rank: c.TryGetProperty("rank", out var r) ? r.GetString() : null,
                Id: c.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var value) && value.TryGetProperty("value", out var v)
                    ? v.GetString() : null))
            .Where(c => c.Id is { Length: > 0 } id && IsId(id) && c.Rank != "deprecated")
            .OrderBy(c => c.Rank == "preferred" ? 0 : 1)
            .Select(c => c.Id!)
            .ToList();
    }

    private static bool IsId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    // A Deezer album is also its one edition, so an edition's own ID and its album's both lead to the cover.
    private async Task<IReadOnlyList<ArtworkCandidate>> CoversAsync(ArtworkQuery query, CancellationToken ct)
    {
        foreach (var id in query.Ids.Concat(query.AlbumIds).Where(i => i.Provider == Providers.Deezer).Select(i => i.Value).Distinct())
        {
            if (!IsId(id))
                continue;
            var album = await _http.GetFromJsonAsync<JsonElement>(new Uri(Api, $"album/{id}"), ct);
            if (album.TryGetProperty($"cover_{_size}", out var cover) && cover.GetString() is { Length: > 0 } url && !IsPlaceholder(url))
                return [new ArtworkCandidate(url, "front")];
        }
        return [];
    }

    /// <summary>The artists in a search answer with exactly this name, ignoring case and accents, most followed first.</summary>
    public static IReadOnlyList<JsonElement> Named(JsonElement search, string name)
    {
        if (search.ValueKind != JsonValueKind.Object || !search.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        var wanted = Normalise(name);
        return data.EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) && Normalise(n.GetString()) == wanted)
            .OrderByDescending(a => a.TryGetProperty("nb_fan", out var f) && f.TryGetInt64(out var fans) ? fans : 0)
            .ToList();
    }

    /// <summary>An artist's picture, unless it's Deezer's placeholder.</summary>
    public static string? Picture(JsonElement artist, string size) =>
        artist.ValueKind == JsonValueKind.Object && artist.TryGetProperty($"picture_{size}", out var p) && p.GetString() is { Length: > 0 } url && !IsPlaceholder(url)
            ? url
            : null;

    /// <summary>Deezer's stand-in for a missing picture has no image ID in its path: <c>/images/artist//</c> or <c>/images/cover//</c>.</summary>
    public static bool IsPlaceholder(string url) => url.Contains("/artist//", StringComparison.Ordinal) || url.Contains("/cover//", StringComparison.Ordinal);

    private static string Normalise(string? text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(c));
        return string.Join(' ', plain.ToString().Replace('’', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(candidate.Key, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return new ArtworkImage(await response.Content.ReadAsByteArrayAsync(cancellationToken), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetAsync("search/artist?limit=1&q=Portishead", cancellationToken);
            return HealthReport.Healthy();
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach Deezer: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "Deezer didn't answer in time.");
        }
    }

    // Deezer's code for an ID it doesn't know.
    private const int NoData = 800;
    private static readonly JsonElement Nothing = JsonDocument.Parse("{}").RootElement;

    /// <param name="missing">Whether an unknown ID is nothing found (an empty answer) rather than an error.</param>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct, bool missing = false)
    {
        var body = await _http.GetFromJsonAsync<JsonElement>(new Uri(Api, path), ct);
        // Errors, including going over the request quota, come back as 200 with an error object.
        if (body.TryGetProperty("error", out var error))
        {
            if (missing && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == NoData)
                return Nothing;
            throw new HttpRequestException($"Deezer refused the request: {(error.TryGetProperty("message", out var m) ? m.GetString() : "unknown error")}");
        }
        return body;
    }
}
