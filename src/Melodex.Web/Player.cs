using System.Runtime.CompilerServices;
using Microsoft.JSInterop;
using Melodex.Client;

namespace Melodex.Web;

/// <summary>A track in the player's queue: one you have, or one fetched to listen to.</summary>
public sealed class PlayerEntry
{
    public required Guid TrackId { get; init; }
    public Guid? EditionId { get; init; }
    public required string Title { get; init; }
    public required string ArtistCredit { get; init; }
    public Guid? ArtistId { get; init; }
    public Guid? AlbumId { get; init; }
    public string? AlbumTitle { get; init; }
    public string? CoverUrl { get; init; }
    public double? DurationSeconds { get; set; }

    /// <summary>A file of your own that no catalogue knows: it's played as the file, and its album is a local one.</summary>
    public Guid? FileId { get; init; }

    /// <summary>The listen-now track, for a track you don't have.</summary>
    public Guid? ListenId { get; set; }

    /// <summary>
    /// A track you don't have, put in the queue with its album: it's only fetched to listen to once it's near the song
    /// playing, so playing one song of a long album doesn't fetch all of it (#174).
    /// </summary>
    public bool Missing { get; init; }

    /// <summary>Being asked for now, so it isn't asked for twice.</summary>
    internal bool Asking { get; set; }

    public ListenNowState? ListenState { get; set; }

    /// <summary>What it was played from, for listening history.</summary>
    public PlaySource? From { get; set; }

    /// <summary>Why it can't be played, or what's happening to it.</summary>
    public string? Detail { get; set; }

    /// <summary>It failed, expired or the browser can't play it, so the queue skips it.</summary>
    public bool Unplayable { get; set; }

    public bool Waiting => ListenState is ListenNowState.Requested or ListenNowState.Fetching || Missing && ListenId is null && !Unplayable;

    /// <summary>A track you don't have, not asked for yet.</summary>
    public bool Unfetched => Missing && ListenId is null && !Unplayable;

