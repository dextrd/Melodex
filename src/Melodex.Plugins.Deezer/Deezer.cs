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
        Version = "0.1.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "Artist pictures and album covers from Deezer's public catalogue. No account needed. Artists are found by name, so two with the same name can be mixed up; covers are for albums Deezer's catalogue knows.",
        Homepage = "https://www.deezer.com",
        Capabilities = [ArtworkCapabilities.Artists, ArtworkCapabilities.Albums, ArtworkCapabilities.Editions],
        Access = new PluginAccess { Network = ["api.deezer.com", "cdn-images.dzcdn.net", "e-cdns-images.dzcdn.net"] },
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
/// The picture of the Deezer artist with exactly the subject's name (ignoring case and accents), the one with the
/// most fans when there are several, and the covers of albums by their Deezer IDs. Deezer gives a blank placeholder
/// for artists and albums without one, which isn't offered.
/// </summary>
public sealed class Deezer(PluginContext context) : IArtworkPlugin
{
    private static readonly Uri Api = new("https://api.deezer.com/");

    private readonly HttpClient _http = context.HttpClient;
    private readonly string _size = context.Settings.TryGetProperty("size", out var size) && size.GetString() is "medium" or "xl" ? size.GetString()! : "big";

    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        if (query.Subject != ArtworkSubject.Artist)
            return await CoversAsync(query, cancellationToken);
        if (string.IsNullOrWhiteSpace(query.Title))
            return [];
        var found = await GetAsync($"search/artist?limit=25&q={Uri.EscapeDataString(query.Title)}", cancellationToken);
        return Pick(found, query.Title, _size) is { } url ? [new ArtworkCandidate(url, "photo")] : [];
    }

    // A Deezer album is also its one edition, so an edition's own ID and its album's both lead to the cover.
    private async Task<IReadOnlyList<ArtworkCandidate>> CoversAsync(ArtworkQuery query, CancellationToken ct)
    {
        foreach (var id in query.Ids.Concat(query.AlbumIds).Where(i => i.Provider == Providers.Deezer).Select(i => i.Value).Distinct())
        {
            if (id.Length == 0 || !id.All(char.IsAsciiDigit))
                continue;
            var album = await _http.GetFromJsonAsync<JsonElement>(new Uri(Api, $"album/{id}"), ct);
            if (album.TryGetProperty($"cover_{_size}", out var cover) && cover.GetString() is { Length: > 0 } url && !IsPlaceholder(url))
                return [new ArtworkCandidate(url, "front")];
        }
        return [];
    }

    /// <summary>The picture of the best-matching artist in a search answer, if they have one.</summary>
    public static string? Pick(JsonElement search, string name, string size)
    {
        if (!search.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        var wanted = Normalise(name);
        return data.EnumerateArray()
            .Where(a => a.TryGetProperty("name", out var n) && Normalise(n.GetString()) == wanted)
            .OrderByDescending(a => a.TryGetProperty("nb_fan", out var f) && f.TryGetInt64(out var fans) ? fans : 0)
            .Select(a => a.TryGetProperty($"picture_{size}", out var p) ? p.GetString() : null)
            .FirstOrDefault(url => url is { Length: > 0 } && !IsPlaceholder(url));
    }

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

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await _http.GetFromJsonAsync<JsonElement>(new Uri(Api, path), ct);
        // Errors, including going over the request quota, come back as 200 with an error object.
        if (body.TryGetProperty("error", out var error))
            throw new HttpRequestException($"Deezer refused the request: {(error.TryGetProperty("message", out var m) ? m.GetString() : "unknown error")}");
        return body;
    }
}
