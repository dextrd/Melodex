using System.Text;

namespace Melodex.Contracts.Plugins.Connectors;

/// <summary>
/// Telling whether a media server's song is the one a <see cref="TrackLookup"/> asks for, the same way for every
/// connector: the most certain evidence first, and tags compared without case, accents or punctuation.
/// </summary>
public static class TrackMatching
{
    /// <summary>A song as a media server lists it.</summary>
    /// <param name="Id">The server's ID for it.</param>
    /// <param name="Title">Its title.</param>
    /// <param name="Artist">Its artist, as the server has it.</param>
    /// <param name="Album">Its album, as the server has it.</param>
    /// <param name="Duration">Its length.</param>
    /// <param name="Path">Its file as the server sees it, absolute or not.</param>
    /// <param name="MusicBrainzRecordingId">Its MusicBrainz recording ID, if the server knows it.</param>
    /// <param name="Isrcs">Its ISRCs, if the server knows them.</param>
    public sealed record Candidate(
        string Id,
        string Title,
        string? Artist,
        string? Album,
        TimeSpan? Duration,
        string? Path,
        string? MusicBrainzRecordingId,
        IReadOnlyList<string> Isrcs);

    /// <summary>
    /// The best of the songs for the lookup: by MusicBrainz recording ID, then ISRC, then path, then title, artist,
    /// album and length (within three seconds). Null if none fits. A song with a MusicBrainz ID of its own that isn't
    /// the one asked for is a different song, however alike its tags.
    /// </summary>
    public static Candidate? Pick(IEnumerable<Candidate> songs, TrackLookup lookup)
    {
        var list = songs as IReadOnlyList<Candidate> ?? songs.ToList();
        if (lookup.MusicBrainzRecordingId is { Length: > 0 } recording
            && list.FirstOrDefault(s => string.Equals(s.MusicBrainzRecordingId, recording, StringComparison.OrdinalIgnoreCase)) is { } byRecording)
            return byRecording;
        if (lookup.Isrc is { Length: > 0 } isrc && list.FirstOrDefault(s => s.Isrcs.Any(i => SameIsrc(i, isrc))) is { } byIsrc)
            return byIsrc;
        if (lookup.Path is { Length: > 0 } path && list.FirstOrDefault(s => s.Path is not null && SamePath(s.Path, path)) is { } byPath)
            return byPath;

        return list
            .Where(s => lookup.MusicBrainzRecordingId is null || string.IsNullOrEmpty(s.MusicBrainzRecordingId))
            .Where(s => SameText(s.Title, lookup.Title))
            .Where(s => lookup.Artist is null || SameText(s.Artist, lookup.Artist))
            .Where(s => lookup.Album is null || SameText(s.Album, lookup.Album))
            .Where(s => lookup.Duration is null || s.Duration is null || Math.Abs((s.Duration.Value - lookup.Duration.Value).TotalSeconds) <= 3)
            .FirstOrDefault();
    }

    /// <summary>A title without what's in brackets at its end, such as "Digital Love" for "Digital Love (album version)".</summary>
    public static string? WithoutBrackets(string title)
    {
        var cut = title.IndexOfAny(['(', '[']);
        return cut > 0 ? title[..cut].Trim() : null;
    }

    /// <summary>The same ISRC, with or without its hyphens.</summary>
    public static bool SameIsrc(string a, string b) =>
        string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's path ends with Melodex's library-relative one.</summary>
    public static bool SamePath(string server, string ours) =>
        server.Replace('\\', '/').TrimStart('/').EndsWith(ours.Replace('\\', '/').TrimStart('/'), StringComparison.Ordinal);

    /// <summary>The same words, without regard to case, accents, spaces or punctuation.</summary>
    public static bool SameText(string? a, string? b) => a is not null && b is not null && Simplify(a) == Simplify(b);

    private static string Simplify(string text)
    {
        var normalised = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
            if (char.IsLetterOrDigit(c))
                result.Append(char.ToLowerInvariant(c));
        return result.ToString();
    }
}
