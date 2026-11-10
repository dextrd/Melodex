using System.Text.Json;
using System.Text.Json.Serialization;
using Melodex.Contracts.Catalogue;

namespace Melodex.Client;

// Models of the public API, /api/v1. Every catalogue entity carries its internal ID plus every provider ID the core
// knows it by, so apps can match against their own data. Enums are camelCase strings on the wire.

/// <summary>Serializer settings matching the API.</summary>
public static class MelodexJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}

/// <summary>Whether an album is in the library.</summary>
public enum AlbumStatus
{
    /// <summary>An edition is in the library with every track.</summary>
    Complete,

    /// <summary>An edition is in the library, but some of its tracks are missing.</summary>
    Partial,

    /// <summary>
    /// No edition is in the library, but every track of one is, on other albums. Only official editions count when the
    /// album has any, so a one-track promo doesn't hide a B-side. Only when duplicate tracks are
    /// fetched once (<see cref="DuplicateTracks.KeepInAlbum"/> or <see cref="DuplicateTracks.KeepInSingle"/>).
    /// </summary>
    Covered,

    /// <summary>Not in the library.</summary>
    Missing,
}

/// <summary>Whether a track is in the library.</summary>
public enum TrackStatus
{
    /// <summary>A file for it is in this album.</summary>
    Owned,

    /// <summary>The same recording is in the library on another album, and duplicates are fetched once.</summary>
    OwnedElsewhere,

    /// <summary>Not in the library.</summary>
    Missing,

    /// <summary>Not in the library, and ignored: it doesn't count against the album.</summary>
    Ignored,

    /// <summary>
    /// Not in the library, on a kind of release the settings show for new songs only (<see cref="ReleaseShowing.NewSongsOnly"/>),
    /// and the song is on a release shown in full or one you have. It doesn't count against the album.
    /// </summary>
    Elsewhere,
}

/// <summary>
/// How to treat a song that's on more than one release: an album and a single, a studio album and a compilation or a
/// live album, a standard and a deluxe edition, and so on.
/// </summary>
public enum DuplicateTracks
{
    /// <summary>Fetch it for each release and keep a copy in each. Every release is judged on its own files.</summary>
    KeepBoth,

    /// <summary>
    /// Fetch it once. Owning it on any release counts for every release it's on, and a song fetched on its own is kept
    /// with the artist's main release that has it: an album before an EP or a single, a studio release before a
    /// compilation or a live one.
    /// </summary>
    KeepInAlbum,

    /// <summary>
    /// Fetch it once, as <see cref="KeepInAlbum"/>, but a song fetched on its own is kept with the smallest release that
    /// has it: a single before an EP or an album.
    /// </summary>
    KeepInSingle,
}

/// <summary>
/// A kind of release the missing view can show or hide: the catalogue's main types, its secondary types (live,
/// compilation, remix...), and bootlegs, which have no official release at all. A release can be several at once, such
/// as a live compilation, and is shown as the strictest of them says.
/// </summary>
public enum ReleaseKind
{
    Album, Ep, Single, Broadcast, Other,
    Live, Compilation, Remix, Demo, Soundtrack, DjMix, Mixtape, Spokenword, Interview, Audiobook, AudioDrama, FieldRecording,
    Bootleg,
}

/// <summary>How the missing view treats a kind of release you don't have. In order from the least to the most strict.</summary>
public enum ReleaseShowing
{
    /// <summary>Listed, with every song on it.</summary>
    Show,

    /// <summary>
    /// Listed only for songs that aren't on a release shown in full or one you have (the same recording, or a song of
    /// the same title by the same artist, leaving aside notes on the version such as "(Live)"); only those count as missing.
    /// </summary>
    NewSongsOnly,

    /// <summary>Not listed.</summary>
    Hide,
}

public static class ReleaseKinds
{
    public static ReleaseKind Of(AlbumType type) => type switch
    {
        AlbumType.Album => ReleaseKind.Album,
        AlbumType.Ep => ReleaseKind.Ep,
        AlbumType.Single => ReleaseKind.Single,
        AlbumType.Broadcast => ReleaseKind.Broadcast,
        _ => ReleaseKind.Other,
    };

