using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Melodex.Core.Catalogue;

/// <summary>Normalisation and similarity for fuzzy matching of titles and names.</summary>
public static partial class TextMatch
{
    // Qualifiers that don't change which recording a title refers to. "Live", "remix", "edit" and the like do, so
    // they're kept and count against a match.
    [GeneratedRegex(@"[\(\[](?:[^\)\]]*\b(?:album version|original (?:mix|version)|remaster(?:ed)?|explicit|clean|bonus track|lp version|single|soundtrack version|as (?:featured|heard|seen) in)\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TrackQualifier();

    // Where a cover or soundtrack piece comes from, and who's featured: "(From "Kekkai Sensen")", "(feat. Lydia Night)".
    [GeneratedRegex(@"[\(\[]\s*(?:from|feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex SourceOrFeaturing();

    // On a live album the tracks are live anyway, and a file names the venue where the catalogue doesn't.
    [GeneratedRegex(@"[\(\[]\s*live\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex LiveQualifier();

    // Qualifiers that mark an edition of an album rather than a different album.
    [GeneratedRegex(@"[\(\[](?:[^\)\]]*\b(?:deluxe|expanded|anniversary|remaster(?:ed)?|special|edition|bonus|version|reissue|single|ep|flac|mp3|aac|m4a|ogg|opus|320|v0|web|cd|vinyl|\d{4})\b[^\)\]]*)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumQualifier();

    // Streaming services put qualifiers after a dash ("Song - 2016 Remaster", "Song - Live at X", "Album - Single"); the
    // last one is read as if it were in brackets.
    [GeneratedRegex(@"\s+[-–]\s+(?!.*\s[-–]\s)(.+)$")]
    private static partial Regex DashQualifier();

    [GeneratedRegex(@"\s*[\(\[]?\b(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    // Between artists in one tag or credit: "A; B", "A, B", "A & B", "A feat. B".
    [GeneratedRegex(@"\s*(?:;|,|&|/|\s(?:and|x|feat\.?|ft\.?|featuring|with)\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    [GeneratedRegex(@"^the\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingThe();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Lower case, no accents or punctuation, single spaces.</summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = text.ToLowerInvariant()
            .Replace("œ", "oe").Replace("æ", "ae").Replace("ß", "ss").Replace("ø", "o").Replace("ł", "l").Replace("đ", "d")
            .Replace("&", " and ").Replace("’", "'").Replace("'", "");
        var decomposed = s.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return NonAlphanumeric().Replace(builder.ToString(), " ").Trim();
    }

    /// <param name="live">Whether the title is on a live album, where "(Live at …)" doesn't tell recordings apart.</param>
    public static string NormaliseTitle(string? title, bool live = false)
    {
        var s = SourceOrFeaturing().Replace(TrackQualifier().Replace(DashQualifier().Replace(title ?? "", " ($1)"), " "), " ");
        return Normalise(live ? LiveQualifier().Replace(s, " ") : s);
    }

    public static string NormaliseAlbum(string? album) => Normalise(AlbumQualifier().Replace(DashQualifier().Replace(album ?? "", " ($1)"), " "));

    public static string NormaliseArtist(string? artist) =>
        Normalise(LeadingThe().Replace(Featuring().Replace(artist ?? "", ""), ""));

    /// <summary>
    /// How alike two artist credits are, from 0 to 1: the whole credits, or any artist in one against any in the other,
    /// so a file tagged "Gerard Way; Lydia Night" fits a credit of "Gerard Way &amp; Lydia Night".
    /// </summary>
    public static double ArtistSimilarity(string a, string b)
    {
        var best = Similarity(NormaliseArtist(a), NormaliseArtist(b));
        foreach (var x in ArtistParts(a))
        foreach (var y in ArtistParts(b))
            best = Math.Max(best, Similarity(x, y));
        return best;
    }

    private static IEnumerable<string> ArtistParts(string credit) =>
        ArtistSeparator().Split(credit).Select(p => Normalise(LeadingThe().Replace(p, ""))).Where(p => p.Length > 0);

    /// <summary>"Iwasaki, Taku" as "Taku Iwasaki", normalised: how a sort name reads as a name.</summary>
    public static string SortNameAsName(string? sortName)
    {
        if (string.IsNullOrWhiteSpace(sortName))
            return "";
        var comma = sortName.IndexOf(',');
        return NormaliseArtist(comma < 0 ? sortName : $"{sortName[(comma + 1)..].Trim()} {sortName[..comma]}");
    }

    /// <summary>From 0 to 1: the better of character-level similarity and word-order-insensitive similarity.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1;
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1;
        var sorted = Ratio(SortWords(a), SortWords(b));
        return Math.Max(Ratio(a, b), sorted);
    }

    private static string SortWords(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static double Ratio(string a, string b) => 1 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
