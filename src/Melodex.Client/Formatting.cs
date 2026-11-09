namespace Melodex.Client;

/// <summary>What a format covers.</summary>
public enum FormatScopeKind
{
    /// <summary>Every matched file in the library.</summary>
    Library,

    Artist,
    Album,
    Track,
}

/// <summary>The files to format: the whole library, or an artist's, album's or track's.</summary>
/// <param name="Id">The artist, album or track; none for the whole library.</param>
public sealed record FormatScope(FormatScopeKind Kind, Guid? Id = null);

/// <summary>Where each part of a file's metadata comes from, and whether it moves.</summary>
/// <remarks>
/// Tags take <see cref="FormatSources.Keep"/> or <see cref="FormatSources.Catalogue"/>. Artwork takes
/// <see cref="FormatSources.Keep"/>, <see cref="FormatSources.Shown"/>, <see cref="FormatSources.Remove"/> or the ID of
/// an artwork plugin, as <see cref="FormatPlan.Artwork"/> lists them.
/// </remarks>
public sealed record FormatOptions
{
    /// <summary>Title, artist, album and album artist.</summary>
    public string Titles { get; init; } = FormatSources.Catalogue;

    /// <summary>Track and disc numbers, and how many there are.</summary>
    public string Numbers { get; init; } = FormatSources.Catalogue;

    /// <summary>Release and original dates, country, media and album type.</summary>
    public string Release { get; init; } = FormatSources.Catalogue;

    /// <summary>MusicBrainz IDs and the ISRC.</summary>
    public string Identifiers { get; init; } = FormatSources.Catalogue;

    /// <summary>The cover embedded in each file.</summary>
    public string Artwork { get; init; } = FormatSources.Keep;

    /// <summary>Move and rename files to where the path template says, going by their new tags.</summary>
    public bool Move { get; init; }
}

/// <summary>The sources a <see cref="FormatOptions"/> part can name, besides an artwork plugin's ID.</summary>
public static class FormatSources
{
    /// <summary>Leave it as the file has it.</summary>
    public const string Keep = "keep";

    /// <summary>What the catalogue says, as the index plugin (MusicBrainz) filled it in.</summary>
    public const string Catalogue = "catalogue";

    /// <summary>The cover Melodex shows for the album, from whichever artwork plugin found it.</summary>
    public const string Shown = "shown";

    /// <summary>Take the embedded cover out.</summary>
    public const string Remove = "remove";
}

/// <summary>A choice for one part of the metadata.</summary>
/// <param name="Id">What to put in <see cref="FormatOptions"/>.</param>
public sealed record FormatSource(string Id, string Name);

/// <summary>What formatting a scope involves, and the choices for it, before starting.</summary>
/// <param name="Title">What it covers, for example an album's title, or "the whole library".</param>
/// <param name="Files">Files in the scope.</param>
/// <param name="Formattable">Of those, the ones matched to the catalogue, which are the ones formatted.</param>
/// <param name="Tags">Sources for each group of tags.</param>
/// <param name="Artwork">Sources for the embedded cover.</param>
/// <param name="PathTemplate">Where files move to when moving, in beets' path format.</param>
/// <param name="WorkerReady">Whether the beets worker, which writes the files, is reachable.</param>
/// <param name="WorkerProblem">Why not, when it isn't.</param>
/// <param name="MusiclessFolders">For the whole library, the folders already with no music in them (nothing but artwork,
/// notes and the like, or nothing at all), which moving the files removes too; null when it couldn't be looked at.</param>
public sealed record FormatPlan(
    string Title,
    int Files,
    int Formattable,
    IReadOnlyList<FormatSource> Tags,
    IReadOnlyList<FormatSource> Artwork,
    string PathTemplate,
    bool WorkerReady,
    string? WorkerProblem,
    IReadOnlyList<string>? MusiclessFolders = null);

/// <summary>Body of <c>POST /library/format</c>.</summary>
public sealed record StartFormat(FormatScope Scope, FormatOptions Options);

/// <summary>What a cover is chosen for.</summary>
public enum CoverSubject
{
    Album,
    Track,
}

/// <summary>A picture that could be chosen as the cover.</summary>
/// <param name="Source">What to send back to choose it: <c>album:{id}</c>, <c>edition:{id}</c> or <c>file:{id}</c>.</param>
/// <param name="Group">The release it's from, for showing them together.</param>
/// <param name="Title">This picture's own name: the release's usual cover, an edition, or the files' own.</param>
/// <param name="Detail">When and where an edition came out, and its kind.</param>
/// <param name="Url">The picture. Like other covers, it can be 503 while it's still being fetched, or 404 when there's none.</param>
/// <param name="Current">The one shown now.</param>
public sealed record CoverOption(string Source, string Group, string Title, string? Detail, string Url, bool Current);

/// <summary>The covers to choose from for an album or song, from every release it's on, and what choosing writes.</summary>
/// <param name="Title">The album or song.</param>
/// <param name="Files">Files of it in the library, each of which gets the cover embedded.</param>
/// <param name="WorkerReady">Whether the beets worker, which writes the files, is reachable.</param>
/// <param name="WorkerProblem">Why not, when it isn't.</param>
public sealed record CoverChoices(string Title, string? CurrentUrl, IReadOnlyList<CoverOption> Options, int Files, bool WorkerReady, string? WorkerProblem);

/// <summary>A cover to use: one of the <see cref="CoverChoices"/>, or a picture of your own.</summary>
/// <param name="Source">A <see cref="CoverOption.Source"/>.</param>
/// <param name="Upload">Instead, a JPEG, PNG, WebP or GIF, base64, up to 10 MB.</param>
public sealed record ChooseCover(string? Source = null, string? Upload = null);

/// <summary>What choosing a cover did.</summary>
/// <param name="Written">Files it was embedded in. An album's folders get it as their folder image too.</param>
/// <param name="Failed">Files it couldn't be written to, with why.</param>
/// <param name="CoverUrl">The cover as Melodex now shows it.</param>
public sealed record CoverChosen(int Written, IReadOnlyList<string> Failed, string CoverUrl);