    public static ReleaseKind Of(AlbumSecondaryType type) => type switch
    {
        AlbumSecondaryType.Compilation => ReleaseKind.Compilation,
        AlbumSecondaryType.Soundtrack => ReleaseKind.Soundtrack,
        AlbumSecondaryType.Spokenword => ReleaseKind.Spokenword,
        AlbumSecondaryType.Interview => ReleaseKind.Interview,
        AlbumSecondaryType.Audiobook => ReleaseKind.Audiobook,
        AlbumSecondaryType.AudioDrama => ReleaseKind.AudioDrama,
        AlbumSecondaryType.Live => ReleaseKind.Live,
        AlbumSecondaryType.Remix => ReleaseKind.Remix,
        AlbumSecondaryType.DjMix => ReleaseKind.DjMix,
        AlbumSecondaryType.Mixtape => ReleaseKind.Mixtape,
        AlbumSecondaryType.Demo => ReleaseKind.Demo,
        _ => ReleaseKind.FieldRecording,
    };

    /// <summary>Every kind a release is: its main type, each secondary type, and a bootleg if it has no official release.</summary>
    public static IEnumerable<ReleaseKind> Of(AlbumType type, IEnumerable<AlbumSecondaryType> secondary, bool? official)
    {
        yield return Of(type);
        foreach (var kind in secondary)
            yield return Of(kind);
        if (official == false)
            yield return ReleaseKind.Bootleg;
    }

    /// <summary>What the missing view does with a kind until it's set: albums, EPs and singles in full, nothing else.</summary>
    public static ReleaseShowing Default(ReleaseKind kind) =>
        kind is ReleaseKind.Album or ReleaseKind.Ep or ReleaseKind.Single ? ReleaseShowing.Show : ReleaseShowing.Hide;
}

/// <summary>Library-wide settings for matching and the missing view.</summary>
public sealed record LibrarySettings
{
    public DuplicateTracks DuplicateTracks { get; init; } = DuplicateTracks.KeepInAlbum;

    /// <summary>Fuzzy matches under this confidence, from 0 to 1, go to the review list.</summary>
    public double MatchThreshold { get; init; } = 0.85;

    /// <summary>
    /// How many times a scan tries an album while the catalogue source (MusicBrainz) says it's busy. Each busy album
    /// goes to the back of the queue; after the last attempt it waits for the next scan. From 1 to 10.
    /// </summary>
    public int AttemptsWhenBusy { get; init; } = 3;

    /// <summary>
    /// How the missing view treats each kind of release you don't have; a kind left out is as <see cref="ReleaseKinds.Default"/>
    /// says. A release that's several kinds is treated as the strictest of them says.
    /// </summary>
    public IReadOnlyDictionary<ReleaseKind, ReleaseShowing> Showing { get; init; } = new Dictionary<ReleaseKind, ReleaseShowing>();

    /// <summary>
    /// Show song titles, artists and albums as your files' tags have them, rather than as the catalogue names them,
    /// wherever a file has them. Matching and what counts as missing still go by the catalogue.
    /// </summary>
    public bool PreferFileTags { get; init; }

    /// <summary>Show covers from your files (an embedded picture, or one like folder.jpg beside them) rather than the catalogue's, wherever there's one.</summary>
    public bool PreferFileCovers { get; init; }

    /// <summary>Find lyrics for songs in the library that have none, after each scan, through the lyrics plugins.</summary>
    public bool FetchLyrics { get; init; } = true;

    /// <summary>Where lyrics that are found are written.</summary>
    public LyricsStorage LyricsStorage { get; init; } = LyricsStorage.Sidecar;

    // Settings saved before each kind had its own choice, and tags and covers their own switch, are read into the above.
    private IReadOnlyList<AlbumType>? _oldShownTypes;
    private bool? _oldHideSecondary;
    private bool? _oldHideUnofficial;

    /// <summary>Read from older settings only: the main types shown, before <see cref="Showing"/>.</summary>
    [JsonPropertyName("shownTypes")]
    public IReadOnlyList<AlbumType>? OldShownTypes { get => null; init => _oldShownTypes = value; }

