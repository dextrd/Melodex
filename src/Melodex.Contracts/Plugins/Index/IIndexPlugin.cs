using Melodex.Contracts.Catalogue;

namespace Melodex.Contracts.Plugins.Index;

/// <summary>
/// Index plugin contract (version 1.0): answers what exists. All IDs passed in and returned are in the
/// plugin's own scheme. Caching and rate limiting live in the core, so implementations should call their
/// upstream service directly and not cache on their own.
/// </summary>
/// <remarks>
/// Methods return <c>null</c> when an entity doesn't exist and throw for anything else, such as network
/// failures or upstream errors. Optional features are separate interfaces (<see cref="IIsrcLookup"/>,
/// <see cref="IUpcLookup"/>, <see cref="IPopularityProvider"/>), each matched by a capability
/// name in the manifest. Pictures come from artwork plugins (<see cref="Artwork.IArtworkPlugin"/>).
/// </remarks>
public interface IIndexPlugin : IPlugin
{
    /// <summary>Searches artists, albums and tracks.</summary>
    Task<IndexSearchResults> SearchAsync(IndexSearchQuery query, CancellationToken cancellationToken);

    /// <summary>Gets one artist.</summary>
    Task<IndexArtist?> GetArtistAsync(string artistId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists an artist's albums of every type. The core does the filtering for the missing view, so don't
    /// leave out live albums, compilations and the like.
    /// </summary>
    Task<IReadOnlyList<IndexAlbum>> GetArtistAlbumsAsync(string artistId, CancellationToken cancellationToken);

    /// <summary>Gets an album with its editions and their track listings.</summary>
    Task<IndexAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken);
}

/// <summary>What to search for.</summary>
/// <param name="Text">Free text.</param>
/// <param name="Kinds">Which entity kinds to return; all of them when empty.</param>
/// <param name="Limit">Maximum results per kind.</param>
public sealed record IndexSearchQuery(string Text, IReadOnlySet<SearchKind>? Kinds = null, int Limit = 25);

/// <summary>Entity kinds a search can return.</summary>
public enum SearchKind
{
    /// <summary>Artists.</summary>
    Artist,

    /// <summary>Albums.</summary>
    Album,

    /// <summary>Tracks.</summary>
    Track,
}

/// <summary>Search results, each list ordered by relevance.</summary>
public sealed record IndexSearchResults(
    IReadOnlyList<IndexArtist> Artists,
    IReadOnlyList<IndexAlbum> Albums,
    IReadOnlyList<IndexTrack> Tracks)
{
    /// <summary>No results.</summary>
    public static IndexSearchResults Empty { get; } = new([], [], []);
}

/// <summary>Optional: find recordings by ISRC. Capability <see cref="IndexCapabilities.IsrcLookup"/>.</summary>
public interface IIsrcLookup
{
    /// <summary>Recordings carrying this ISRC; usually one, sometimes several.</summary>
    Task<IReadOnlyList<IndexTrack>> LookupIsrcAsync(string isrc, CancellationToken cancellationToken);
}

/// <summary>Optional: the albums a recording is on. Capability <see cref="IndexCapabilities.RecordingAlbums"/>.</summary>
public interface IRecordingAlbums
{
    /// <summary>
    /// The albums with an edition this recording is on, of every type. Empty when the recording isn't known.
    /// </summary>
    Task<IReadOnlyList<IndexAlbum>> GetRecordingAlbumsAsync(string recordingId, CancellationToken cancellationToken);
}

/// <summary>Optional: find editions by barcode. Capability <see cref="IndexCapabilities.UpcLookup"/>.</summary>
public interface IUpcLookup
{
    /// <summary>Editions carrying this barcode, with the album each belongs to.</summary>
    Task<IReadOnlyList<IndexAlbumDetail>> LookupUpcAsync(string upc, CancellationToken cancellationToken);
}

/// <summary>Optional: popularity. Capability <see cref="IndexCapabilities.Popularity"/>.</summary>
public interface IPopularityProvider
{
    /// <summary>Popularity from 0 to 1 for each requested track ID it knows; unknown IDs are left out.</summary>
    Task<IReadOnlyDictionary<string, double>> GetTrackPopularityAsync(IReadOnlyCollection<string> trackIds, CancellationToken cancellationToken);
}

/// <summary>Capability names an index plugin can declare in its manifest.</summary>
public static class IndexCapabilities
{
    /// <summary>Implements <see cref="IIsrcLookup"/>.</summary>
    public const string IsrcLookup = "isrc-lookup";

    /// <summary>Implements <see cref="IRecordingAlbums"/>.</summary>
    public const string RecordingAlbums = "recording-albums";

    /// <summary>Implements <see cref="IUpcLookup"/>.</summary>
    public const string UpcLookup = "upc-lookup";

    /// <summary>Fills <c>ExternalIds</c> with links to other providers.</summary>
    public const string ExternalIds = "external-ids";

    /// <summary>Implements <see cref="IPopularityProvider"/>.</summary>
    public const string Popularity = "popularity";
}