    public static PlayerEntry Owned(TrackRow track, AlbumSummary album, Guid? editionId) => new()
    {
        TrackId = track.TrackId, EditionId = editionId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    public static PlayerEntry ToFetch(TrackRow track, AlbumSummary album, Guid? editionId) => new()
    {
        TrackId = track.TrackId, EditionId = editionId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds, Missing = true,
    };

    public static PlayerEntry Owned(SearchTrack track) => new()
    {
        TrackId = track.Id, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = track.ArtistId,
        AlbumId = track.AlbumId, AlbumTitle = track.AlbumTitle, CoverUrl = track.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    public static PlayerEntry Owned(LibraryTrack track) => new()
    {
        TrackId = track.TrackId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = track.ArtistId,
        AlbumId = track.AlbumId, AlbumTitle = track.AlbumTitle, CoverUrl = track.CoverUrl, DurationSeconds = track.DurationSeconds,
        FileId = track.Local ? track.FileId : null,
    };

    public static PlayerEntry Owned(LocalTrack track, LocalAlbumDetail album) => new()
    {
        TrackId = track.FileId, FileId = track.FileId, Title = track.Title, ArtistCredit = track.Artist, ArtistId = album.ArtistLocal ? null : album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds,
    };

    /// <summary>Where its album's page is.</summary>
    /// <summary>What a playlist keeps of it: the song, or a file of your own; nothing for one only listened to.</summary>
    public IReadOnlyList<PlaylistItem>? Keep => ListenId is not null || Missing ? null : FileId is { } file ? [new PlaylistItem(FileId: file)] : [new PlaylistItem(TrackId)];

    public string? ArtistHref => ArtistId is { } artist ? $"artists/{artist}" : null;

    public string? AlbumHref => AlbumId is { } album ? $"{(FileId is null ? "albums" : "local/albums")}/{album}" : null;

    /// <summary>A song a radio or the discovery plugins found: played from the library if you have it, else fetched when it's near.</summary>
    public static PlayerEntry Discovered(DiscoveredSong song) => new()
    {
        TrackId = song.TrackId, Title = song.Title, ArtistCredit = song.ArtistCredit, ArtistId = song.ArtistId, AlbumId = song.AlbumId,
        AlbumTitle = song.AlbumTitle, CoverUrl = song.CoverUrl, DurationSeconds = song.DurationSeconds, Missing = !song.Owned,
    };

    /// <summary>The radio it was queued by: what that was started from.</summary>
    public Guid? RadioSeed => From is { Kind: PlaySourceKind.Radio, Id: { } seed } ? seed : null;

    public static PlayerEntry Listening(ListenNowInfo item) => new()
    {
        TrackId = item.TrackId, EditionId = item.EditionId, Title = item.Title, ArtistCredit = item.ArtistCredit, ArtistId = item.ArtistId,
        AlbumId = item.AlbumId, AlbumTitle = item.AlbumTitle, CoverUrl = item.CoverUrl, DurationSeconds = item.DurationSeconds,
        ListenId = item.Id, ListenState = item.State, Detail = item.Detail,
    };

    internal void Update(ListenNowInfo item)
    {
        ListenState = item.State;
        Detail = item.Detail;
        DurationSeconds ??= item.DurationSeconds;
        if (item.State is ListenNowState.Failed or ListenNowState.Expired)
            Unplayable = true;
    }
}

/// <summary>What the full-screen player shows beside the cover.</summary>
public enum PlayerPanel
{
    None,
    Lyrics,
    Queue,
}

/// <summary>
/// The queue and the audio element behind the player bar. Tracks you have stream from the music folder; tracks you
/// don't are fetched to listen to, and the queue waits at one until it's ready.
/// </summary>
/// <summary>What happens at the end: stop, go round the queue again, or play the same song again.</summary>
public enum RepeatMode { Off, All, One }

public sealed class Player : IAsyncDisposable
{
    private readonly MelodexClient _api;
    private readonly IJSRuntime _js;
    private readonly ServerEvents _events;
    private readonly Preferences _prefs;
    private readonly List<PlayerEntry> _queue = [];

    // The queue in the order it was given, while it's shuffled, so turning shuffle off puts it back.
    private List<PlayerEntry>? _ordered;
    private bool _modesLoaded;

    // Whose queue it is, so it's kept for them in this browser; and where to pick up a song from after a reload.
    private Guid? _owner;
    private double? _resumeAt;
    // How long the song playing is, as the server measured its file: kept over the browser's own idea, which Safari gets
    // wrong for files that don't say up front (#93).
    private double? _length;
    private int _savedSecond = -1;
    private IJSObjectReference? _audio;
    private DotNetObjectReference<Player>? _self;
    private Timer? _poll;
    private int _starting;

    public Player(MelodexClient api, IJSRuntime js, ServerEvents events, Preferences prefs)
    {
        _api = api;
        _js = js;
        _events = events;
        _prefs = prefs;
        events.Received += OnEvent;
        events.EnsureStarted();
        Changed += () => _ = SaveAsync();
        Changed += () => _ = PrepareNextAsync();
        Changed += () => _ = WarmAroundAsync();
        Changed += () => _ = FetchAroundAsync();
        Changed += () => _ = TopUpAsync();
    }

    /// <summary>The queue changed, or what's playing did.</summary>
    public event Action? Changed;

    /// <summary>The position moved; separate from <see cref="Changed"/> so only the seek bar redraws every second.</summary>
    public event Action? TimeChanged;

    public IReadOnlyList<PlayerEntry> Queue => _queue;
    public int Current { get; private set; } = -1;

    /// <summary>The songs before the one playing, kept so they can be gone back to.</summary>
    public int Played => Math.Max(Current, 0);

    /// <summary>The songs still to play: the one playing and those after it.</summary>
    public int Left => _queue.Count - Played;
    public PlayerEntry? Now => Current >= 0 && Current < _queue.Count ? _queue[Current] : null;

    /// <summary>What's to come is in a random order. Remembered in this browser.</summary>
    public bool Shuffle { get; private set; }

    /// <summary>Remembered in this browser.</summary>
    public RepeatMode Repeat { get; private set; }

    /// <summary>There's a song to go on to: a later one, or the first again when repeating.</summary>
    public bool HasNext => Current + 1 < _queue.Count || Repeat != RepeatMode.Off && _queue.Count > 0;
    public bool Playing { get; private set; }
    public double Position { get; private set; }
    public double Duration { get; private set; }

    /// <summary>Something went wrong starting playback, such as nothing to fetch the tracks from.</summary>
    public string? Error { get; private set; }

    /// <summary>The full-screen player is open.</summary>
    public bool Expanded { get; private set; }

    /// <summary>What the full-screen player shows beside the cover.</summary>
    public PlayerPanel Panel { get; private set; } = PlayerPanel.None;

    /// <summary>Opens the full-screen player, showing a panel beside the cover if asked.</summary>
    public void Expand(PlayerPanel? panel = null)
    {
        Expanded = true;
        if (panel is { } p)
            Panel = p;
        Changed?.Invoke();
    }

    public void Collapse()
    {
        Expanded = false;
        Changed?.Invoke();
    }

    /// <summary>Shows the panel, or hides it if it's showing.</summary>
    public void TogglePanel(PlayerPanel panel)
    {
        Panel = Panel == panel ? PlayerPanel.None : panel;
        Changed?.Invoke();
    }

    /// <summary>The audio element's script, for the full-screen player's visualiser and lyrics.</summary>
    public Task<IJSObjectReference> ScriptAsync() => AudioAsync();

    /// <summary>The volume, from 0 to 1, kept per browser.</summary>
    public async Task<double> GetVolumeAsync() => await (await AudioAsync()).InvokeAsync<double>("getVolume");

    public async Task SetVolumeAsync(double volume) => await (await AudioAsync()).InvokeVoidAsync("setVolume", volume);

    // Each song's lyrics, once asked for.
    private readonly Dictionary<(Guid TrackId, Guid? FileId), TrackLyrics> _lyrics = new();

    /// <summary>
    /// The lyrics of what's playing, as its file in the library has them; none for a song only fetched to listen to.
    /// </summary>
    /// <returns>Null while a song fetched to listen to is still having its lyrics looked for, or isn't ready yet.</returns>
    public async Task<TrackLyrics?> LyricsAsync(PlayerEntry entry, bool fresh = false)
    {
        var key = (entry.TrackId, entry.FileId);
        if (!fresh && _lyrics.TryGetValue(key, out var known))
            return known;
        var none = new TrackLyrics(LyricsKind.None, [], null);
        TrackLyrics lyrics;
        try
        {
            if (entry.FileId is { } file)
                lyrics = await _api.GetFileLyricsAsync(file);
            else if (entry.ListenState is null or ListenNowState.InLibrary)
                lyrics = await _api.GetTrackLyricsAsync(entry.TrackId);
            // Looked for once it's ready to play: until then, and while they're looked for, they're on their way.
            else if (entry.ListenId is not { } listen || entry.ListenState is not (ListenNowState.Playable or ListenNowState.Importing))
                return entry.ListenState is ListenNowState.Failed or ListenNowState.Expired ? none : null;
            else if (await _api.GetListeningLyricsAsync(listen) is { } found)
                lyrics = found;
            else
                return null;
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            lyrics = none;
        }
        return _lyrics[key] = lyrics;
    }

    /// <summary>Remembers lyrics just found, so the player shows them.</summary>
    public void SetLyrics(PlayerEntry entry, TrackLyrics lyrics) => _lyrics[(entry.TrackId, entry.FileId)] = lyrics;

    /// <summary>Whether this track is the one playing, or waiting to.</summary>
    public bool IsNow(Guid trackId) => Now?.TrackId == trackId;

    // ---- Starting things ----

    /// <summary>
    /// Plays an album in order from a track: tracks you have from the library, missing ones fetched to listen to.
    /// Playback starts as soon as the first is ready.
    /// </summary>
    public async Task PlayAlbumAsync(AlbumDetail album, Guid? fromTrack = null)
    {
        var (entries, start) = await AlbumEntriesAsync(album, fromTrack);
        await PlayAsync(entries, start);
    }

    /// <summary>Adds an album to the end of the queue, or next, fetching tracks you don't have to listen to.</summary>
    public async Task QueueAlbumAsync(AlbumDetail album, bool next = false) => await AddAsync((await AlbumEntriesAsync(album, null)).Entries, next);

    /// <summary>Adds an album from a list to the end of the queue, or next, loading its tracks first.</summary>
    public async Task QueueAlbumAsync(Guid albumId, bool next = false)
    {
        if (await _api.GetAlbumAsync(albumId) is { } album)
            await QueueAlbumAsync(album, next);
        else if (await _api.GetLocalAlbumAsync(albumId) is { } own)
            await AddAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList(), next, new PlaySource(PlaySourceKind.Album, own.Id, own.Title));
    }

    /// <summary>
    /// The album's tracks in order: tracks you have, and the missing ones, fetched to listen to as they come near the
    /// song playing.
    /// </summary>
    private Task<(List<PlayerEntry> Entries, int Start)> AlbumEntriesAsync(AlbumDetail album, Guid? fromTrack)
    {
        Error = null;
        var tracks = album.Tracks.Where(t => t.Status is TrackStatus.Owned or TrackStatus.OwnedElsewhere or TrackStatus.Missing).ToList();
        var entries = new List<PlayerEntry>();
        var start = 0;
        foreach (var track in tracks)
        {
            if (track.TrackId == fromTrack)
                start = entries.Count;
            entries.Add(track.Status == TrackStatus.Missing
                ? PlayerEntry.ToFetch(track, album.Album, album.ShownEditionId)
                : PlayerEntry.Owned(track, album.Album, album.ShownEditionId));
        }
        Tag(entries, new PlaySource(PlaySourceKind.Album, album.Album.Id, album.Album.Title));
        return Task.FromResult((entries, start));
    }

    // How far either side of the song playing missing songs are fetched to listen to, from the fetch settings.
    private (int Ahead, int Behind)? _window;
    private DateTime _windowAt;

    /// <summary>
    /// Asks for the missing songs near the one playing to be fetched to listen to: the one playing first, then the few
    /// after and before it, as the fetch settings say. Others wait until the queue comes near them.
    /// </summary>
    private async Task FetchAroundAsync()
    {
        if (Current < 0 || !_queue.Any(e => e.Unfetched))
            return;
        if (_window is null || DateTime.UtcNow - _windowAt > TimeSpan.FromMinutes(10))
        {
            _windowAt = DateTime.UtcNow;
            try
            {
                var fetch = await _api.GetFetchSettingsAsync();
                _window = (fetch.ListenNowAhead, fetch.ListenNowBehind);
            }
            catch (Exception e) when (e is HttpRequestException or MelodexApiException)
            {
                _window ??= (3, 2);
            }
        }
        var (ahead, behind) = _window.Value;
        var near = Enumerable.Range(0, ahead + 1).Select(i => Current + i)
            .Concat(Enumerable.Range(1, behind).Select(i => Current - i))
            .Where(i => i >= 0 && i < _queue.Count)
            .Select(i => _queue[i])
            .Where(e => e.Unfetched && !e.Asking)
            .ToList();
        if (near.Count == 0)
            return;
        foreach (var entry in near)
            entry.Asking = true;
        foreach (var entry in near)
            await AskAsync(entry);
        Changed?.Invoke();
        // The song playing may have been waiting on this.
        if (Now is { } now && near.Contains(now))
            await StartAsync();
    }

    private async Task AskAsync(PlayerEntry entry)
    {
        try
        {
            var items = await _api.StartListeningAsync(new StartListen(RequestKind.Track, entry.TrackId, entry.EditionId));
            if (items.FirstOrDefault(i => i.TrackId == entry.TrackId) is { } item)
            {
                entry.ListenId = item.Id;
                entry.Update(item);
            }
            else
            {
                entry.Unplayable = true;
                entry.Detail = "It couldn't be fetched.";
            }
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            entry.Unplayable = true;
            entry.Detail = e.Message;
        }
        finally
        {
            entry.Asking = false;
        }
    }

    /// <summary>Plays an album from a list, loading its tracks first. Albums of your own files are found too.</summary>
    public async Task PlayAlbumAsync(Guid albumId)
    {
        if (await _api.GetAlbumAsync(albumId) is { } album)
            await PlayAlbumAsync(album);
        else if (await _api.GetLocalAlbumAsync(albumId) is { } own)
            await PlayAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList(), from: new PlaySource(PlaySourceKind.Album, own.Id, own.Title));
    }