    /// <summary>Read from older settings only: hide every secondary type, before <see cref="Showing"/>.</summary>
    [JsonPropertyName("hideSecondaryTypes")]
    public bool? OldHideSecondaryTypes { get => null; init => _oldHideSecondary = value; }

    /// <summary>Read from older settings only: hide bootlegs, before <see cref="Showing"/>.</summary>
    [JsonPropertyName("hideUnofficial")]
    public bool? OldHideUnofficial { get => null; init => _oldHideUnofficial = value; }

    /// <summary>Read from older settings only: both <see cref="PreferFileTags"/> and <see cref="PreferFileCovers"/>.</summary>
    [JsonPropertyName("preferFileMetadata")]
    public bool? OldPreferFileMetadata
    {
        get => null;
        init
        {
            if (value is true)
                PreferFileTags = PreferFileCovers = true;
        }
    }

    /// <summary>How the missing view treats a kind of release.</summary>
    public ReleaseShowing ShowingOf(ReleaseKind kind)
    {
        if (Showing.TryGetValue(kind, out var showing))
            return showing;
        var hidden = kind switch
        {
            ReleaseKind.Album or ReleaseKind.Ep or ReleaseKind.Single or ReleaseKind.Broadcast or ReleaseKind.Other =>
                _oldShownTypes is { } shown ? !shown.Any(t => ReleaseKinds.Of(t) == kind) : (bool?)null,
            ReleaseKind.Bootleg => _oldHideUnofficial,
            _ => _oldHideSecondary,
        };
        return hidden is { } old ? (old ? ReleaseShowing.Hide : ReleaseShowing.Show) : ReleaseKinds.Default(kind);
    }

    /// <summary>How the missing view treats a release that's all these kinds: as the strictest says.</summary>
    public ReleaseShowing ShowingOf(IEnumerable<ReleaseKind> kinds) => kinds.Select(ShowingOf).DefaultIfEmpty().Max();

    /// <summary>
    /// Artists with choices of their own for the missing view, in place of <see cref="Showing"/> for their albums. They're
    /// kept and changed apart from these settings (see <see cref="ArtistShowing"/>), so they aren't sent with them.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<ReleaseKind, ReleaseShowing>> ArtistShowing { get; init; } =
        new Dictionary<Guid, IReadOnlyDictionary<ReleaseKind, ReleaseShowing>>();

    /// <summary>How the missing view treats a kind of release of this artist's: as their own choices say, else as for everyone.</summary>
    public ReleaseShowing ShowingOf(Guid artistId, ReleaseKind kind) =>
        ArtistShowing.TryGetValue(artistId, out var own) && own.TryGetValue(kind, out var showing) ? showing : ShowingOf(kind);

    /// <summary>Whether owning a recording on any album counts for every album it appears on.</summary>
    [JsonIgnore]
    public bool CountsOwnedAnywhere => DuplicateTracks != DuplicateTracks.KeepBoth;
}

/// <summary>An artist in the library, with how complete it is.</summary>
/// <param name="OwnedAlbums">Albums with an edition in the library.</param>
/// <param name="MissingAlbums">Albums the missing view lists, after filters and ignores.</param>
/// <param name="CoveredAlbums">Albums whose tracks are all in the library on other albums.</param>
/// <param name="ImageUrl">A picture of the artist from the artwork plugins, served by the core; a 404 when there's none.</param>
/// <param name="PartialAlbums">Owned albums with tracks missing; included in <paramref name="OwnedAlbums"/>.</param>
/// <param name="Local">An artist only your own files name, no catalogue; see <c>GET /library/local/artists/{id}</c>.</param>
public sealed record LibraryArtist(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    IReadOnlyList<ProviderId> Ids,
    int OwnedAlbums,
    int MissingAlbums,
    int CoveredAlbums,
    int Files,
    string? ImageUrl = null,
    int PartialAlbums = 0,
    bool Local = false);

