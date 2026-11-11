using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Index;
using Melodex.Contracts.Plugins.Lyrics;
using Melodex.Contracts.Plugins.Remote;

namespace Melodex.Core.Plugins;

/// <summary>
/// A catalogue, artwork, lyrics or media-server plugin that isn't loaded into Melodex but reached at an address, over
/// the HTTP contract for its kind (docs/contracts/remote-plugins.md). It's used just as one loaded in process is.
/// </summary>
public abstract class RemotePlugin(HttpClient http, JsonElement settings, PluginManifest manifest) : IPlugin
{
    protected PluginManifest Manifest { get; } = manifest;

    /// <summary>The client for an instance's kind, offering the optional features its manifest declares.</summary>
    public static IPlugin Create(PluginType type, HttpClient http, JsonElement settings, PluginManifest manifest) => type switch
    {
        PluginType.Index => new RemoteIndexPlugin(http, settings, manifest),
        PluginType.Artwork => new RemoteArtworkPlugin(http, settings, manifest),
        PluginType.Lyrics => new RemoteLyricsPlugin(http, settings, manifest),
        PluginType.Connector => RemoteConnector.Create(http, settings, manifest),
        _ => throw new InvalidOperationException($"{type} plugins aren't reached this way."),
    };

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken) =>
        (await CallAsync<object, HealthReport>("v1/health", null, cancellationToken))!;

    protected bool Declares(string capability) => Manifest.Capabilities.Contains(capability);

    /// <summary>Calls the plugin; a 404 is no answer.</summary>
    protected async Task<TResult?> CallAsync<TInput, TResult>(string path, TInput? input, CancellationToken ct)
    {
        using var response = await PostAsync(path, input, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        return await response.Content.ReadFromJsonAsync<TResult>(RemoteJson.Options, ct);
    }

    /// <summary>Calls the plugin for a list; a 404 is an empty one.</summary>
    protected async Task<IReadOnlyList<TResult>> ListAsync<TInput, TResult>(string path, TInput input, CancellationToken ct) =>
        await CallAsync<TInput, List<TResult>>(path, input, ct) ?? [];

    /// <summary>Calls the plugin for a file, such as a picture; a 404 is none.</summary>
    protected async Task<(byte[] Data, string ContentType)?> FileAsync<TInput>(string path, TInput input, CancellationToken ct)
    {
        using var response = await PostAsync(path, input, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    protected async Task SendAsync<TInput>(string path, TInput input, CancellationToken ct)
    {
        using var _ = await PostAsync(path, input, ct);
    }

    private async Task<HttpResponseMessage> PostAsync<TInput>(string path, TInput? input, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(path, new RemoteCall<TInput>(settings, input), RemoteJson.Options, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return response;
        using (response)
        {
            RemoteError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<RemoteError>(RemoteJson.Options, ct);
            }
            catch (JsonException)
            {
            }
            var message = error?.Error ?? $"{Manifest.Name} answered {(int)response.StatusCode} {response.ReasonPhrase}";
            throw Failure(message, error?.Code == RemoteErrorCodes.Unavailable
                                   || response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests);
        }
    }

    /// <summary>What the plugin's refusal is to Melodex: a catalogue that can't answer now is tried again later.</summary>
    protected virtual Exception Failure(string message, bool unavailable) => new RemotePluginException(message, unavailable);
}

/// <summary>A plugin at an address said no, with its reason.</summary>
public sealed class RemotePluginException(string message, bool unavailable) : Exception(message)
{
    /// <summary>Its source can't answer right now, so it's worth asking again later.</summary>
    public bool Unavailable { get; } = unavailable;
}

public sealed class RemoteIndexPlugin(HttpClient http, JsonElement settings, PluginManifest manifest)
    : RemotePlugin(http, settings, manifest), IIndexPlugin, IIsrcLookup, IRecordingAlbums
{
    public async Task<IndexSearchResults> SearchAsync(IndexSearchQuery query, CancellationToken cancellationToken) =>
        await CallAsync<IndexSearchQuery, IndexSearchResults>("v1/search", query, cancellationToken) ?? IndexSearchResults.Empty;

    public Task<IndexArtist?> GetArtistAsync(string artistId, CancellationToken cancellationToken) =>
        CallAsync<IdInput, IndexArtist>("v1/artist", new IdInput(artistId), cancellationToken);

    public Task<IReadOnlyList<IndexAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken) =>
        ListAsync<IdInput, IndexAlbum>("v1/artist-albums", new IdInput(artistId), cancellationToken);

    public Task<IndexAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken) =>
        CallAsync<IdInput, IndexAlbumDetail>("v1/album", new IdInput(albumId), cancellationToken);

    // Optional: a catalogue that doesn't declare them has nothing to say, just as one without them wouldn't.
    public Task<IReadOnlyList<IndexTrack>> LookupIsrcAsync(string isrc, CancellationToken cancellationToken) =>
        Declares(IndexCapabilities.IsrcLookup)
            ? ListAsync<IsrcInput, IndexTrack>("v1/isrc", new IsrcInput(isrc), cancellationToken)
            : Task.FromResult<IReadOnlyList<IndexTrack>>([]);

    public Task<IReadOnlyList<IndexAlbum>> GetRecordingAlbumsAsync(string recordingId, CancellationToken cancellationToken) =>
        Declares(IndexCapabilities.RecordingAlbums)
            ? ListAsync<IdInput, IndexAlbum>("v1/recording-albums", new IdInput(recordingId), cancellationToken)
            : Task.FromResult<IReadOnlyList<IndexAlbum>>([]);

    protected override Exception Failure(string message, bool unavailable) =>
        unavailable ? new IndexUnavailableException(message) : base.Failure(message, unavailable);
}

