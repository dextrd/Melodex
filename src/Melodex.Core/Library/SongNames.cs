using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Melodex.Core.Library;

/// <summary>
/// Song titles and artist names as they're compared between places that write them differently: only their letters
/// and digits, without accents, so "Don't" is "Don’t" and "Beyoncé" is "Beyonce"; titles without "(feat. …)", and
/// credits split into their artists.
/// </summary>
public static partial class SongNames
{
    public static string TitleKey(string title) => Key(Featuring().Replace(title, ""));

    /// <summary>Each artist a credit names, such as "Queen; David Bowie" or "Daft Punk feat. Pharrell Williams".</summary>
    public static IEnumerable<string> ArtistKeys(string credit) =>
        Separators().Split(credit).Select(Key).Where(k => k.Length > 0);

    public static string Key(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c))
                result.Append(char.ToLowerInvariant(c));
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
        return result.ToString();
    }

    // "(feat. X)", "[with X]", or " feat. X" to the end of a title; a bare "with" is part of titles such as "Stay with Me".
    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]|\s+(?:feat\.|ft\.|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"\s*(?:;|,|/|&|\+|\bx\b|\band\b|\bfeat\.?|\bft\.|\bfeaturing\b|\bwith\b|\bvs\.?)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();
}