public sealed record ArtistDetail(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Country,
    IReadOnlyList<ProviderId> Ids,
    bool InLibrary,
    DateTime? DiscographyRefreshedAt,
    IReadOnlyList<AlbumSummary> Albums,
    string? ImageUrl = null)
{
    /// <summary>Its discography is being fetched: look again shortly for the rest.</summary>
    public bool Fetching { get; init; }

    /// <summary>The artist has their own choices of which releases the missing view shows, rather than the ones in Settings.</summary>
    public bool OwnShowing { get; init; }

    /// <summary>Songs of theirs you have that aren't on any album the catalogue lists.</summary>
    public IReadOnlyList<LibraryTrack> Songs { get; init; } = [];
}

/// <summary>Which releases the missing view shows for one artist.</summary>
/// <param name="Own">The artist's own choice for every kind, or null when they follow <paramref name="Default"/>.</param>
/// <param name="Default">The choices in Settings, for every artist without their own.</param>
public sealed record ArtistShowing(IReadOnlyDictionary<ReleaseKind, ReleaseShowing>? Own, IReadOnlyDictionary<ReleaseKind, ReleaseShowing> Default);

/// <param name="HiddenByDefault">The missing view's settings hide it when it isn't owned: its kind is hidden, or shown for new songs only and it has none.</param>
/// <param name="OwnedTracks">Tracks owned (here or elsewhere) on the owned edition, or the best-covered edition.</param>
/// <param name="TotalTracks">Tracks on that edition, less ignored ones you don't have; null until the album's editions have been fetched.</param>
/// <param name="Ignored">The album is ignored, or every track you don't have on it is.</param>
/// <param name="CoverUrl">The cover from the artwork plugins, served by the core: the owned edition's, or the album's. A 404 when there's none.</param>
/// <param name="Local">Music of your own that no catalogue knows, as its files' tags say; see <c>GET /library/local/albums/{id}</c>.</param>
/// <param name="Quality">The quality of its worst song you have: an album's as good as that.</param>
/// <param name="MixedQuality">Its songs aren't all of one quality.</param>
public sealed record AlbumSummary(
    Guid Id,
    Guid ArtistId,
    string Title,
    string ArtistCredit,
    AlbumType Type,
    IReadOnlyList<AlbumSecondaryType> SecondaryTypes,
    string? FirstReleaseDate,
    AlbumStatus Status,
    bool HiddenByDefault,
    bool Ignored,
    int? OwnedTracks,
    int? TotalTracks,
    IReadOnlyList<ProviderId> Ids,
    string? CoverUrl = null,
    bool Local = false,
    FileQuality? Quality = null,
    bool MixedQuality = false);

/// <param name="OwnedEditionId">The edition the library has, if any.</param>
/// <param name="ShownEditionId">The edition whose tracks are listed: the owned one, the requested one, or the best fit.</param>
public sealed record AlbumDetail(
    AlbumSummary Album,
    string ArtistName,
    Guid? OwnedEditionId,
    Guid? ShownEditionId,
    IReadOnlyList<EditionSummary> Editions,
    IReadOnlyList<TrackRow> Tracks)
{
    /// <summary>Its editions are being fetched: look again shortly for them.</summary>
    public bool Fetching { get; init; }
}

public sealed record EditionSummary(
    Guid Id,
    string Title,
    string? ReleaseDate,
    string? Country,
    string? Status,
    string? Disambiguation,
    string? Formats,
    int TrackCount,
    int FilesInLibrary,
    IReadOnlyList<ProviderId> Ids);

/// <param name="FilePath">The library file, path relative to the music folder, when owned on this album.</param>
/// <param name="Ignored">The song is ignored. A track you have still shows as owned.</param>
public sealed record TrackRow(
    Guid TrackId,
    int Disc,
    int Position,
    string Number,
    string Title,
    string ArtistCredit,
    double? DurationSeconds,
    TrackStatus Status,
    string? FilePath,
    IReadOnlyList<ProviderId> Ids,
    bool Ignored = false,
    FileQuality? Quality = null);

public sealed record MissingAlbum(string ArtistName, AlbumSummary Album);

/// <summary>An album you have some or all of.</summary>
public sealed record LibraryAlbum(string ArtistName, AlbumSummary Album);

