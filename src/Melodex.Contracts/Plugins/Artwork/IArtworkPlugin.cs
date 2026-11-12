using Melodex.Contracts.Catalogue;

namespace Melodex.Contracts.Plugins.Artwork;

/// <summary>
/// Artwork plugin contract (version 1.0): pictures of artists, albums and editions. The core asks the enabled artwork
/// plugins in the user's order the first time a picture is shown, keeps the first image one of them returns, and
/// serves it from its own cache from then on, so plugins needn't cache.
/// </summary>
/// <remarks>
/// A plugin declares which of <see cref="ArtworkCapabilities"/> it answers for, and is only asked about those.
/// Finding and downloading are separate so a plugin can offer several images, best first, and the core can move on
/// to the next when one turns out to be missing. Return nothing when there's no picture; throw for anything else,
/// such as network failures, so the core tries again later instead of remembering that there's none.
/// </remarks>
public interface IArtworkPlugin : IPlugin
{
    /// <summary>Images for the subject, best first. Empty when the plugin has none.</summary>
    Task<IReadOnlyList<ArtworkCandidate>> FindAsync(ArtworkQuery query, CancellationToken cancellationToken);

    /// <summary>Downloads a candidate from <see cref="FindAsync"/>; null if it turns out not to exist.</summary>
    Task<ArtworkImage?> DownloadAsync(ArtworkCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>What a picture is of.</summary>
public enum ArtworkSubject
{
    /// <summary>An artist.</summary>
    Artist,

    /// <summary>An album in general: its usual cover.</summary>
    Album,

    /// <summary>One edition of an album, such as the one in the library.</summary>
    Edition,
}

/// <summary>The subject to find a picture of, with everything the core knows that might identify it.</summary>
/// <param name="Subject">Artist, album or edition.</param>
/// <param name="Title">The artist's name, or the album's or edition's title.</param>
/// <param name="ArtistName">For an album or edition, its artist credit.</param>
/// <param name="Ids">The subject's IDs: for MusicBrainz, the artist, release group or release ID.</param>
/// <param name="AlbumIds">For an edition, its album's IDs, such as the MusicBrainz release group.</param>
/// <param name="ArtistIds">For an album or edition, its artist's IDs.</param>
/// <param name="AlbumTitles">
/// For an artist, the titles of albums of theirs the core knows, so a plugin that finds artists by name can tell two
/// with the same name apart. Empty for albums and editions, and from older cores.
/// </param>
public sealed record ArtworkQuery(
    ArtworkSubject Subject,
    string Title,
    string? ArtistName,
    IReadOnlyList<ProviderId> Ids,
    IReadOnlyList<ProviderId> AlbumIds,
    IReadOnlyList<ProviderId> ArtistIds,
    IReadOnlyList<string>? AlbumTitles = null);

/// <summary>An image a plugin can download.</summary>
/// <param name="Key">The plugin's own reference to it, such as a URL; the core only hands it back.</param>
/// <param name="Kind">For example <c>front</c> or <c>thumb</c>.</param>
public sealed record ArtworkCandidate(string Key, string Kind = "front");

/// <summary>A downloaded image.</summary>
/// <param name="Data">The image file's bytes.</param>
/// <param name="ContentType">An image media type, such as <c>image/jpeg</c>.</param>
public sealed record ArtworkImage(byte[] Data, string ContentType);

/// <summary>Capability names an artwork plugin declares: the subjects it can find pictures of.</summary>
public static class ArtworkCapabilities
{
    /// <summary>Pictures of artists.</summary>
    public const string Artists = "artists";

    /// <summary>Album covers.</summary>
    public const string Albums = "albums";

    /// <summary>Covers of particular editions.</summary>
    public const string Editions = "editions";

    /// <summary>The capability that covers a subject.</summary>
    public static string For(ArtworkSubject subject) => subject switch
    {
        ArtworkSubject.Artist => Artists,
        ArtworkSubject.Album => Albums,
        _ => Editions,
    };
}