    /// <summary>Fetches a track you don't have to listen to, and plays it now or after the queue.</summary>
    public async Task ListenAsync(Guid trackId, Guid? editionId = null, bool next = false)
    {
        Error = null;
        try
        {
            var items = await _api.StartListeningAsync(new StartListen(RequestKind.Track, trackId, editionId));
            var entries = items.Select(PlayerEntry.Listening).ToList();
            await (next ? AddAsync(entries) : PlayAsync(entries));
        }
        catch (MelodexApiException e)
        {
            Error = e.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>Fetches a track you don't have to listen to, and puts it next in the queue or last.</summary>
    public async Task QueueListenAsync(Guid trackId, bool next, Guid? editionId = null)
    {
        Error = null;
        try
        {
            var items = await _api.StartListeningAsync(new StartListen(RequestKind.Track, trackId, editionId));
            await AddAsync(items.Select(PlayerEntry.Listening).ToList(), next);
        }
        catch (MelodexApiException e)
        {
            Error = e.Message;
            Changed?.Invoke();
        }
    }

    // ---- Radio (#4) ----

    /// <summary>How many songs a radio keeps queued after the one playing, and how many it asks for at a time.</summary>
    private const int RadioAhead = 3, RadioTake = 5;

    /// <summary>A radio's song skipped this soon counts against it and songs like it.</summary>
    private const double QuickSkip = 30;

    private bool _toppingUp;
    private RadioSettings? _radioSettings;
    private DateTime _radioSettingsAt;
    // Songs a radio couldn't be started from (a file of your own no catalogue knows, say), so autoplay doesn't keep asking.
    private readonly HashSet<Guid> _noRadio = [];

    /// <summary>
    /// Replaces the queue with a radio started from a song, album, artist or playlist, and plays it. It keeps itself
    /// topped up as it plays, and never runs out.
    /// </summary>
    public async Task StartRadioAsync(Guid seedId)
    {
        Error = null;
        try
        {
            if (await _api.NextRadioAsync(new RadioNext(seedId, [], RadioTake)) is not { Songs.Count: > 0 } batch)
            {
                Error = "There's nothing to make a radio from yet.";
                Changed?.Invoke();
                return;
            }
            await PlayAsync(batch.Songs.Select(PlayerEntry.Discovered).ToList(), from: RadioSource(seedId, batch));
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            Error = e.Message;
            Changed?.Invoke();
        }
    }

    private static PlaySource RadioSource(Guid seedId, RadioBatch batch) => new(PlaySourceKind.Radio, seedId, $"{batch.Name} radio");

    /// <summary>
    /// Keeps a radio a few songs ahead of the one playing. A queue that isn't a radio, on its last song with nothing
    /// repeating, carries on as one from that song when autoplay is on.
    /// </summary>
    private async Task TopUpAsync()
    {
        if (_toppingUp || _owner is null || Now is null || _queue.Count - Current - 1 >= RadioAhead)
            return;
        var last = _queue[^1];
        Guid seed;
        if (last.RadioSeed is { } radio)
            seed = radio;
        else if (Current == _queue.Count - 1 && Repeat == RepeatMode.Off && !_noRadio.Contains(last.TrackId) && last.FileId is null
                 && (await RadioSettingsAsync()).Autoplay)
            seed = last.TrackId;
        else
            return;
        _toppingUp = true;
        try
        {
            // What this radio has queued, so none comes again; the latest steer what's next.
            var recent = _queue.Where(e => e.RadioSeed == seed).Select(e => e.TrackId).TakeLast(100).ToList();
            if (last.RadioSeed is null)
                recent.Add(last.TrackId);
            var batch = await _api.NextRadioAsync(new RadioNext(seed, recent, RadioTake));
            if (batch is not { Songs.Count: > 0 })
            {
                _noRadio.Add(seed);
                return;
            }
            if (_queue.Count == 0 || !ReferenceEquals(_queue[^1], last))
                return;
            await AddAsync(batch.Songs.Select(PlayerEntry.Discovered).ToList(), from: RadioSource(seed, batch));
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            // Tried again with the next change.
        }
        finally
        {
            _toppingUp = false;
        }
    }

    /// <summary>This person's radio settings, asked for again every so often.</summary>
    public async Task<RadioSettings> RadioSettingsAsync()
    {
        if (_radioSettings is null || DateTime.UtcNow - _radioSettingsAt > TimeSpan.FromMinutes(10))
        {
            _radioSettingsAt = DateTime.UtcNow;
            try
            {
                _radioSettings = await _api.GetRadioSettingsAsync();
            }
            catch (Exception e) when (e is HttpRequestException or MelodexApiException)
            {
                _radioSettings ??= new RadioSettings(Autoplay: false);
            }
        }
        return _radioSettings;
    }

    /// <summary>The radio settings were changed here: they count at once.</summary>
    public void RadioSettingsChanged(RadioSettings settings) => (_radioSettings, _radioSettingsAt) = (settings, DateTime.UtcNow);

    /// <summary>A radio's song skipped in its first half minute: radios steer away from it and songs like it.</summary>
    private void NoteSkip()
    {
        if (_owner is not null && Now is { RadioSeed: not null, Waiting: false, Unplayable: false } now && Playing && Position < QuickSkip
            && Duration - Position > 5)
            _ = TellAsync(() => _api.SkipRadioSongAsync(now.TrackId));
    }

    /// <summary>
    /// Replaces the queue and starts playing it. When shuffling, the song started from comes first and the rest follow
    /// in a random order; started from the beginning, all of it is in a random order.
    /// </summary>
    public async Task PlayAsync(IReadOnlyList<PlayerEntry> entries, int start = 0, PlaySource? from = null)
    {
        await LoadModesAsync();
        Tag(entries, from);
        _queue.Clear();
        _queue.AddRange(entries);
        Current = entries.Count == 0 ? -1 : Math.Clamp(start, 0, entries.Count - 1);
        _ordered = null;
        _resumeAt = null;
        // A radio comes in its own order: what it was started from first.
        if (Shuffle && Current >= 0 && from?.Kind != PlaySourceKind.Radio)
        {
            _ordered = [.. _queue];
            var first = start > 0 ? _queue[Current] : null;
            var rest = _queue.Where(e => e != first).OrderBy(_ => Random.Shared.Next()).ToList();
            _queue.Clear();
            if (first is not null)
                _queue.Add(first);
            _queue.AddRange(rest);
            Current = 0;
        }
        await StartAsync();
    }

    /// <summary>Adds to the end of the queue, or after what's playing, starting it if nothing's playing.</summary>
    public async Task AddAsync(IReadOnlyList<PlayerEntry> entries, bool next = false, PlaySource? from = null)
    {
        await LoadModesAsync();
        Tag(entries, from);
        if (next && Now is { } now)
        {
            _queue.InsertRange(Current + 1, entries);
            _ordered?.InsertRange(_ordered.IndexOf(now) + 1, entries);
            Changed?.Invoke();
            return;
        }
        _queue.AddRange(entries);
        _ordered?.AddRange(entries);
        if (Now is null && entries.Count > 0)
        {
            Current = _queue.Count - entries.Count;
            await StartAsync();
            return;
        }
        Changed?.Invoke();
    }

    // Where the songs were played from, for those that don't already say.
    private static void Tag(IReadOnlyList<PlayerEntry> entries, PlaySource? from)
    {
        if (from is null)
            return;
        foreach (var entry in entries)
            entry.From ??= from;
    }

    /// <summary>
    /// Shuffles what's to come, or puts the queue back in the order it was given, carrying on from the same song.
    /// </summary>
    public async Task SetShuffleAsync(bool on)
    {
        Shuffle = on;
        await _prefs.SetAsync("player.shuffle", on);
        if (on && _queue.Count > 0)
        {
            _ordered = [.. _queue];
            var upcoming = _queue.Skip(Current + 1).OrderBy(_ => Random.Shared.Next()).ToList();
            _queue.RemoveRange(Current + 1, _queue.Count - Current - 1);
            _queue.AddRange(upcoming);
        }
        else if (!on && _ordered is { } ordered)
        {
            var now = Now;
            var kept = ordered.Where(_queue.Contains).ToList();
            _queue.Clear();
            _queue.AddRange(kept);
            Current = now is null ? -1 : _queue.IndexOf(now);
            _ordered = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Off, then the whole queue, then the one song, then off again.</summary>
    public async Task CycleRepeatAsync()
    {
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };
        await _prefs.SetAsync("player.repeat", Repeat);
        Changed?.Invoke();
    }

    private async Task LoadModesAsync()
    {
        if (_modesLoaded)
            return;
        _modesLoaded = true;
        Shuffle = await _prefs.GetAsync("player.shuffle", false);
        Repeat = await _prefs.GetAsync("player.repeat", RepeatMode.Off);
    }

    // ---- Controls ----

    public async Task ToggleAsync()
    {
        if (Now is null)
            return;
        // A queue picked up after a reload hasn't loaded its song yet.
        if (_audio is null || _resumeAt is not null)
        {
            await StartAsync();
            return;
        }
        if (Playing)
            await _audio.InvokeVoidAsync("pause");
        else if (Now.Waiting || Now.Unplayable)
            await StartAsync();
        else
            await _audio.InvokeVoidAsync("play");
    }

    /// <summary>On to the next song, or round to the first when repeating the queue.</summary>
    public Task NextAsync()
    {
        NoteSkip();
        return JumpAsync(Current + 1 >= _queue.Count && Repeat != RepeatMode.Off ? 0 : Current + 1);
    }

    /// <summary>Back to the start of this track, or to the one before if it's only just started.</summary>
    public async Task PreviousAsync()
    {
        if (Position > 3 && _audio is not null)
            await SeekAsync(0);
        else
            await JumpAsync(Current == 0 && Repeat != RepeatMode.Off ? _queue.Count - 1 : Math.Max(Current - 1, 0));
    }

    public async Task JumpAsync(int index)
    {
        if (index >= _queue.Count)
        {
            await StopAsync();
            return;
        }
        // Where a queue picked up after a reload was left is for its song alone: another starts from its beginning (#105).
        if (index != Current)
            _resumeAt = null;
        Current = index;
        await StartAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        Position = seconds;
        // Not loaded yet, after a reload: it starts from there when played.
        if (_resumeAt is not null)
            _resumeAt = seconds;
        else if (_audio is not null)
            await _audio.InvokeVoidAsync("seek", seconds);
        TimeChanged?.Invoke();
    }

    public async Task RemoveAsync(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;
        _ordered?.Remove(_queue[index]);
        _queue.RemoveAt(index);
        if (index < Current)
            Current--;
        else if (index == Current)
        {
            await JumpAsync(Current);
            return;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves a song a step up or down the queue (-1 or 1), or to the top or bottom (<see cref="int.MinValue"/> or
    /// <see cref="int.MaxValue"/>), as a song's menu asks.
    /// </summary>
    public void MoveBy(int index, int by) =>
        Move(index, Math.Clamp(by is int.MinValue or int.MaxValue ? by : index + by, 0, _queue.Count - 1));

    /// <summary>Moves a song in the queue, carrying on with the same one.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _queue.Count || to < 0 || to >= _queue.Count || from == to)
            return;
        var now = Now;
        var entry = _queue[from];
        _queue.RemoveAt(from);
        _queue.Insert(to, entry);
        Current = now is null ? -1 : _queue.IndexOf(now);
        Changed?.Invoke();
    }

    /// <summary>Stops and empties the queue, which hides the player bar.</summary>
    public async Task ClearAsync()
    {
        _queue.Clear();
        _ordered = null;
        _resumeAt = null;
        // With nothing left to play, there's no player to show.
        Expanded = false;
        await StopAsync();
    }

    /// <summary>Adds a listen-now track to the library, from the file it already has.</summary>
    public async Task AddToLibraryAsync(PlayerEntry entry)
    {
        if (entry.ListenId is not { } id)
            return;
        try
        {
            entry.Update(await _api.AddListeningAsync(id));
        }
        catch (MelodexApiException e)
        {
            entry.Detail = e.Message;
        }
        Changed?.Invoke();
    }

    // ---- Playing ----

    // ---- Gapless: the next song loaded ahead, to start as this one ends ----

    private PlayerEntry? _prepared;
    private int _preparedToken;
    private double? _preparedLength;
    private GaplessPlayback? _gapless;
    private DateTime _gaplessAt;

    /// <summary>The song to load ahead, if the settings say this one and the next run into each other.</summary>
    private PlayerEntry? GaplessNext(GaplessPlayback gapless)
    {
        if (gapless == GaplessPlayback.Off || Repeat == RepeatMode.One || _resumeAt is not null || Now is not { Waiting: false, Unplayable: false } now)
            return null;
        var index = Current + 1 < _queue.Count ? Current + 1 : Repeat == RepeatMode.All ? 0 : -1;
        if (index < 0 || index == Current || _queue[index] is not { Waiting: false, Unplayable: false } next)
            return null;
        return gapless == GaplessPlayback.Always || next.AlbumId is { } album && album == now.AlbumId ? next : null;
    }

    /// <summary>Has the script load the song after this one ahead, or forget one that's no longer next.</summary>
    private async Task PrepareNextAsync()
    {
        if (_audio is null)
            return;
        if (_gapless is null || DateTime.UtcNow - _gaplessAt > TimeSpan.FromMinutes(10))
        {
            _gaplessAt = DateTime.UtcNow;
            try
            {
                _gapless = (await _api.GetPlaybackAsync()).Gapless;
            }
            catch (Exception e) when (e is HttpRequestException or MelodexApiException)
            {
                _gapless ??= GaplessPlayback.Off;
            }
        }
        var next = GaplessNext(_gapless.Value);
        if (ReferenceEquals(next, _prepared))
            return;
        _prepared = next;
        var token = ++_preparedToken;
        try
        {
            if (next is null)
            {
                await _audio.InvokeVoidAsync("unprepare");
                return;
            }
            var (url, mediaType, length) = await SourceAsync(next);
            if (token != _preparedToken)
                return;
            if (url is null || !await _audio.InvokeAsync<bool>("canPlay", mediaType))
            {
                await _audio.InvokeVoidAsync("unprepare");
                return;
            }
            _preparedLength = length;
            await _audio.InvokeVoidAsync("prepare", url, new { title = next.Title, artist = next.ArtistCredit, album = next.AlbumTitle, cover = next.CoverUrl }, token, length);
        }
        catch (JSException)
        {
            // Played the usual way when it comes.
        }
    }

    /// <summary>Whether the full-screen player's visualiser is showing, so the next song's bars are made ahead of it.</summary>
    public bool Visualising
    {
        get => _visualising;
        set
        {
            if (value && !_visualising)
                _ = WarmNextAsync();
            _visualising = value;
        }
    }

    private bool _visualising;

    /// <summary>Has the server make the visualiser's bars for the song after this one, so they're there when it plays.</summary>
    private async Task WarmNextAsync()
    {
        var next = Repeat == RepeatMode.One ? -1 : Current + 1 < _queue.Count ? Current + 1 : Repeat == RepeatMode.All ? 0 : -1;
        if (next < 0 || next == Current || _queue[next] is not { Unplayable: false } entry)
            return;
        try
        {
            var url = entry.ListenId is { } id
                ? (await _api.GetListeningAsync(id))?.StreamUrl
                : (await (entry.FileId is { } file ? _api.GetFilePlayInfoAsync(file) : _api.GetPlayInfoAsync(entry.TrackId, entry.EditionId)))?.StreamUrl;
            if (url is not null)
                await _api.WarmSpectrumAsync(url);
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException or TaskCanceledException)
        {
            // Made when it plays instead.
        }
    }

    /// <summary>Plays <see cref="Current"/>, skipping tracks that can't be played, or waits if it's still being fetched.</summary>
    private async Task StartAsync()
    {
        // A newer start (a click, or the next track) wins over one still waiting on the API.
        var attempt = ++_starting;
        while (Now is { } entry)
        {
            var (url, mediaType, length) = await SourceAsync(entry);
            if (attempt != _starting)
                return;
            if (url is not null)
            {
                var audio = await AudioAsync();
                if (await audio.InvokeAsync<bool>("canPlay", mediaType))
                {
                    Position = 0;
                    _length = length;
                    Duration = length ?? entry.DurationSeconds ?? 0;
                    // Loaded ahead again as it plays: a start of the next song that this overtook took the spare with it.
                    _prepared = null;
                    Begin(entry, _resumeAt ?? 0);
                    await audio.InvokeVoidAsync("load", url, new { title = entry.Title, artist = entry.ArtistCredit, album = entry.AlbumTitle, cover = entry.CoverUrl }, length);
                    if (_visualising)
                        _ = WarmNextAsync();
                    if (_resumeAt is { } at)
                    {
                        Position = at;
                        await audio.InvokeVoidAsync("seek", at);
                    }
                    _resumeAt = null;
                    break;
                }
                entry.Unplayable = true;
                entry.Detail = $"Your browser can't play this file ({mediaType}).";
            }
            if (entry.Waiting)
            {
                // Paused here until the server says it's ready; polling too, in case events are down.
                if (_audio is not null)
                    await _audio.InvokeVoidAsync("stop");
                Playing = false;
                _poll ??= new Timer(_ => _ = RefreshListeningAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                break;
            }
            if (Current + 1 >= _queue.Count)
            {
                await StopAsync();
                return;
            }
            Current++;
        }
        Changed?.Invoke();
    }

    /// <summary>A fresh stream URL for the entry, or null if it can't be played (yet), and how long its file plays if known.</summary>
    private async Task<(string? Url, string? MediaType, double? Length)> SourceAsync(PlayerEntry entry)
    {
        if (entry.Unplayable)
            return (null, null, null);
        if (_sources.TryGetValue(entry, out var known) && DateTime.UtcNow - known.At < SourceKept)
            return (known.Url, known.MediaType, known.Length);
        // Asked for once at a time: warming ahead and a skip to it share the one answer.
        if (!_fetching.TryGetValue(entry, out var fetching))
        {
            fetching = FetchAndKeepAsync(entry);
            _fetching.AddOrUpdate(entry, fetching);
        }
        return await fetching;
    }

    private async Task<(string? Url, string? MediaType, double? Length)> FetchAndKeepAsync(PlayerEntry entry)
    {
        try
        {
            var source = await FetchSourceAsync(entry);
            if (source.Url is not null)
                _sources.AddOrUpdate(entry, new Source(source.Url, source.MediaType, source.Length, DateTime.UtcNow));
            return source;
        }
        finally
        {
            _fetching.Remove(entry);
        }
    }

    private readonly ConditionalWeakTable<PlayerEntry, Task<(string? Url, string? MediaType, double? Length)>> _fetching = new();
    private PlayerEntry? _warmedAround;

    /// <summary>
    /// Asks ahead for the stream URLs of the songs either side of this one, as it starts: a skip soon after, on a locked
    /// phone where the page runs slowly, took seconds asking for its song first (#107).
    /// </summary>
    private async Task WarmAroundAsync()
    {
        if (Now is not { } now || ReferenceEquals(now, _warmedAround))
            return;
        _warmedAround = now;
        foreach (var index in new[] { Current + 1, Current - 1, Current + 2, Current + 3 })
        {
            if (index < 0 || index >= _queue.Count || !ReferenceEquals(Now, now))
                continue;
            if (_queue[index] is { Unplayable: false, Waiting: false } entry)
                await SourceAsync(entry);
        }
    }

    // Each song's stream URL, kept a while: a skip on a locked phone, where the page runs slowly, needn't ask for it
    // first (#103). The URLs last 12 hours, and a listen-now song stays at least an hour after it's played.
    private static readonly TimeSpan SourceKept = TimeSpan.FromMinutes(10);
    private readonly ConditionalWeakTable<PlayerEntry, Source> _sources = new();

    private sealed record Source(string Url, string? MediaType, double? Length, DateTime At);

    private async Task<(string? Url, string? MediaType, double? Length)> FetchSourceAsync(PlayerEntry entry)
    {
        // Asked for when it's near the song playing, and played once it's fetched.
        if (entry.Unfetched)
            return (null, null, null);
        try
        {
            if (entry.ListenId is { } id)
            {
                if (await _api.GetListeningAsync(id) is not { } item)
                {
                    entry.Unplayable = true;
                    return (null, null, null);
                }
                entry.Update(item);
                if (item.StreamUrl is not null)
                    return (item.StreamUrl, item.MediaType, entry.DurationSeconds = item.DurationSeconds ?? entry.DurationSeconds);
                if (item.State != ListenNowState.InLibrary)
                    return (null, null, null);
                // Added since: play it from the library.
            }
            if (await (entry.FileId is { } file ? _api.GetFilePlayInfoAsync(file) : _api.GetPlayInfoAsync(entry.TrackId, entry.EditionId)) is { } play)
            {
                entry.DurationSeconds = play.DurationSeconds ?? entry.DurationSeconds;
                return (play.StreamUrl, play.MediaType, play.DurationSeconds);
            }
            entry.Unplayable = true;
            entry.Detail = "It's no longer in your library.";
        }
        catch (HttpRequestException e)
        {
            entry.Unplayable = true;
            entry.Detail = e.Message;
        }
        return (null, null, null);
    }

    private async Task StopAsync()
    {
        _starting++;
        Current = -1;
        Playing = false;
        Position = Duration = 0;
        _length = null;
        StopPolling();
        if (_audio is not null)
            await _audio.InvokeVoidAsync("stop");
        Changed?.Invoke();
    }

    /// <summary>Whether songs can run into each other in this browser: not on iPhones and iPads (#107).</summary>
    public async Task<bool> GaplessWorksAsync() => await (await AudioAsync()).InvokeAsync<bool>("gaplessWorks");

    private async Task<IJSObjectReference> AudioAsync()
    {
        if (_audio is null)
        {
            _audio = await _js.InvokeAsync<IJSObjectReference>("import", "./js/player.js");
            _self = DotNetObjectReference.Create(this);
            await _audio.InvokeVoidAsync("init", _self);
        }
        return _audio;
    }

    // ---- Keeping listen-now tracks current ----

    private void OnEvent(string type)
    {
        if (type is "listen" or "connected")
            _ = RefreshListeningAsync();
        if (type is "queue" or "connected")
            _ = PickUpAsync();
    }

    private async Task RefreshListeningAsync()
    {
        if (!_queue.Any(e => e.ListenId is not null && e.ListenState is not (ListenNowState.InLibrary or ListenNowState.Expired)))
        {
            StopPolling();
            return;
        }
        IReadOnlyList<ListenNowInfo> items;
        try
        {
            items = await _api.ListListeningAsync(all: true);
        }
        catch (HttpRequestException)
        {
            return;
        }
        var byId = items.ToDictionary(i => i.Id);
        var wasWaiting = Now?.Waiting == true;
        foreach (var entry in _queue)
            if (entry.ListenId is { } id && byId.TryGetValue(id, out var item))
                entry.Update(item);
        if (!_queue.Any(e => e.ListenId is not null && e.Waiting))
            StopPolling();
        if (wasWaiting && Now is { Waiting: false })
            await StartAsync();
        else
            Changed?.Invoke();
    }

    private void StopPolling()
    {
        _poll?.Dispose();
        _poll = null;
    }

    // ---- From the audio element ----

    [JSInvokable]
    public void OnTime(double position, double duration)
    {
        Position = position;
        if (_length is > 0 and var length)
            Duration = length;
        else if (duration > 0)
            Duration = duration;
        TimeChanged?.Invoke();
        Hear(position);
        // Where it's got to is kept every so often, to pick up from after a reload or on a phone.
        if ((int)position / 15 != _savedSecond)
        {
            _savedSecond = (int)position / 15;
            _ = SaveAsync();
        }
    }

    /// <summary>A key pressed anywhere in the app, outside a text box: see the guide's list of shortcuts.</summary>
    [JSInvokable]
    public Task OnKey(string action) => action switch
    {
        "toggle" => ToggleAsync(),
        "next" => HasNext ? NextAsync() : Task.CompletedTask,
        "previous" => PreviousAsync(),
        "forward" when Now is not null && _resumeAt is null => SeekAsync(Math.Min(Position + 10, Math.Max(Duration - 1, 0))),
        "back" when Now is not null && _resumeAt is null => SeekAsync(Math.Max(Position - 10, 0)),
        "shuffle" => SetShuffleAsync(!Shuffle),
        "repeat" => CycleRepeatAsync(),
        _ => Task.CompletedTask,
    };

    // ---- Listening history (#2) ----

    // The song being listened to, how much of it has been heard, and where it had got to; and whether it's been recorded.
    private PlayerEntry? _hearing;
    private double _heard;
    private double _heardTo;
    private bool _recorded;

    /// <summary>A song started, from the start or picked up part way: it's playing now, and counts once enough is heard.</summary>
    private void Begin(PlayerEntry entry, double at)
    {
        (_hearing, _heard, _heardTo, _recorded) = (entry, 0, at, false);
        if (_owner is not null)
            _ = TellAsync(() => _api.SetNowPlayingAsync(PlayOf(entry, DateTime.UtcNow)));
    }

    /// <summary>
    /// Counts what's been heard, not where it's got to, so a seek doesn't make a play; once it's half the song or four
    /// minutes, it's a play.
    /// </summary>
    private void Hear(double position)
    {
        if (_hearing is not { } entry || !ReferenceEquals(entry, Now))
            return;
        var step = position - _heardTo;
        _heardTo = position;
        // A jump either way is a seek; the time comes about four times a second as it plays.
        if (step is > 0 and < 2 && Playing)
            _heard += step;
        if (_recorded || _owner is null || Duration <= 0 || _heard < Math.Min(Duration / 2, 240))
            return;
        _recorded = true;
        var started = DateTime.UtcNow.AddSeconds(-position);
        _ = TellAsync(() => _api.RecordPlayAsync(PlayOf(entry, started)));
    }

    private RecordPlay PlayOf(PlayerEntry e, DateTime at) => new(
        e.TrackId, e.FileId, e.Title, e.ArtistCredit, e.ArtistId, e.AlbumId, e.AlbumTitle, e.CoverUrl, e.DurationSeconds ?? (Duration > 0 ? Duration : null),
        at, e.From);

    private static async Task TellAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            // Someone who may not listen keeps no history; and one lost play isn't worth bothering anyone with.
        }
    }

    // ---- Kept per person, on the server ----

    // Where the queue was kept before it moved to the server (#7): read once, to move it there.
    private sealed record Saved(List<PlayerEntry> Queue, int Current, double Position, List<int>? Ordered);

    private string SavedKey => $"player.queue.{_owner:N}";

    // What was saved or picked up last, so the same isn't saved again and a queue isn't sent back where it came from;
    // and when this browser saved it, so its own save isn't taken for another's.
    private string? _lastKept;
    private DateTime? _savedAt;
    private Task? _saving;
    private bool _saveAgain;

    /// <summary>
    /// Picks up the queue this person left, here, in another browser or in a Subsonic player, paused where they were,
    /// and starts the keyboard shortcuts.
    /// </summary>
    public async Task AttachAsync(Guid? userId)
    {
        await AudioAsync();
        if (userId is null || userId == _owner)
            return;
        _owner = userId;
        await LoadModesAsync();
        if (_queue.Count > 0)
            return;
        if (await LoadQueueAsync() is { } queue)
        {
            Adopt(queue);
            return;
        }
        // Kept in this browser before: moved to the server now.
        var saved = await _prefs.GetAsync<Saved?>(SavedKey, null);
        await _prefs.SetAsync<Saved?>(SavedKey, null);
        if (saved is not { Queue.Count: > 0 } || saved.Current < 0 || saved.Current >= saved.Queue.Count)
            return;
        Adopt(saved.Queue, saved.Current, saved.Position, saved.Ordered);
        await SaveAsync();
    }

    private async Task<PlayQueue?> LoadQueueAsync()
    {
        try
        {
            return await _api.GetPlayQueueAsync();
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            // Someone who may not listen has none.
            return null;
        }
    }

    private void Adopt(PlayQueue queue)
    {
        _lastKept = Kept(queue.Items, queue.Current, queue.Position, queue.Ordered);
        Adopt(queue.Items.Select(Entry).ToList(), queue.Current, queue.Position, queue.Ordered);
    }

    private void Adopt(List<PlayerEntry> entries, int current, double position, IReadOnlyList<int>? ordered)
    {
        _queue.Clear();
        _queue.AddRange(entries);
        // The order it was given in, while shuffled, as places in the queue.
        _ordered = ordered?.Where(i => i >= 0 && i < _queue.Count).Select(i => _queue[i]).ToList();
        Current = current >= 0 && current < _queue.Count ? current : _queue.Count > 0 ? 0 : -1;
        Position = position;
        Duration = Now?.DurationSeconds ?? 0;
        _resumeAt = Now is null ? null : position;
        _length = null;
        Changed?.Invoke();
        _ = RefreshListeningAsync();
    }

    /// <summary>
    /// The queue was saved somewhere: picked up here unless something's playing, so a phone can carry on from where the
    /// browser left off and the browser from the phone, without either interrupting the other.
    /// </summary>
    private async Task PickUpAsync()
    {
        if (_owner is null || Playing || _starting > 0)
            return;
        var queue = await LoadQueueAsync();
        if (Playing || queue is not null && queue.ChangedAt == _savedAt)
            return;
        if (queue is null)
        {
            if (_queue.Count == 0)
                return;
            _lastKept = Kept([], -1, 0, null);
            await StopForPickUpAsync();
            Adopt([], -1, 0, null);
            return;
        }
        if (Kept(queue.Items, queue.Current, queue.Position, queue.Ordered) == _lastKept)
            return;
        await StopForPickUpAsync();
        Adopt(queue);
    }

    // What's loaded in the audio element belongs to the queue being replaced.
    private async Task StopForPickUpAsync()
    {
        if (_audio is not null)
            await _audio.InvokeVoidAsync("stop");
    }

    private static PlayQueueItem Item(PlayerEntry e) => new(
        e.TrackId, e.Title, e.ArtistCredit, e.FileId, e.EditionId, e.ArtistId, e.AlbumId, e.AlbumTitle, e.CoverUrl, e.DurationSeconds, e.Missing, e.ListenId,
        e.From);

    private static PlayerEntry Entry(PlayQueueItem i) => new()
    {
        TrackId = i.TrackId, Title = i.Title, ArtistCredit = i.ArtistCredit, FileId = i.FileId, EditionId = i.EditionId, ArtistId = i.ArtistId,
        AlbumId = i.AlbumId, AlbumTitle = i.AlbumTitle, CoverUrl = i.CoverUrl, DurationSeconds = i.DurationSeconds, Missing = i.Missing, ListenId = i.ListenId,
        From = i.From,
    };

    // A queue as it's kept, to tell whether it's changed: where in a song is to the second.
    private static string Kept(IReadOnlyList<PlayQueueItem> items, int current, double position, IReadOnlyList<int>? ordered) =>
        System.Text.Json.JsonSerializer.Serialize(new { items, current, position = Math.Floor(position), ordered }, MelodexJson.Options);

    /// <summary>Keeps the queue on the server, once whatever's being saved now has been.</summary>
    private Task SaveAsync()
    {
        if (_owner is null)
            return Task.CompletedTask;
        if (_saving is { IsCompleted: false })
        {
            _saveAgain = true;
            return _saving;
        }
        return _saving = SaveWhileChangedAsync();
    }

    private async Task SaveWhileChangedAsync()
    {
        do
        {
            _saveAgain = false;
            // Changes come in bursts: one save for each.
            await Task.Delay(300);
            var items = _queue.Select(Item).ToList();
            var current = _queue.Count == 0 ? -1 : Current;
            var position = _resumeAt ?? Position;
            var ordered = _ordered?.Select(e => _queue.IndexOf(e)).ToList();
            var kept = Kept(items, current, position, ordered);
            if (kept == _lastKept)
                continue;
            try
            {
                var saved = await _api.SavePlayQueueAsync(new SavePlayQueue(items, current, position, ordered));
                _lastKept = kept;
                _savedAt = saved?.ChangedAt;
            }
            catch (Exception e) when (e is HttpRequestException or MelodexApiException)
            {
                // Tried again with the next change.
            }
        } while (_saveAgain);
    }

    [JSInvokable]
    public void OnPlaying(bool playing)
    {
        Playing = playing;
        Changed?.Invoke();
    }

    /// <summary>
    /// The script started the song it had loaded ahead, as the one before ended, with no gap: it's what's playing now.
    /// One loaded for a queue that's changed since is out of date, and the script is told so before it ends; if it
    /// went on anyway, the song after this one is played as usual.
    /// </summary>
    [JSInvokable]
    public Task OnAdvanced(int token)
    {
        if (token != _preparedToken || _prepared is not { } entry || _queue.IndexOf(entry) is var index && index < 0)
            return NextAsync();
        _starting++;
        _prepared = null;
        Current = index;
        Position = 0;
        _length = _preparedLength;
        Duration = _preparedLength ?? entry.DurationSeconds ?? 0;
        Begin(entry, 0);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>A song finished: the same again when repeating one, otherwise the next.</summary>
    [JSInvokable]
    public Task OnEnded() => Repeat == RepeatMode.One && Now is not null ? StartAsync() : NextAsync();

    [JSInvokable]
    public Task OnError(string message)
    {
        if (Now is { } entry)
        {
            entry.Unplayable = true;
            entry.Detail = message;
        }
        // Not round again when repeating, or a queue that can't be played would go round for ever.
        return JumpAsync(Current + 1);
    }

    [JSInvokable]
    public Task OnRemote(string action) => action == "next" ? NextAsync() : PreviousAsync();

    public async ValueTask DisposeAsync()
    {
        _events.Received -= OnEvent;
        StopPolling();
        if (_audio is not null)
            await _audio.DisposeAsync();
        _self?.Dispose();
    }
}