/// <summary>A song you have: one matched file, or one of your own (<paramref name="Local"/>), whose track ID is its file's.</summary>
/// <param name="AlbumId">The album the file is filed under, when it's matched to one.</param>
/// <param name="Number">Its track number on that album, such as <c>4</c> or <c>A2</c>.</param>
/// <param name="Path">The file, relative to the music folder.</param>
public sealed record LibraryTrack(
    Guid FileId,
    Guid TrackId,
    string Title,
    string ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    int? Disc,
    string? Number,
    double? DurationSeconds,
    string Path,
    string? Format,
    string? CoverUrl,
    bool Local = false,
    FileQuality? Quality = null);

/// <summary>A file that couldn't be matched confidently.</summary>
public sealed record ReviewItem(
    Guid FileId,
    string Path,
    string? Reason,
    double? Confidence,
    string? Title,
    string? Artist,
    string? Album,
    double? DurationSeconds,
    SuggestedTrack? Suggestion,
    bool Excluded = false,
    SetAside SetAside = SetAside.None,
    string? CoverUrl = null,
    DateTime? ModifiedAt = null);

/// <summary>Body of <c>PUT /library/files/{id}/excluded</c>.</summary>
public sealed record ExcludeFile(bool Excluded);

/// <summary>What's been decided about a file that couldn't be matched, instead of matching it.</summary>
public enum SetAside
{
    /// <summary>Nothing: it's matched, or on the review list.</summary>
    None,

    /// <summary>Never matched, but kept in the library as its own tags say.</summary>
    Excluded,

    /// <summary>Off the review list; each scan tries it again, matching it only when it beats the threshold.</summary>
    Deferred,

    /// <summary>Out of the library altogether, though it stays in the music folder.</summary>
    Hidden,
}

/// <summary>The lists on the Review page.</summary>
public enum ReviewList
{
    /// <summary>Files waiting for a decision.</summary>
    Review,

    /// <summary>Excluded files with a title and artist, which the library shows as they are.</summary>
    Excluded,

    /// <summary>Excluded files missing a title or artist, which the library can't show.</summary>
    Incomplete,

    Deferred,
    Hidden,
}

/// <summary>Body of <c>PUT /library/files/{id}/set-aside</c>.</summary>
public sealed record SetFileAside(SetAside As);

/// <summary>Body of <c>POST /library/review/set-aside</c>.</summary>
public sealed record SetFilesAside(IReadOnlyList<Guid> FileIds, SetAside As);

/// <summary>Which files a scan matches again, as well as new and changed ones.</summary>
public enum Rematch
{
    /// <summary>Only new and changed files.</summary>
    None,

    /// <summary>Files on the review list and deferred ones too.</summary>
    Unmatched,

    /// <summary>Every file, except ones you matched, excluded or hid yourself.</summary>
    All,
}

/// <summary>Body of <c>POST /library/scan</c>; it can be left out.</summary>
public sealed record ScanLibrary(Rematch Rematch = Rematch.None);

/// <summary>Body of <c>POST /library/review/approve</c> and <c>/library/review/exclude</c>: the files to act on.</summary>
public sealed record ReviewFiles(IReadOnlyList<Guid> FileIds);

/// <summary>How many of the files a bulk review action changed.</summary>
public sealed record ReviewDone(int Count);

/// <summary>Body of <c>POST /library/files/{id}/match</c>. Without an edition, the suggestion's or the likeliest one is used.</summary>
public sealed record MatchFile(Guid TrackId, Guid? EditionId = null);

/// <param name="CoverUrl">The suggested edition's cover, served by the core.</param>
public sealed record SuggestedTrack(Guid TrackId, string Title, string ArtistCredit, double? DurationSeconds, Guid? EditionId, Guid? AlbumId, string? AlbumTitle,
    string? CoverUrl = null, Guid? ArtistId = null);

public sealed record SearchResults(
    IReadOnlyList<SearchArtist> Artists,
    IReadOnlyList<AlbumSummary> Albums,
    IReadOnlyList<SearchTrack> Tracks);

