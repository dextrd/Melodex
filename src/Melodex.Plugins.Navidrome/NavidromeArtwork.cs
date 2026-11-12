using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Artwork;

namespace Melodex.Plugins.Navidrome;

/// <summary>Entry point for Navidrome cover art. It uses the Navidrome media server's connection rather than settings of its own.</summary>
public sealed class NavidromeArtworkFactory : IPluginFactory
{
    public const string PluginId = "melodex.artwork.navidrome";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Navidrome cover art",
        Version = "0.1.0",
        Type = PluginType.Artwork,
        ContractVersion = ContractVersions.Artwork.ToString(),
        Description = "The artwork in your own files, through Navidrome: embedded covers and cover images in album folders. Albums you have only.",
        Homepage = "https://www.navidrome.org",
        Capabilities = [ArtworkCapabilities.Albums, ArtworkCapabilities.Editions],
        Access = new PluginAccess { Network = ["Your Navidrome server"] },
        UsesSettingsOf = NavidromeConnectorFactory.PluginId,
    };

    public IPlugin Create(PluginContext context) => new NavidromeArtwork(context);
}

/// <summary>
/// Album covers as Navidrome shows them. The album is found by its MusicBrainz release ID, or else by title and
/// artist. Navidrome sends a placeholder for an album without artwork, so only albums it reports artwork for are offered.
/// </summary>
public sealed class NavidromeArtwork(PluginContext context) : IArtworkPlugin
{
    private const string Size = "600";

    private readonly SubsonicClient _client = new(context);

    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken)
    {
        if (query.Subject == ArtworkSubject.Artist || string.IsNullOrWhiteSpace(query.Title))
            return [];
        var reply = await _client.CallAsync("search3", [("query", query.Title), ("albumCount", "20"), ("songCount", "0"), ("artistCount", "0")], cancellationToken);
        var albums = reply.TryGetProperty("searchResult3", out var result) ? NavidromeConnector.Array(result, "album").ToList() : [];
        return Pick(albums, query) is { } album && NavidromeConnector.Text(album, "coverArt") is { Length: > 0 } coverArt
            ? [new ArtworkCandidate(coverArt)]
            : [];
    }

    /// <summary>The album that is the subject: by release ID for an edition, then by title and artist.</summary>
    public static JsonElement? Pick(IReadOnlyList<JsonElement> albums, ArtworkQuery query)
    {
        var releases = query.Subject == ArtworkSubject.Edition
            ? query.Ids.Where(i => i.Provider == Providers.MusicBrainz).Select(i => i.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var album in albums)
            if (NavidromeConnector.Text(album, "musicBrainzId") is { Length: > 0 } id && releases.Contains(id))
                return album;
        foreach (var album in albums)
        {
            // An album Navidrome knows to be a different release isn't this one, however alike the names.
            var known = NavidromeConnector.Text(album, "musicBrainzId");
            if (releases.Count > 0 && !string.IsNullOrEmpty(known))
                continue;
            if (TrackMatching.SameText(NavidromeConnector.Text(album, "name"), query.Title)
                && (query.ArtistName is null || TrackMatching.SameText(NavidromeConnector.Text(album, "artist"), query.ArtistName)))
                return album;
        }
        return null;
    }

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var (data, type) = await _client.DownloadAsync("getCoverArt", [("id", candidate.Key), ("size", Size)], cancellationToken);
            return new ArtworkImage(data, type);
        }
        catch (SubsonicException e) when (e.Code == 70)
        {
            return null;
        }
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken) =>
        (await _client.PingAsync(cancellationToken)).Report;
}