public sealed class RemoteArtworkPlugin(HttpClient http, JsonElement settings, PluginManifest manifest)
    : RemotePlugin(http, settings, manifest), IArtworkPlugin
{
    public Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken) =>
        ListAsync<ArtworkQuery, ArtworkCandidate>("v1/find", query, cancellationToken);

    public async Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken) =>
        await FileAsync("v1/download", candidate, cancellationToken) is { } file ? new ArtworkImage(file.Data, file.ContentType) : null;
}

public sealed class RemoteLyricsPlugin(HttpClient http, JsonElement settings, PluginManifest manifest)
    : RemotePlugin(http, settings, manifest), ILyricsPlugin
{
    public Task<FoundLyrics?> FindAsync(LyricsQuery query, CancellationToken cancellationToken) =>
        CallAsync<LyricsQuery, FoundLyrics>("v1/find", query, cancellationToken);

    public Task<IReadOnlyList<LyricsResult>> SearchAsync(LyricsSearch query, CancellationToken cancellationToken) =>
        Declares(LyricsCapabilities.Search)
            ? ListAsync<LyricsSearch, LyricsResult>("v1/search", query, cancellationToken)
            : Task.FromResult<IReadOnlyList<LyricsResult>>([]);
}

/// <summary>
/// A media server's connector at an address. Melodex checks which features a connector has by what it implements, so
/// there's a client for each mix of the features Melodex uses: a server's identity, and users' playlists.
/// </summary>
public class RemoteConnector(HttpClient http, JsonElement settings, PluginManifest manifest)
    : RemotePlugin(http, settings, manifest), IMediaServerConnector
{
    public static RemoteConnector Create(HttpClient http, JsonElement settings, PluginManifest manifest) =>
        (manifest.Capabilities.Contains(ConnectorCapabilities.ServerIdentity), manifest.Capabilities.Contains(ConnectorCapabilities.UserPlaylists)) switch
        {
            (true, true) => new WithIdentityAndPlaylists(http, settings, manifest),
            (true, false) => new WithIdentity(http, settings, manifest),
            (false, true) => new WithPlaylists(http, settings, manifest),
            _ => new RemoteConnector(http, settings, manifest),
        };

    public Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken) =>
        SendAsync("v1/scan", new ScanInput(paths), cancellationToken);

    public async Task<ScanStatus> GetScanStatusAsync(CancellationToken cancellationToken) =>
        await CallAsync<object, ScanStatus>("v1/scan-status", new { }, cancellationToken) ?? new ScanStatus(false);

    public Task<string?> ResolveTrackAsync(TrackLookup lookup, CancellationToken cancellationToken) =>
        CallAsync<TrackLookup, string>("v1/resolve", lookup, cancellationToken);

    protected Task<ServerIdentity?> ServerAsync(CancellationToken ct) => CallAsync<object, ServerIdentity>("v1/server", new { }, ct);

    protected Task<HealthReport> CheckLoginAsync(ServerLogin login, CancellationToken ct) =>
        CallAsync<LoginInput, HealthReport>("v1/playlists/login", new LoginInput(login), ct)!;

    protected Task<IReadOnlyList<UserPlaylist>> PlaylistsAsync(ServerLogin login, CancellationToken ct) =>
        ListAsync<LoginInput, UserPlaylist>("v1/playlists/list", new LoginInput(login), ct);

    protected Task<UserPlaylistSongs?> PlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct) =>
        CallAsync<PlaylistInput, UserPlaylistSongs>("v1/playlists/get", new PlaylistInput(login, playlistId), ct);

    protected async Task<string> CreatePlaylistAsync(ServerLogin login, PlaylistContent content, CancellationToken ct) =>
        await CallAsync<PlaylistContentInput, string>("v1/playlists/create", new PlaylistContentInput(login, content), ct)
        ?? throw new RemotePluginException($"{Manifest.Name} didn't say what it called the new playlist.", false);

    protected Task UpdatePlaylistAsync(ServerLogin login, string playlistId, PlaylistContent content, CancellationToken ct) =>
        SendAsync("v1/playlists/update", new PlaylistContentInput(login, content, playlistId), ct);

    protected Task DeletePlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct) =>
        SendAsync("v1/playlists/delete", new PlaylistInput(login, playlistId), ct);

    protected async Task<PlaylistCover?> PlaylistCoverAsync(ServerLogin login, string playlistId, CancellationToken ct) =>
        await FileAsync("v1/playlists/cover", new PlaylistInput(login, playlistId), ct) is { } file ? new PlaylistCover(file.Data, file.ContentType) : null;

    protected Task SetPlaylistCoverAsync(ServerLogin login, string playlistId, PlaylistCover? cover, CancellationToken ct) =>
        SendAsync("v1/playlists/set-cover", new PlaylistCoverInput(login, playlistId, cover), ct);

    private sealed class WithIdentity(HttpClient http, JsonElement settings, PluginManifest manifest)
        : RemoteConnector(http, settings, manifest), IServerIdentity
    {
        public Task<ServerIdentity?> GetServerAsync(CancellationToken cancellationToken) => ServerAsync(cancellationToken);
    }

    private class WithPlaylists(HttpClient http, JsonElement settings, PluginManifest manifest)
        : RemoteConnector(http, settings, manifest), IPlaylistSync
    {
        public bool SyncsPlaylists => true;
        public bool SyncsPlaylistCovers => true;
        Task<HealthReport> IPlaylistSync.CheckLoginAsync(ServerLogin login, CancellationToken ct) => CheckLoginAsync(login, ct);
        public Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(ServerLogin login, CancellationToken ct) => PlaylistsAsync(login, ct);
        public Task<UserPlaylistSongs?> GetPlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct) => PlaylistAsync(login, playlistId, ct);
        Task<string> IPlaylistSync.CreatePlaylistAsync(ServerLogin login, PlaylistContent content, CancellationToken ct) => CreatePlaylistAsync(login, content, ct);
        Task IPlaylistSync.UpdatePlaylistAsync(ServerLogin login, string playlistId, PlaylistContent content, CancellationToken ct) =>
            UpdatePlaylistAsync(login, playlistId, content, ct);
        Task IPlaylistSync.DeletePlaylistAsync(ServerLogin login, string playlistId, CancellationToken ct) => DeletePlaylistAsync(login, playlistId, ct);
        public Task<PlaylistCover?> GetPlaylistCoverAsync(ServerLogin login, string playlistId, CancellationToken ct) => PlaylistCoverAsync(login, playlistId, ct);
        Task IPlaylistSync.SetPlaylistCoverAsync(ServerLogin login, string playlistId, PlaylistCover? cover, CancellationToken ct) =>
            SetPlaylistCoverAsync(login, playlistId, cover, ct);
    }

    private sealed class WithIdentityAndPlaylists(HttpClient http, JsonElement settings, PluginManifest manifest)
        : WithPlaylists(http, settings, manifest), IServerIdentity
    {
        public Task<ServerIdentity?> GetServerAsync(CancellationToken cancellationToken) => ServerAsync(cancellationToken);
    }
}