/// <param name="ImageUrl">A picture of the artist, served by the core; a 404 when there's none.</param>
/// <param name="Local">An artist only your own files name, as their tags say: open them at <c>/library/local/artists/{id}</c>.</param>
public sealed record SearchArtist(Guid Id, string Name, string? Disambiguation, bool InLibrary, IReadOnlyList<ProviderId> Ids, string? ImageUrl = null,
    bool Local = false);

/// <param name="AlbumId">The album the track is best known from, when the catalogue says.</param>
/// <param name="CoverUrl">That album's cover, served by the core.</param>
public sealed record SearchTrack(Guid Id, string Title, string ArtistCredit, double? DurationSeconds, bool Owned, IReadOnlyList<ProviderId> Ids,
    Guid? ArtistId = null, Guid? AlbumId = null, string? AlbumTitle = null, string? CoverUrl = null);

public enum TaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <param name="Title">What it's working on, such as "Library scan"; filled in by <c>GET /tasks</c>.</param>
/// <param name="Paused">Held back: it won't start until resumed. A paused scan picks up where it stopped.</param>
/// <param name="QueueOrder">Its place among queued tasks: lower runs first.</param>
public sealed record TaskInfo(Guid Id, string Kind, TaskState State, string? Progress, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
    string? Title = null, bool Paused = false, double QueueOrder = 0)
{
    public bool Active => State is TaskState.Queued or TaskState.Running;

    /// <summary>When it last changed: finished, started or queued.</summary>
    public DateTime UpdatedAt => FinishedAt ?? StartedAt ?? CreatedAt;
}

/// <param name="LastScan">The most recent finished scan, successful or not: its progress says what it found, its error why it failed.</param>
/// <param name="Reading">A scan is reading and matching files, or about to: new requests are refused and queued ones wait until it's done.</param>
/// <param name="Files">Files in the library: every file in the music folder except hidden ones.</param>
/// <param name="Excluded">Files excluded from matching; they're counted in <paramref name="Files"/> but not under review.</param>
/// <param name="Deferred">Files put off, which each scan tries again; counted in <paramref name="Files"/> but not under review.</param>
/// <param name="Hidden">Files hidden from the library, not counted in <paramref name="Files"/>.</param>
/// <param name="Preparing">
/// Melodex has just started and is working out the library's lists of artists, albums and tracks for the first time;
/// they take a while on a big library, and come back once they're ready.
/// </param>
public sealed record LibraryStatus(int Files, int Matched, int Review, int Pending, DateTime? LastScanAt, TaskInfo? CurrentTask, TaskInfo? LastScan = null,
    bool Reading = false, int Excluded = 0, int Deferred = 0, int Hidden = 0, bool Preparing = false);

public enum LogEntryLevel
{
    Information,
    Warning,
    Error,
    /// <summary>Information about something that finished well, such as a scan.</summary>
    Success,
}

/// <summary>One event in the activity log.</summary>
/// <param name="Seq">Increases by one per entry; pass the latest you have as <c>after</c> to get only newer ones.</param>
/// <param name="Source">The part of Melodex it came from, such as LibraryScanner or LibraryMatcher.</param>
/// <param name="Error">The underlying error's message, if there was one.</param>
public sealed record LogEntry(long Seq, DateTime Time, LogEntryLevel Level, string Source, string Message, string? Error);

/// <summary>How long the activity log is kept.</summary>
public sealed record LogSettings
{
    /// <summary>Days of entries the Log page shows, from 1 to 90.</summary>
    public int MemoryDays { get; init; } = 7;

    /// <summary>Days the backup files under <c>data/logs</c> are kept, from 1 to 3650; at least <see cref="MemoryDays"/>.</summary>
    public int FileDays { get; init; } = 30;
}

/// <param name="Latest">The newest entry's sequence number, even when no entries are returned.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long Latest);

/// <summary>Where a file is in the current scan.</summary>
public enum ScanFileState
{
    /// <summary>Its tags are being read.</summary>
    Reading,

    /// <summary>Being matched.</summary>
    Matching,

    /// <summary>Matched to a song; the song's details are filled in.</summary>
    Matched,

    /// <summary>No confident match; it's on the Review page.</summary>
    Review,

    /// <summary>The catalogue source was busy; it's tried again later in the scan, or next scan.</summary>
    Waiting,

