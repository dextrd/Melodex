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
    public Guid? ListenId { get; init; }

    public ListenNowState? ListenState { get; set; }

    /// <summary>Why it can't be played, or what's happening to it.</summary>
    public string? Detail { get; set; }

    /// <summary>It failed, expired or the browser can't play it, so the queue skips it.</summary>
    public bool Unplayable { get; set; }

    public bool Waiting => ListenState is ListenNowState.Requested or ListenNowState.Fetching;

    public static PlayerEntry Owned(TrackRow track, AlbumSummary album, Guid? editionId) => new()
    {
        TrackId = track.TrackId, EditionId = editionId, Title = track.Title, ArtistCredit = track.ArtistCredit, ArtistId = album.ArtistId,
        AlbumId = album.Id, AlbumTitle = album.Title, CoverUrl = album.CoverUrl, DurationSeconds = track.DurationSeconds,
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
    public string? AlbumHref => AlbumId is { } album ? $"{(FileId is null ? "albums" : "local/albums")}/{album}" : null;

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
    }

    /// <summary>The queue changed, or what's playing did.</summary>
    public event Action? Changed;

    /// <summary>The position moved; separate from <see cref="Changed"/> so only the seek bar redraws every second.</summary>
    public event Action? TimeChanged;

    public IReadOnlyList<PlayerEntry> Queue => _queue;
    public int Current { get; private set; } = -1;
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
    public async Task<TrackLyrics> LyricsAsync(PlayerEntry entry, bool fresh = false)
    {
        var key = (entry.TrackId, entry.FileId);
        if (!fresh && _lyrics.TryGetValue(key, out var known))
            return known;
        var none = new TrackLyrics(LyricsKind.None, [], null);
        TrackLyrics lyrics;
        try
        {
            lyrics = entry.FileId is { } file ? await _api.GetFileLyricsAsync(file)
                : entry.ListenState is null or ListenNowState.InLibrary ? await _api.GetTrackLyricsAsync(entry.TrackId)
                : none;
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
            await AddAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList(), next);
    }

    /// <summary>The album's tracks in order: tracks you have, and the missing ones fetched to listen to.</summary>
    private async Task<(List<PlayerEntry> Entries, int Start)> AlbumEntriesAsync(AlbumDetail album, Guid? fromTrack)
    {
        Error = null;
        var tracks = album.Tracks.Where(t => t.Status is TrackStatus.Owned or TrackStatus.OwnedElsewhere or TrackStatus.Missing).ToList();
        var listening = new Dictionary<Guid, ListenNowInfo>();
        if (tracks.Any(t => t.Status == TrackStatus.Missing))
        {
            try
            {
                foreach (var item in await _api.StartListeningAsync(new StartListen(RequestKind.Album, album.Album.Id, album.ShownEditionId, fromTrack)))
                    listening[item.TrackId] = item;
            }
            catch (MelodexApiException e)
            {
                Error = e.Message;
            }
        }
        var entries = new List<PlayerEntry>();
        var start = 0;
        foreach (var track in tracks)
        {
            if (track.TrackId == fromTrack)
                start = entries.Count;
            if (track.Status != TrackStatus.Missing)
                entries.Add(PlayerEntry.Owned(track, album.Album, album.ShownEditionId));
            else if (listening.TryGetValue(track.TrackId, out var item))
                entries.Add(PlayerEntry.Listening(item));
        }
        return (entries, start);
    }

    /// <summary>Plays an album from a list, loading its tracks first. Albums of your own files are found too.</summary>
    public async Task PlayAlbumAsync(Guid albumId)
    {
        if (await _api.GetAlbumAsync(albumId) is { } album)
            await PlayAlbumAsync(album);
        else if (await _api.GetLocalAlbumAsync(albumId) is { } own)
            await PlayAsync(own.Tracks.Select(t => PlayerEntry.Owned(t, own)).ToList());
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

    /// <summary>
    /// Replaces the queue and starts playing it. When shuffling, the song started from comes first and the rest follow
    /// in a random order; started from the beginning, all of it is in a random order.
    /// </summary>
    public async Task PlayAsync(IReadOnlyList<PlayerEntry> entries, int start = 0)
    {
        await LoadModesAsync();
        _queue.Clear();
        _queue.AddRange(entries);
        Current = entries.Count == 0 ? -1 : Math.Clamp(start, 0, entries.Count - 1);
        _ordered = null;
        _resumeAt = null;
        if (Shuffle && Current >= 0)
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
    public async Task AddAsync(IReadOnlyList<PlayerEntry> entries, bool next = false)
    {
        await LoadModesAsync();
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
    public Task NextAsync() => JumpAsync(Current + 1 >= _queue.Count && Repeat != RepeatMode.Off ? 0 : Current + 1);

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
        Current = index;
        await StartAsync();
    }

    public async Task SeekAsync(double seconds)
    {
        Position = seconds;
        if (_audio is not null)
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
            var (url, mediaType) = await SourceAsync(next);
            if (token != _preparedToken)
                return;
            if (url is null || !await _audio.InvokeAsync<bool>("canPlay", mediaType))
            {
                await _audio.InvokeVoidAsync("unprepare");
                return;
            }
            await _audio.InvokeVoidAsync("prepare", url, new { title = next.Title, artist = next.ArtistCredit, album = next.AlbumTitle, cover = next.CoverUrl }, token);
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
                await _api.WarmBarsAsync(url);
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
            var (url, mediaType) = await SourceAsync(entry);
            if (attempt != _starting)
                return;
            if (url is not null)
            {
                var audio = await AudioAsync();
                if (await audio.InvokeAsync<bool>("canPlay", mediaType))
                {
                    Position = 0;
                    Duration = entry.DurationSeconds ?? 0;
                    await audio.InvokeVoidAsync("load", url, new { title = entry.Title, artist = entry.ArtistCredit, album = entry.AlbumTitle, cover = entry.CoverUrl });
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

    /// <summary>A fresh stream URL for the entry, or null if it can't be played (yet).</summary>
    private async Task<(string? Url, string? MediaType)> SourceAsync(PlayerEntry entry)
    {
        if (entry.Unplayable)
            return (null, null);
        try
        {
            if (entry.ListenId is { } id)
            {
                if (await _api.GetListeningAsync(id) is not { } item)
                {
                    entry.Unplayable = true;
                    return (null, null);
                }
                entry.Update(item);
                if (item.StreamUrl is not null)
                    return (item.StreamUrl, item.MediaType);
                if (item.State != ListenNowState.InLibrary)
                    return (null, null);
                // Added since: play it from the library.
            }
            if (await (entry.FileId is { } file ? _api.GetFilePlayInfoAsync(file) : _api.GetPlayInfoAsync(entry.TrackId, entry.EditionId)) is { } play)
            {
                entry.DurationSeconds ??= play.DurationSeconds;
                return (play.StreamUrl, play.MediaType);
            }
            entry.Unplayable = true;
            entry.Detail = "It's no longer in your library.";
        }
        catch (HttpRequestException e)
        {
            entry.Unplayable = true;
            entry.Detail = e.Message;
        }
        return (null, null);
    }

    private async Task StopAsync()
    {
        _starting++;
        Current = -1;
        Playing = false;
        Position = Duration = 0;
        StopPolling();
        if (_audio is not null)
            await _audio.InvokeVoidAsync("stop");
        Changed?.Invoke();
    }

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
        if (!_queue.Any(e => e.Waiting))
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
        if (duration > 0)
            Duration = duration;
        TimeChanged?.Invoke();
        // Where it's got to is kept every few seconds, to pick up from after a reload.
        if ((int)position / 5 != _savedSecond)
        {
            _savedSecond = (int)position / 5;
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

    // ---- Kept per person in this browser ----

    private sealed record Saved(List<PlayerEntry> Queue, int Current, double Position, List<int>? Ordered);

    private string SavedKey => $"player.queue.{_owner:N}";

    /// <summary>
    /// Picks up the queue this person left in this browser, paused where they were, and starts the keyboard shortcuts.
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
        var saved = await _prefs.GetAsync<Saved?>(SavedKey, null);
        if (saved is not { Queue.Count: > 0 } || saved.Current < 0 || saved.Current >= saved.Queue.Count)
            return;
        _queue.AddRange(saved.Queue);
        // The order it was given in, while shuffled, as places in the queue.
        _ordered = saved.Ordered?.Where(i => i >= 0 && i < _queue.Count).Select(i => _queue[i]).ToList();
        Current = saved.Current;
        Position = saved.Position;
        Duration = Now!.DurationSeconds ?? 0;
        _resumeAt = saved.Position;
        Changed?.Invoke();
    }

    private async Task SaveAsync()
    {
        if (_owner is null)
            return;
        var ordered = _ordered?.Select(e => _queue.IndexOf(e)).ToList();
        await _prefs.SetAsync(SavedKey, _queue.Count == 0 ? null : new Saved([.. _queue], Current, _resumeAt ?? Position, ordered));
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
        Duration = entry.DurationSeconds ?? 0;
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