    /// <summary>Read, and waiting its turn to be matched.</summary>
    Queued,
}

/// <summary>A file the running (or last) scan has read or matched, from <c>GET /library/scan/files</c>.</summary>
/// <param name="Seq">When it last changed: pass the highest seen as <c>since</c> to get only later changes.</param>
/// <param name="Order">Its place in the list, which it keeps as it's read and matched.</param>
/// <param name="Path">Relative to the music folder.</param>
/// <param name="Title">The song once matched; before that, the file's title tag if it has one.</param>
/// <param name="Detail">Why it needs review, or what it's waiting for.</param>
public sealed record ScanFile(
    long Seq,
    long Order,
    string Path,
    ScanFileState State,
    string? Title,
    string? ArtistCredit,
    Guid? ArtistId,
    string? AlbumTitle,
    Guid? AlbumId,
    string? CoverUrl,
    string? Detail,
    DateTime At);

/// <summary>The scan's files that changed since a sequence number, from <c>GET /library/scan/files</c>.</summary>
/// <param name="Scan">Which scan they belong to. When it changes, a new scan has started: start the list again.</param>
/// <param name="Latest">The newest change's sequence number, to pass as <c>since</c> next time.</param>
/// <param name="Files">In list order.</param>
public sealed record ScanFeedPage(Guid Scan, long Latest, IReadOnlyList<ScanFile> Files);

/// <summary>What to remove from the library.</summary>
public enum LibraryItemKind
{
    /// <summary>One file: a row of the library's track list.</summary>
    File,

    /// <summary>Every file of an album, whichever edition.</summary>
    Album,

    /// <summary>Every file credited to an artist or filed under one of their albums.</summary>
    Artist,
}

/// <summary>What removing something from the library would affect, from <c>GET /library/removal</c>.</summary>
/// <param name="Files">How many files.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Folders">The folders they're in, relative to the music folder; up to 20.</param>
/// <param name="CanDelete">Whether the files can be deleted: the import worker, which writes the music folder, is working.</param>
/// <param name="CantDeleteReason">Why not, when they can't.</param>
public sealed record RemovalPreview(int Files, long Bytes, IReadOnlyList<string> Folders, bool CanDelete, string? CantDeleteReason = null);

/// <summary>Removes an artist, album or file from the library, for <c>POST /library/remove</c>.</summary>
/// <param name="DeleteFiles">
/// Delete the files from the music folder, and any folder left with nothing but artwork and the like. Otherwise the
/// files stay where they are, hidden, so Melodex no longer counts them; they can be shown again from the Review
/// page.
/// </param>
public sealed record RemoveFromLibrary(LibraryItemKind Kind, Guid Id, bool DeleteFiles);

/// <param name="Removed">Files deleted, or hidden when keeping them.</param>
/// <param name="Failed">Files that couldn't be deleted, with why; they stay in the library.</param>
public sealed record RemovalResult(int Removed, IReadOnlyList<string> Failed);

/// <summary>A song's page: the recording, the albums it's on, and its files in the library.</summary>
/// <param name="CoverUrl">The cover of the album its file is on, or of the first album it's on.</param>
public sealed record TrackDetail(
    Guid Id,
    string Title,
    string ArtistCredit,
    Guid ArtistId,
    string ArtistName,
    double? DurationSeconds,
    bool Ignored,
    IReadOnlyList<ProviderId> Ids,
    string? CoverUrl,
    IReadOnlyList<TrackAppearance> Appearances,
    IReadOnlyList<TrackFile> Files);

/// <summary>An edition the song is on, and where on it.</summary>
/// <param name="Title">The song's title on this edition, which can differ from the recording's.</param>
/// <param name="InLibrary">A file of the song is filed under this edition.</param>
/// <param name="AlbumStatus">How much of the album you have, as its artist's page shows it.</param>
public sealed record TrackAppearance(
    Guid AlbumId,
    string AlbumTitle,
    AlbumType AlbumType,
    Guid EditionId,
    string EditionTitle,
    string? ReleaseDate,
    string? Country,
    int Disc,
    string Number,
    string Title,
    bool InLibrary,
    AlbumStatus AlbumStatus);

/// <summary>A file of the song in the library, and the tags it has.</summary>
/// <param name="Path">Relative to the music folder.</param>
/// <param name="FormattedAt">When a format last rewrote it.</param>
/// <param name="Lyrics">What lyrics it has, in an .lrc file beside it or in its tags.</param>
public sealed record TrackFile(
    Guid Id,
    string Path,
    string? Format,
    double? DurationSeconds,
    long Size,
    Guid? AlbumId,
    Guid? EditionId,
    IReadOnlyList<FileTag> Tags,
    DateTime? FormattedAt,
    LyricsKind Lyrics = LyricsKind.None,
    FileQuality? Quality = null);

/// <summary>What lyrics a song has.</summary>
public enum LyricsKind
{
    /// <summary>None, or none found yet.</summary>
    None,

    /// <summary>The words, without timings.</summary>
    Plain,

    /// <summary>The words, each line with when it's sung, to follow along.</summary>
    Synced,

    /// <summary>A lyrics source said it has no words.</summary>
    Instrumental,
}

/// <summary>Where lyrics found for the library are written.</summary>
public enum LyricsStorage
{
    /// <summary>An .lrc file beside each song, with the same name. Most players read these, and the music files aren't changed.</summary>
    Sidecar,

    /// <summary>The lyrics tag in the music file itself.</summary>
    Embedded,

    /// <summary>Both.</summary>
    Both,
}

/// <summary>A line of lyrics.</summary>
/// <param name="Seconds">When it's sung, for synced lyrics; null for plain ones.</param>
public sealed record LyricLine(double? Seconds, string Text);

/// <summary>A song's lyrics, as its file in the library has them.</summary>
/// <param name="Lines">Synced lyrics in the order they're sung, or plain ones in order. Empty when there are none.</param>
/// <param name="Source">Where they were read from: <c>sidecar</c> (an .lrc file) or <c>embedded</c> (the file's tags).</param>
/// <param name="Text">The lyrics as they're stored, LRC or plain text, to edit.</param>
/// <param name="Excluded">Whether the song is excluded from lyrics matching, so no scan looks for its lyrics.</param>
/// <param name="CheckedAt">When the lyrics sources were last asked and had none.</param>
/// <param name="Instrumental">Whether a lyrics source said it has no words.</param>
public sealed record TrackLyrics(LyricsKind Kind, IReadOnlyList<LyricLine> Lines, string? Source, string? Text = null, bool Excluded = false,
    DateTime? CheckedAt = null, bool Instrumental = false);

/// <summary>Body of <c>PUT /library/tracks/{id}/lyrics</c>: the lyrics to write, LRC or plain text.</summary>
public sealed record SaveLyrics(string Text);

/// <summary>Lyrics a lyrics plugin found for someone choosing a song's lyrics by hand.</summary>
/// <param name="Source">The plugin's name.</param>
/// <param name="Text">The lyrics, synced (LRC) where the source has them, else plain.</param>
public sealed record LyricsChoice(string Source, string Title, string Artist, string? Album, double? DurationSeconds, LyricsKind Kind, string Text);

/// <summary>A tag as read from a file, with a name to show.</summary>
public sealed record FileTag(string Name, string Value);

/// <summary>An artist only your own files name: excluded files, shown as their tags say.</summary>
public sealed record LocalArtistDetail(Guid Id, string Name, IReadOnlyList<AlbumSummary> Albums, string? ImageUrl);

/// <summary>An album of your own files, as their tags say.</summary>
/// <param name="ArtistId">The artist: a catalogue artist with the same name if the library has one, else the local artist.</param>
/// <param name="ArtistLocal">Whether <paramref name="ArtistId"/> is a local artist rather than a catalogue one.</param>
public sealed record LocalAlbumDetail(Guid Id, string Title, Guid ArtistId, string ArtistName, bool ArtistLocal, string? Date, string? CoverUrl,
    IReadOnlyList<LocalTrack> Tracks);

public sealed record LocalTrack(Guid FileId, int? Disc, int? Number, string Title, string Artist, double? DurationSeconds, string Path, string? Format,
    FileQuality? Quality = null);
