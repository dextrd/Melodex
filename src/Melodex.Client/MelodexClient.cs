using System.Net;
using System.Net.Http.Json;

namespace Melodex.Client;

/// <summary>Typed client for the Melodex public API. Pass an HttpClient whose BaseAddress is the Melodex server.</summary>
public sealed class MelodexClient(HttpClient http)
{
    private const string Api = "api/v1";

    public Task<LibraryStatus?> GetLibraryStatusAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<LibraryStatus>($"{Api}/library", MelodexJson.Options, ct);

    public Task<IReadOnlyList<LibraryArtist>> GetLibraryArtistsAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryArtist>($"{Api}/library/artists", ct);

    public Task<IReadOnlyList<LibraryAlbum>> GetLibraryAlbumsAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryAlbum>($"{Api}/library/albums", ct);

    public Task<IReadOnlyList<LibraryTrack>> GetLibraryTracksAsync(CancellationToken ct = default) =>
        GetListAsync<LibraryTrack>($"{Api}/library/tracks", ct);

    // The library's big lists as last sent, by their ETags, so asking again for one that hasn't changed is a 304 with
    // nothing to download or read.
    private readonly Dictionary<string, (string ETag, object List)> _lists = [];

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        (string? ETag, object? List) known;
        lock (_lists)
            known = _lists.TryGetValue(path, out var held) ? held : default;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (known.ETag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", known.ETag);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified && known.List is IReadOnlyList<T> same)
            return same;
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<T>>(MelodexJson.Options, ct) ?? [];
        if (response.Headers.ETag is { } tag)
            lock (_lists)
                _lists[path] = (tag.ToString(), list);
        return list;
    }

    public async Task<IReadOnlyList<MissingAlbum>> GetMissingAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<MissingAlbum>>($"{Api}/library/missing", MelodexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<ReviewItem>> GetReviewAsync(ReviewList list = ReviewList.Review, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ReviewItem>>($"{Api}/library/review?list={list}", MelodexJson.Options, ct) ?? [];

    /// <summary>Excludes, defers or hides a file, or with <see cref="SetAside.None"/> puts it back to be matched at the next scan.</summary>
    public async Task SetFileAsideAsync(Guid fileId, SetAside setAside, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/files/{fileId}/set-aside", new SetFileAside(setAside), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>As <see cref="SetFileAsideAsync"/>, for many files.</summary>
    public async Task<ReviewDone> SetFilesAsideAsync(IReadOnlyList<Guid> fileIds, SetAside setAside, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/set-aside", new SetFilesAside(fileIds, setAside), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    /// <summary>Excludes a file from matching, or includes it again.</summary>
    public async Task SetFileExcludedAsync(Guid fileId, bool excluded, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/files/{fileId}/excluded", new ExcludeFile(excluded), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<TaskInfo> ScanLibraryAsync(Rematch rematch = Rematch.None, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/scan", new ScanLibrary(rematch), MelodexJson.Options, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Queues filling in the catalogue for every library artist.</summary>
    public async Task<TaskInfo> UpdateCatalogueAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/catalogue-update", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Looks for lyrics for songs without them, through the lyrics plugins.</summary>
    public async Task<TaskInfo> FindLyricsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/lyrics", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Makes the visualiser's bars for songs without them; the queued task.</summary>
    public async Task<TaskInfo> MakeBarsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/bars", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<ScheduleStatus> GetSchedulesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScheduleStatus>($"{Api}/schedules", MelodexJson.Options, ct))!;

    public async Task SaveSchedulesAsync(ScheduleSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/schedules", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>A song's lyrics, as its files have them.</summary>
    public async Task<TrackLyrics> GetTrackLyricsAsync(Guid trackId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<TrackLyrics>($"{Api}/library/tracks/{trackId}/lyrics", MelodexJson.Options, ct))!;

    /// <summary>A file's lyrics.</summary>
    public async Task<TrackLyrics> GetFileLyricsAsync(Guid fileId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<TrackLyrics>($"{Api}/library/files/{fileId}/lyrics", MelodexJson.Options, ct))!;

    /// <summary>Looks for a song's lyrics now and writes them to its files, replacing any it has.</summary>
    public async Task<TrackLyrics> FindTrackLyricsAsync(Guid trackId, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"{Api}/library/tracks/{trackId}/lyrics", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TrackLyrics>(MelodexJson.Options, ct))!;
    }

    /// <summary>Writes a song's lyrics to its files, replacing any it has; with no text, takes them away.</summary>
    public async Task<TrackLyrics> SaveTrackLyricsAsync(Guid trackId, string? text, CancellationToken ct = default)
    {
        using var response = text is null
            ? await http.DeleteAsync($"{Api}/library/tracks/{trackId}/lyrics", ct)
            : await http.PutAsJsonAsync($"{Api}/library/tracks/{trackId}/lyrics", new SaveLyrics(text), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TrackLyrics>(MelodexJson.Options, ct))!;
    }

    /// <summary>Lyrics the lyrics plugins have that might be the song's: for what's typed, or else its title and artist.</summary>
    public async Task<IReadOnlyList<LyricsChoice>> SearchTrackLyricsAsync(Guid trackId, string? text, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/library/tracks/{trackId}/lyrics/search?q={Uri.EscapeDataString(text ?? "")}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<IReadOnlyList<LyricsChoice>>(MelodexJson.Options, ct))!;
    }

    /// <summary>Excludes a song from lyrics matching, so no scan looks for its lyrics, or includes it again.</summary>
    public async Task<TrackLyrics> ExcludeTrackLyricsAsync(Guid trackId, bool excluded, CancellationToken ct = default)
    {
        using var response = excluded
            ? await http.PutAsync($"{Api}/library/tracks/{trackId}/lyrics/excluded", null, ct)
            : await http.DeleteAsync($"{Api}/library/tracks/{trackId}/lyrics/excluded", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TrackLyrics>(MelodexJson.Options, ct))!;
    }

    private static string CoverPath(CoverSubject subject, Guid id) => $"{Api}/library/{(subject == CoverSubject.Album ? "albums" : "tracks")}/{id}/cover";

    /// <summary>The covers an album or song could have, from every release it's on.</summary>
    public async Task<CoverChoices> GetCoverChoicesAsync(CoverSubject subject, Guid id, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<CoverChoices>(CoverPath(subject, id), MelodexJson.Options, ct))!;

    /// <summary>Sets an album's or song's cover, writing it into its files straight away.</summary>
    public async Task<CoverChosen> ChooseCoverAsync(CoverSubject subject, Guid id, ChooseCover body, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync(CoverPath(subject, id), body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CoverChosen>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool? active = null, int? limit = null, CancellationToken ct = default)
    {
        var query = string.Join('&', new[] { active is { } a ? $"active={a.ToString().ToLowerInvariant()}" : null, limit is { } l ? $"limit={l}" : null }.OfType<string>());
        return await http.GetFromJsonAsync<List<TaskInfo>>($"{Api}/tasks{(query.Length > 0 ? "?" + query : "")}", MelodexJson.Options, ct) ?? [];
    }

    public Task<TaskInfo?> GetTaskAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<TaskInfo>($"{Api}/tasks/{id}", MelodexJson.Options, ct);

    /// <summary>What the task has done, row by row; null when it has none kept, as only the newest of each kind does.</summary>
    /// <param name="since">The <see cref="TaskRowsPage.Latest"/> from the last call, to get only what changed; 0 for every row.</param>
    public Task<TaskRowsPage?> GetTaskRowsAsync(Guid id, long since = 0, CancellationToken ct = default) =>
        GetOrNullAsync<TaskRowsPage>($"{Api}/tasks/{id}/rows?since={since}", ct);

    /// <param name="quick">Answer at once with what's known, rather than fetching a discography first; see <see cref="ArtistDetail.Fetching"/>.</param>
    public Task<ArtistDetail?> GetArtistAsync(Guid id, bool quick = false, CancellationToken ct = default) =>
        GetOrNullAsync<ArtistDetail>(quick ? $"{Api}/artists/{id}?quick=true" : $"{Api}/artists/{id}", ct);

    public async Task<IReadOnlyList<AlbumSummary>> GetArtistAlbumsAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AlbumSummary>>($"{Api}/artists/{id}/albums", MelodexJson.Options, ct) ?? [];

    public Task<TrackDetail?> GetTrackAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<TrackDetail>($"{Api}/tracks/{id}", ct);

    /// <param name="quick">Answer at once with what's known, rather than fetching editions first; see <see cref="AlbumDetail.Fetching"/>.</param>
    public Task<AlbumDetail?> GetAlbumAsync(Guid id, Guid? editionId = null, bool quick = false, CancellationToken ct = default) =>
        GetOrNullAsync<AlbumDetail>($"{Api}/albums/{id}?quick={(quick ? "true" : "false")}{(editionId is null ? "" : $"&edition={editionId}")}", ct);

    public async Task SetAlbumIgnoredAsync(Guid id, bool ignored, CancellationToken ct = default)
    {
        using var response = ignored
            ? await http.PutAsync($"{Api}/albums/{id}/ignore", null, ct)
            : await http.DeleteAsync($"{Api}/albums/{id}/ignore", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetTrackIgnoredAsync(Guid id, bool ignored, CancellationToken ct = default)
    {
        using var response = ignored
            ? await http.PutAsync($"{Api}/tracks/{id}/ignore", null, ct)
            : await http.DeleteAsync($"{Api}/tracks/{id}/ignore", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <param name="kinds">Some of <c>artists</c>, <c>albums</c> and <c>tracks</c>; all of them when empty.</param>
    /// <param name="local">Only what Melodex already holds, which answers at once.</param>
    public async Task<SearchResults> SearchAsync(string query, IReadOnlyCollection<string>? kinds = null, bool local = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SearchResults>(
            $"{Api}/catalogue/search?q={Uri.EscapeDataString(query)}{(kinds is { Count: > 0 } ? $"&kinds={string.Join(',', kinds)}" : "")}{(local ? "&local=true" : "")}",
            MelodexJson.Options, ct) ?? new SearchResults([], [], []);

    public async Task<LibrarySettings> GetLibrarySettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySettings>($"{Api}/settings/library", MelodexJson.Options, ct) ?? new LibrarySettings();

    public async Task SaveLibrarySettingsAsync(LibrarySettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/library", settings, MelodexJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ArtistShowing> GetArtistShowingAsync(Guid artistId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ArtistShowing>($"{Api}/settings/artists/{artistId}/showing", MelodexJson.Options, ct))!;

    /// <summary>The artist's own choices for the missing view; null to have them follow the library's again.</summary>
    public async Task SaveArtistShowingAsync(Guid artistId, IReadOnlyDictionary<ReleaseKind, ReleaseShowing>? showing, CancellationToken ct = default)
    {
        using var response = showing is null
            ? await http.DeleteAsync($"{Api}/settings/artists/{artistId}/showing", ct)
            : await http.PutAsJsonAsync($"{Api}/settings/artists/{artistId}/showing", showing, MelodexJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Activity log entries newer than <paramref name="after"/>, oldest first.</summary>
    /// <param name="since">The <see cref="ScanFeedPage.Latest"/> from the last call, to get only what changed; 0 for every file.</param>
    public async Task<ScanFeedPage> GetScanFilesAsync(long since = 0, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScanFeedPage>($"{Api}/library/scan/files?since={since}", MelodexJson.Options, ct))!;

    /// <summary>One scan's files, the running one or an earlier one; null once an earlier one's list isn't kept.</summary>
    public Task<ScanFeedPage?> GetScanFilesAsync(Guid task, long since = 0, CancellationToken ct = default) =>
        GetOrNullAsync<ScanFeedPage>($"{Api}/library/scan/files?task={task}&since={since}", ct);

    public async Task<LogPage> GetLogAsync(long after = 0, int limit = 1000, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LogPage>($"{Api}/log?after={after}&limit={limit}", MelodexJson.Options, ct) ?? new LogPage([], after);

    // ---- Requests ----

    /// <summary>Fetch an artist, album or track. Returns the new request, or the one already queued for it.</summary>
    public async Task<RequestInfo> CreateRequestAsync(CreateRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests", request, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<RequestInfo>> ListRequestsAsync(bool activeOnly = false, int limit = 100, DateTime? before = null, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<RequestInfo>>($"{Api}/requests?active={activeOnly.ToString().ToLowerInvariant()}&limit={limit}"
                                                       + (before is { } b ? $"&before={Uri.EscapeDataString(b.ToUniversalTime().ToString("O"))}" : ""), MelodexJson.Options, ct) ?? [];

    public Task<RequestInfo?> GetRequestAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<RequestInfo>($"{Api}/requests/{id}", ct);

    // ---- Listen now ----

    /// <summary>Starts listening to a track, or to an album's missing tracks in order.</summary>
    public async Task<IReadOnlyList<ListenNowInfo>> StartListeningAsync(StartListen start, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/listen", start, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<ListenNowInfo>>(MelodexJson.Options, ct))!;
    }

    public async Task<RemovalPreview> GetRemovalAsync(LibraryItemKind kind, Guid id, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<RemovalPreview>($"{Api}/library/removal?kind={kind}&id={id}", MelodexJson.Options, ct))!;

    public async Task<FormatPlan> GetFormatPlanAsync(FormatScope scope, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<FormatPlan>($"{Api}/library/format?kind={scope.Kind}{(scope.Id is { } id ? $"&id={id}" : "")}", MelodexJson.Options, ct))!;

    /// <summary>Starts formatting in the background; the task reports progress like a scan.</summary>
    public async Task<TaskInfo> StartFormatAsync(StartFormat body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"{Api}/library/format", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<RemovalResult> RemoveFromLibraryAsync(RemoveFromLibrary body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"{Api}/library/remove", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RemovalResult>(MelodexJson.Options, ct))!;
    }

    /// <param name="all">Include those discarded, expired or added to the library.</param>
    public async Task<IReadOnlyList<ListenNowInfo>> ListListeningAsync(bool all = false, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ListenNowInfo>>($"{Api}/listen{(all ? "?all=true" : "")}", MelodexJson.Options, ct) ?? [];

    public Task<ListenNowInfo?> GetListeningAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<ListenNowInfo>($"{Api}/listen/{id}", ct);

    /// <summary>A listen-now song's lyrics; null while they're still being looked for.</summary>
    public async Task<TrackLyrics?> GetListeningLyricsAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/listen/{id}/lyrics", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TrackLyrics>(MelodexJson.Options, ct);
    }

    /// <summary>Adds a playable listen-now track to the library.</summary>
    public async Task<ListenNowInfo> AddListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/listen/{id}/add", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ListenNowInfo>(MelodexJson.Options, ct))!;
    }

    public async Task DiscardListeningAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/listen/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>How to play a track you have; null if you don't have it.</summary>
    /// <summary>How to play one file, matched or not.</summary>
    public Task<PlayInfo?> GetFilePlayInfoAsync(Guid fileId, CancellationToken ct = default) =>
        GetOrNullAsync<PlayInfo>($"{Api}/library/files/{fileId}/play", ct);

    public Task<LocalArtistDetail?> GetLocalArtistAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<LocalArtistDetail>($"{Api}/library/local/artists/{id}", ct);

    public Task<LocalAlbumDetail?> GetLocalAlbumAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<LocalAlbumDetail>($"{Api}/library/local/albums/{id}", ct);

    public Task<PlayInfo?> GetPlayInfoAsync(Guid trackId, Guid? editionId = null, CancellationToken ct = default) =>
        GetOrNullAsync<PlayInfo>($"{Api}/tracks/{trackId}/play{(editionId is { } e ? $"?edition={e}" : "")}", ct);

    public async Task CancelRequestAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/requests/{id}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<RequestInfo> RetryRequestAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/requests/{id}/retry", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Queues every failed download again; how many were.</summary>
    public async Task<AskedAgain> RetryFailedRequestsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/requests/retry-failed", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AskedAgain>(MelodexJson.Options, ct))!;
    }

    public async Task<RequestInfo> ChooseCandidateAsync(Guid id, ChooseCandidate choice, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests/{id}/choose", choice, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Accepts the suggestion for each of these files that has one.</summary>
    public async Task<ReviewDone> ApproveFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/approve", new ReviewFiles(fileIds), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    /// <summary>Excludes these files from matching.</summary>
    public async Task<ReviewDone> ExcludeFilesAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/review/exclude", new ReviewFiles(fileIds), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReviewDone>(MelodexJson.Options, ct))!;
    }

    public async Task MatchFileAsync(Guid fileId, MatchFile match, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/files/{fileId}/match", match, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<LogSettings> GetLogSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LogSettings>($"{Api}/settings/log", MelodexJson.Options, ct))!;

    public async Task SaveLogSettingsAsync(LogSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/log", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<FetchSettings> GetFetchSettingsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<FetchSettings>($"{Api}/settings/fetch", MelodexJson.Options, ct) ?? new FetchSettings();

    public async Task SaveFetchSettingsAsync(FetchSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/fetch", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>How the library's lossless files stand on being checked for lossy music passed off as lossless.</summary>
    public async Task<SpectrumStatus> GetSpectrumStatusAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SpectrumStatus>($"{Api}/library/spectrum", MelodexJson.Options, ct))!;

    /// <summary>Queues checking the library's lossless files: those not yet checked, or with again, all of them.</summary>
    public async Task TrustLosslessAsync(IReadOnlyList<Guid> fileIds, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/spectrum/trust", new TrustLossless(fileIds), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<RequestInfo> KeepRequestAsync(Guid id, Guid jobId, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/requests/{id}/keep", new KeepCopy(jobId), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<TaskInfo> CheckSpectraAsync(bool again = false, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/library/spectrum", new CheckSpectra(again), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<QualityProfile> GetQualityProfileAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<QualityProfile>($"{Api}/settings/quality", MelodexJson.Options, ct) ?? new QualityProfile();

    public async Task SaveQualityProfileAsync(QualityProfile profile, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/quality", profile, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Upgrades' settings, how many songs are below their cutoff, held files, and any warning about the media server.</summary>
    /// <summary>How music is played, as every listener needs to know.</summary>
    public async Task<PlaybackSettings> GetPlaybackAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<PlaybackSettings>($"{Api}/playback", MelodexJson.Options, ct) ?? new PlaybackSettings();

    public async Task<PlaybackStatus> GetPlaybackSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<PlaybackStatus>($"{Api}/settings/playback", MelodexJson.Options, ct))!;

    public async Task SavePlaybackSettingsAsync(SavePlayback settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/playback", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>A song's spectrum, by its stream URL: the same, with <c>spectrum</c> for <c>stream</c>.</summary>
    public static string SpectrumUrl(string streamUrl) => streamUrl.Replace("/stream?", "/spectrum?");

    /// <summary>A song's spectrum in version 1 of its format, by its stream URL: the same, with <c>bars</c> for <c>stream</c>.</summary>
    public static string BarsUrl(string streamUrl) => streamUrl.Replace("/stream?", "/bars?");

    /// <summary>Has the server make a song's spectrum ahead of it being played, by its stream URL.</summary>
    public async Task WarmSpectrumAsync(string streamUrl, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(SpectrumUrl(streamUrl) + "&warm=true", ct);
    }

    public async Task<UpgradeStatus> GetUpgradesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<UpgradeStatus>($"{Api}/upgrades", MelodexJson.Options, ct))!;

    public async Task SaveUpgradeSettingsAsync(UpgradeSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/upgrades/settings", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Fetches a new file for a song in place of this one, better or not, passing over the one you have.</summary>
    public async Task<RequestInfo> ReplaceFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/files/{fileId}/replace", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RequestInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>The look through the library for upgrades that's going, or null when none is.</summary>
    public async Task<UpgradeRunInfo?> GetUpgradeRunAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/upgrades/run", ct);
        await EnsureAsync(response, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null
            : await response.Content.ReadFromJsonAsync<UpgradeRunInfo>(MelodexJson.Options, ct);
    }

    /// <summary>Stops the look through the library for upgrades that's going. False if none is.</summary>
    public async Task<bool> StopUpgradesAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/upgrades/stop", null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        await EnsureAsync(response, ct);
        return true;
    }

    /// <summary>Asks for upgrades of a song's, album's or artist's files below their cutoff, or the whole library's.</summary>
    public async Task<UpgradeSearch> SearchUpgradesAsync(FormatScope scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/upgrades/search", scope, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UpgradeSearch>(MelodexJson.Options, ct))!;
    }

    /// <summary>Files replaced by better copies that can be put back, of one song or all.</summary>
    public async Task<IReadOnlyList<HeldFileInfo>> GetHeldFilesAsync(Guid? trackId = null, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<List<HeldFileInfo>>($"{Api}/upgrades/held{(trackId is { } id ? $"?trackId={id}" : "")}", MelodexJson.Options, ct))!;

    /// <summary>Puts a replaced file back, deleting the copy that replaced it; its song is set never to be upgraded.</summary>
    /// <summary>Rolls an upgrade back; with never, its song is never upgraded again.</summary>
    public async Task RestoreHeldFileAsync(Guid id, bool never = false, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/upgrades/held/{id}/restore{(never ? "?never=true" : "")}", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task DeleteHeldFileAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/upgrades/held/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>An artist's, album's or track's quality setting and where it comes from.</summary>
    /// <param name="kind">"artists", "albums" or "tracks".</param>
    /// <param name="albumId">For a track, the album it's seen on.</param>
    public async Task<QualitySetting?> GetQualitySettingAsync(string kind, Guid id, Guid? albumId = null, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<QualitySetting>($"{Api}/{kind}/{id}/quality{(albumId is { } a ? $"?albumId={a}" : "")}", MelodexJson.Options, ct);

    /// <param name="kind">"artists", "albums" or "tracks".</param>
    public async Task SaveQualityOverrideAsync(string kind, Guid id, QualityOverride value, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/{kind}/{id}/quality", value, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Plugins ----

    public async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PluginInfo>>($"{Api}/plugins", MelodexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<AvailablePlugin>> ListAvailablePluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AvailablePlugin>>($"{Api}/plugins/available", MelodexJson.Options, ct) ?? [];

    public async Task<PluginInfo> AddPluginAsync(AddPlugin plugin, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins", plugin, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<PluginInfo> UpdatePluginAsync(Guid id, UpdatePlugin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/{id}", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task OrderPluginsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/order", new PluginOrder(ids), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<PluginCheckResult> RunPluginCheckAsync(Guid id, string check, RunPluginCheck body, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins/{id}/checks/{Uri.EscapeDataString(check)}", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginCheckResult>(MelodexJson.Options, ct))!;
    }

    public async Task<PluginInfo> TestPluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/plugins/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemovePluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/plugins/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Pausing and ordering ----

    public Task PauseRequestAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/pause", null, ct);

    public Task ResumeRequestAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/resume", null, ct);

    /// <summary>Moves a request to just before <paramref name="before"/> in its queue, or to the end.</summary>
    public Task MoveRequestAsync(Guid id, Guid? before, CancellationToken ct = default) => PostAsync($"{Api}/requests/{id}/move", new MoveInQueue(before), ct);

    public Task CancelTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/cancel", null, ct);

    /// <summary>Pauses, resumes or cancels many tasks at once.</summary>
    public Task<BulkChanged> ChangeTasksAsync(BulkChange change, CancellationToken ct = default) => BulkAsync("tasks", change, ct);

    /// <summary>Pauses, resumes or cancels many requests at once.</summary>
    public Task<BulkChanged> ChangeRequestsAsync(BulkChange change, CancellationToken ct = default) => BulkAsync("requests", change, ct);

    private async Task<BulkChanged> BulkAsync(string what, BulkChange change, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/{what}/bulk", change, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<BulkChanged>(MelodexJson.Options, ct))!;
    }

    public Task PauseTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/pause", null, ct);

    public Task ResumeTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/resume", null, ct);

    public Task MoveTaskAsync(Guid id, Guid? before, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/move", new MoveInQueue(before), ct);

    private async Task PostAsync(string url, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsJsonAsync(url, body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Access ----

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<AuthStatus>($"{Api}/auth/status", MelodexJson.Options, ct))!;

    /// <summary>Makes the admin account, the first time, and signs in with it.</summary>
    public async Task SetUpLoginAsync(SetUpLogin login, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/setup", login, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignInAsync(SignIn signIn, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/login", signIn, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/auth/logout", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task UpdateLoginAsync(UpdateLogin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/account", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<ServerVersion> GetVersionAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ServerVersion>($"{Api}/version", MelodexJson.Options, ct))!;

    /// <summary>Tells the server about an error the app couldn't handle, so it's in the server's log.</summary>
    public async Task ReportErrorAsync(ClientError error, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/client-errors", error, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<SetupStatus> GetSetupAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SetupStatus>($"{Api}/setup", MelodexJson.Options, ct))!;

    /// <summary>Setup is finished or skipped: the web app stops showing its steps.</summary>
    public async Task FinishSetupAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/setup/done", null, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Resets Melodex as it was first installed. The admin only; Melodex restarts.</summary>
    public async Task FactoryResetAsync(string confirm, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/setup/reset", new FactoryResetRequest(confirm), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Keeps how the web app looks with your account.</summary>
    public async Task SetAppearanceAsync(Appearance appearance, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/appearance", appearance, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Go back to everyone's look; returns it.</summary>
    public async Task<Appearance> ClearAppearanceAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/auth/appearance", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Appearance>(MelodexJson.Options, ct))!;
    }

    public async Task<Appearance> GetDefaultAppearanceAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<Appearance>($"{Api}/auth/appearance/default", MelodexJson.Options, ct))!;

    /// <summary>How Melodex looks for everyone who hasn't chosen their own look.</summary>
    public async Task SetDefaultAppearanceAsync(Appearance appearance, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/appearance/default", appearance, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<UserInfo>>($"{Api}/users", MelodexJson.Options, ct) ?? [];

    public async Task<UserInfo> CreateUserAsync(CreateUser user, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/users", user, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<UserInfo> UpdateUserAsync(Guid id, UpdateUser update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/users/{id}", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemoveUserAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/users/{id}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ApiKeyInfo>>($"{Api}/keys", MelodexJson.Options, ct) ?? [];

    /// <summary>Makes a key. Its <see cref="CreatedApiKey.Key"/> can't be had again, so show or keep it now.</summary>
    public async Task<CreatedApiKey> CreateApiKeyAsync(CreateApiKey key, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/keys", key, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKey>(MelodexJson.Options, ct))!;
    }

    public async Task RevokeApiKeyAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/keys/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Webhooks ----

    public async Task<IReadOnlyList<WebhookInfo>> ListWebhooksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookInfo>>($"{Api}/webhooks", MelodexJson.Options, ct) ?? [];

    public async Task<WebhookInfo> CreateWebhookAsync(SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/webhooks", webhook, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> UpdateWebhookAsync(Guid id, SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/webhooks/{id}", webhook, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> RotateWebhookSecretAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/secret", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(MelodexJson.Options, ct))!;
    }

    public async Task RemoveWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/webhooks/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Sends a ping now and says how it went.</summary>
    public async Task<WebhookDeliveryInfo> TestWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookDeliveryInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<WebhookDeliveryInfo>> ListWebhookDeliveriesAsync(Guid id, int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookDeliveryInfo>>($"{Api}/webhooks/{id}/deliveries?limit={limit}", MelodexJson.Options, ct) ?? [];

    // ---- Imports ----

    /// <summary>Reads a list without importing it: what kind it is, and for a CSV, what each column looks to hold.</summary>
    public async Task<ImportPeek> PeekImportAsync(PeekImport list, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/imports/peek", list, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportPeek>(MelodexJson.Options, ct))!;
    }

    /// <summary>Imports a list; its songs are matched in the background.</summary>
    public async Task<ImportSummary> CreateImportAsync(CreateImport import, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/imports", import, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportSummary>(MelodexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<ImportSummary>> ListImportsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ImportSummary>>($"{Api}/imports", MelodexJson.Options, ct) ?? [];

    public Task<ImportDetail?> GetImportAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<ImportDetail>($"{Api}/imports/{id}", ct);

    /// <summary>Chooses which song one of an import's is, or leaves it out or puts it back.</summary>
    public async Task<ImportLineInfo> ChangeImportLineAsync(Guid id, Guid lineId, ChangeImportLine change, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/imports/{id}/lines/{lineId}", change, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportLineInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<ImportApplied> ApplyImportAsync(Guid id, ApplyImport apply, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/imports/{id}/apply", apply, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportApplied>(MelodexJson.Options, ct))!;
    }

    public async Task DeleteImportAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/imports/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Imported songs nothing was found for, waiting in Review.</summary>
    public async Task<IReadOnlyList<UnmatchedImportSong>> GetUnmatchedImportSongsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<UnmatchedImportSong>>($"{Api}/imports/unmatched", MelodexJson.Options, ct) ?? [];

    public async Task MatchUnmatchedImportSongAsync(Guid lineId, MatchImportSong match, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/imports/unmatched/{lineId}/match", match, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task DismissUnmatchedImportSongAsync(Guid lineId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/imports/unmatched/{lineId}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Reads a music service's export of your data (the zip, or the files from it) without importing anything.</summary>
    public async Task<ServicePeek> PeekServiceImportAsync(IReadOnlyList<(string Name, Stream Content)> files, CancellationToken ct = default)
    {
        using var form = Form(files);
        using var response = await http.PostAsync($"{Api}/imports/services/peek", form, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ServicePeek>(MelodexJson.Options, ct))!;
    }

    /// <summary>Imports the chosen lists of a music service's export, an import each.</summary>
    public async Task<ImportBatch> CreateServiceImportAsync(IReadOnlyList<(string Name, Stream Content)> files, CreateServiceImport choice, CancellationToken ct = default)
    {
        using var form = Form(files);
        form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(choice, MelodexJson.Options)), "choice");
        using var response = await http.PostAsync($"{Api}/imports/services", form, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportBatch>(MelodexJson.Options, ct))!;
    }

    public Task<ImportBatch?> GetImportBatchAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<ImportBatch>($"{Api}/imports/batches/{id}", ct);

    /// <summary>Applies every import of a batch that's ready.</summary>
    public async Task<ImportBatchApplied> ApplyImportBatchAsync(Guid id, ApplyImportBatch body, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/imports/batches/{id}/apply", body, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ImportBatchApplied>(MelodexJson.Options, ct))!;
    }

    private static MultipartFormDataContent Form(IReadOnlyList<(string Name, Stream Content)> files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
            form.Add(new StreamContent(content), "files", name);
        return form;
    }

    // ---- Exports ----

    /// <summary>
    /// Where playlists are exported: one as an M3U8 or CSV file, several as a zip of them. Songs you don't have are left out,
    /// or kept by name only; an M3U8's paths are relative to the music folder, or under <paramref name="prefix"/>.
    /// </summary>
    public static string PlaylistExportUrl(IReadOnlyList<Guid> ids, PlaylistExportFormat format = PlaylistExportFormat.M3u8, bool keepMissing = false, string? prefix = null)
    {
        var query = ids.Select(id => $"id={id}").Append($"format={format}").Append($"keepMissing={(keepMissing ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(prefix))
            query = query.Append($"prefix={Uri.EscapeDataString(prefix.Trim())}");
        return $"{Api}/exports/playlists?{string.Join('&', query)}";
    }

    /// <summary>Where the library is exported, as a CSV with a row for each song you have or each album, and the columns given.</summary>
    public static string LibraryExportUrl(LibraryExportRows rows, IReadOnlyList<LibraryExportColumn>? columns = null) =>
        $"{Api}/exports/library?rows={rows}{string.Concat((columns ?? []).Select(c => $"&column={c}"))}";

    /// <summary>Exports playlists: the file, or a zip of them, as <see cref="PlaylistExportUrl"/> says.</summary>
    public async Task<byte[]> ExportPlaylistsAsync(IReadOnlyList<Guid> ids, PlaylistExportFormat format = PlaylistExportFormat.M3u8, bool keepMissing = false,
        string? prefix = null, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(PlaylistExportUrl(ids, format, keepMissing, prefix), ct);
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Exports the library as a CSV.</summary>
    public async Task<byte[]> ExportLibraryAsync(LibraryExportRows rows, IReadOnlyList<LibraryExportColumn>? columns = null, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(LibraryExportUrl(rows, columns), ct);
        await EnsureAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ---- Playlists ----

    /// <summary>Your wishlist, less what the library has now.</summary>
    public async Task<Wishlist> GetWishlistAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<Wishlist>($"{Api}/wishlist", MelodexJson.Options, ct))!;

    /// <summary>Asks for everything on your wishlist; how many were asked for.</summary>
    public async Task<AskedAgain> GetWishlistItemsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/wishlist/get", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AskedAgain>(MelodexJson.Options, ct))!;
    }

    public async Task AddToWishlistAsync(AddToWishlist add, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/wishlist", add, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Takes a song or album off your wishlist, by its track or album ID.</summary>
    public async Task RemoveFromWishlistAsync(Guid targetId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/wishlist/{targetId}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Your stars and ratings, the latest first.</summary>
    public async Task<IReadOnlyList<ItemMark>> ListMarksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ItemMark>>($"{Api}/marks", MelodexJson.Options, ct) ?? [];

    /// <summary>Stars, unstars or rates a song (by track ID), album or artist; what's left of its mark, or null once it has neither.</summary>
    public async Task<ItemMark?> SetMarkAsync(MarkKind kind, Guid id, SetMark change, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/marks/{kind.ToString().ToLowerInvariant()}/{id}", change, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ItemMark>(MelodexJson.Options, ct);
    }

    /// <summary>Your play queue as you or a Subsonic player left it, or null if there's none.</summary>
    public async Task<PlayQueue?> GetPlayQueueAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Api}/player/queue", ct);
        await EnsureAsync(response, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<PlayQueue>(MelodexJson.Options, ct);
    }

    /// <summary>Keeps your play queue for the web player and Subsonic players; an empty one clears it.</summary>
    public async Task<PlayQueue?> SavePlayQueueAsync(SavePlayQueue queue, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/player/queue", queue, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<PlayQueue>(MelodexJson.Options, ct);
    }

    // ---- Listening history (#2) ----

    /// <summary>Your plays, the latest first.</summary>
    public async Task<PlayPage> ListPlaysAsync(int skip = 0, int take = 50, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<PlayPage>($"{Api}/plays?skip={skip}&take={take}", MelodexJson.Options, ct))!;

    /// <summary>Your most played songs, albums or artists over a period, the most first.</summary>
    public async Task<IReadOnlyList<TopItem>> ListMostPlayedAsync(TopKind kind, PlayPeriod period = PlayPeriod.All, int take = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<TopItem>>($"{Api}/plays/top/{kind.ToString().ToLowerInvariant()}s?period={period}&take={take}", MelodexJson.Options, ct) ?? [];

    /// <summary>Records a song you played; the play already recorded if it's the same one again.</summary>
    public async Task<PlayRecord> RecordPlayAsync(RecordPlay play, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plays", play, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlayRecord>(MelodexJson.Options, ct))!;
    }

    /// <summary>Says what you've started playing.</summary>
    public async Task SetNowPlayingAsync(RecordPlay play, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plays/now", play, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Takes a play out of your history.</summary>
    public async Task DeletePlayAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/plays/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Your linked ListenBrainz and Last.fm accounts.</summary>
    public async Task<ScrobbleStatus> GetScrobblingAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScrobbleStatus>($"{Api}/account/scrobbling", MelodexJson.Options, ct))!;

    public async Task<ScrobbleLink> LinkListenBrainzAsync(string token, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/account/scrobbling/listenbrainz", new LinkListenBrainz(token), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ScrobbleLink>(MelodexJson.Options, ct))!;
    }

    public async Task<ScrobbleLink> LinkLastFmAsync(string token, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/account/scrobbling/lastfm", new LinkLastFm(token), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ScrobbleLink>(MelodexJson.Options, ct))!;
    }

    public async Task UnlinkScrobblingAsync(ScrobbleService service, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/account/scrobbling/{service.ToString().ToLowerInvariant()}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<ScrobbleSettingsInfo> GetScrobblingSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScrobbleSettingsInfo>($"{Api}/settings/scrobbling", MelodexJson.Options, ct))!;

    public async Task SaveScrobblingSettingsAsync(SaveScrobbleSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/scrobbling", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>What the discovery plugins that are on can find.</summary>
    public async Task<DiscoveryStatus> GetDiscoveryAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<DiscoveryStatus>($"{Api}/discovery", MelodexJson.Options, ct))!;

    /// <summary>Artists like this one, the closest first.</summary>
    public async Task<IReadOnlyList<SimilarArtist>> ListSimilarArtistsAsync(Guid artistId, int take = 20, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SimilarArtist>>($"{Api}/artists/{artistId}/similar?take={take}", MelodexJson.Options, ct) ?? [];

    /// <summary>The next songs for a radio (#4); null when what it was started from is gone.</summary>
    public async Task<RadioBatch?> NextRadioAsync(RadioNext next, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/radio", next, MelodexJson.Options, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<RadioBatch>(MelodexJson.Options, ct);
    }

    /// <summary>Says a radio's song was skipped within its first half minute, so radios steer away from it and songs like it.</summary>
    public async Task SkipRadioSongAsync(Guid trackId, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/radio/skips", new RadioSkipped(trackId), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<RadioSettings> GetRadioSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<RadioSettings>($"{Api}/radio/settings", MelodexJson.Options, ct))!;

    public async Task SaveRadioSettingsAsync(RadioSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/radio/settings", settings, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Songs like this one, the closest first.</summary>
    public async Task<IReadOnlyList<DiscoveredSong>> ListSimilarSongsAsync(Guid trackId, int take = 20, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DiscoveredSong>>($"{Api}/tracks/{trackId}/similar?take={take}", MelodexJson.Options, ct) ?? [];

    /// <summary>Songs recommended for you, the best first.</summary>
    public async Task<IReadOnlyList<DiscoveredSong>> ListRecommendationsAsync(int take = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DiscoveredSong>>($"{Api}/discovery/recommendations?take={take}", MelodexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<PlaylistSummary>> ListPlaylistsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PlaylistSummary>>($"{Api}/playlists", MelodexJson.Options, ct) ?? [];

    public Task<PlaylistDetail?> GetPlaylistAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<PlaylistDetail>($"{Api}/playlists/{id}", ct);

    public async Task<PlaylistDetail> CreatePlaylistAsync(CreatePlaylist playlist, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/playlists", playlist, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistDetail>(MelodexJson.Options, ct))!;
    }

    public async Task<PlaylistSummary> UpdatePlaylistAsync(Guid id, UpdatePlaylist update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/playlists/{id}", update, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistSummary>(MelodexJson.Options, ct))!;
    }

    public async Task<PlaylistAdded> AddToPlaylistAsync(Guid id, AddToPlaylist songs, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/playlists/{id}/songs", songs, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistAdded>(MelodexJson.Options, ct))!;
    }

    /// <summary>How much of these songs each of your playlists holds.</summary>
    public async Task<IReadOnlyList<PlaylistHolding>> GetPlaylistsHoldingAsync(AddToPlaylist songs, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/playlists/holding", songs, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<PlaylistHolding>>(MelodexJson.Options, ct))!;
    }

    /// <summary>Takes these songs out of a playlist, wherever they are in it.</summary>
    public async Task<PlaylistTakenOut> TakeOutOfPlaylistAsync(Guid id, AddToPlaylist songs, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/playlists/{id}/songs/remove", songs, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistTakenOut>(MelodexJson.Options, ct))!;
    }

    public async Task<PlaylistDetail> ArrangePlaylistAsync(Guid id, ArrangePlaylist arrangement, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/playlists/{id}/songs", arrangement, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistDetail>(MelodexJson.Options, ct))!;
    }

    public async Task DeletePlaylistAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/playlists/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Pins the playlist to show first, or unpins it.</summary>
    public async Task<PlaylistSummary> PinPlaylistAsync(Guid id, bool pinned, CancellationToken ct = default)
    {
        using var response = pinned
            ? await http.PutAsync($"{Api}/playlists/{id}/pin", null, ct)
            : await http.DeleteAsync($"{Api}/playlists/{id}/pin", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistSummary>(MelodexJson.Options, ct))!;
    }

    /// <summary>Gives the playlist a picture of its own, or with null, takes it away.</summary>
    /// <summary>Your folders of playlists kept for themselves: those made empty, and those with pictures of their own.</summary>
    public async Task<IReadOnlyList<PlaylistFolderInfo>> GetPlaylistFoldersAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<IReadOnlyList<PlaylistFolderInfo>>($"{Api}/playlists/folders", MelodexJson.Options, ct))!;

    /// <summary>Makes a folder of playlists, kept even while it's empty.</summary>
    public async Task<PlaylistFolderInfo> CreatePlaylistFolderAsync(string path, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/playlists/folders", new CreatePlaylistFolder(path), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistFolderInfo>(MelodexJson.Options, ct))!;
    }

    /// <summary>Forgets a folder kept for itself, and its picture; the playlists in it stay.</summary>
    public async Task DeletePlaylistFolderAsync(string path, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/playlists/folders?path={Uri.EscapeDataString(path)}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            await EnsureAsync(response, ct);
    }

    /// <summary>Gives a folder of playlists a picture of its own, or with null takes it away.</summary>
    public async Task<PlaylistFolderInfo> SetPlaylistFolderCoverAsync(string path, string? upload, CancellationToken ct = default)
    {
        using var response = upload is null
            ? await http.DeleteAsync($"{Api}/playlists/folders/cover?path={Uri.EscapeDataString(path)}", ct)
            : await http.PutAsJsonAsync($"{Api}/playlists/folders/cover", new SetPlaylistFolderCover(path, upload), MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistFolderInfo>(MelodexJson.Options, ct))!;
    }

    public async Task<PlaylistSummary> SetPlaylistCoverAsync(Guid id, SetPlaylistCover? cover, CancellationToken ct = default)
    {
        using var response = cover is null
            ? await http.DeleteAsync($"{Api}/playlists/{id}/cover", ct)
            : await http.PutAsJsonAsync($"{Api}/playlists/{id}/cover", cover, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistSummary>(MelodexJson.Options, ct))!;
    }

    public async Task RenamePlaylistFolderAsync(RenamePlaylistFolder rename, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/playlists/folders", rename, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<PlaylistSyncStatus> GetPlaylistSyncAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<PlaylistSyncStatus>($"{Api}/playlists/sync", MelodexJson.Options, ct))!;

    /// <summary>Syncs your playlists with the media server now, and says how it went.</summary>
    public async Task<PlaylistSyncStatus> SyncPlaylistsAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/playlists/sync", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistSyncStatus>(MelodexJson.Options, ct))!;
    }

    /// <summary>Links your media server login, or someone else's with <paramref name="userId"/>, after checking it works.</summary>
    public async Task<PlaylistSyncStatus> LinkMediaServerAsync(LinkMediaServer login, Guid? userId = null, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(userId is { } id ? $"{Api}/users/{id}/media-server" : $"{Api}/auth/media-server", login, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PlaylistSyncStatus>(MelodexJson.Options, ct))!;
    }

    public async Task UnlinkMediaServerAsync(Guid? userId = null, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync(userId is { } id ? $"{Api}/users/{id}/media-server" : $"{Api}/auth/media-server", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Your login on each media server that syncs playlists, or someone else's with <paramref name="userId"/>.</summary>
    public async Task<IReadOnlyList<MediaServerAccount>> ListMediaServerAccountsAsync(Guid? userId = null, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<List<MediaServerAccount>>(userId is { } id ? $"{Api}/users/{id}/media-servers" : $"{Api}/auth/media-servers", MelodexJson.Options, ct))!;

    /// <summary>Links a login on one media server, in use or not, after checking it works there.</summary>
    public async Task<MediaServerAccount> LinkMediaServerAccountAsync(Guid serverId, LinkMediaServer login, Guid? userId = null, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{AccountsOf(userId)}/{serverId}", login, MelodexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<MediaServerAccount>(MelodexJson.Options, ct))!;
    }

    public async Task UnlinkMediaServerAccountAsync(Guid serverId, Guid? userId = null, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{AccountsOf(userId)}/{serverId}", ct);
        await EnsureAsync(response, ct);
    }

    private static string AccountsOf(Guid? userId) => userId is { } id ? $"{Api}/users/{id}/media-servers" : $"{Api}/auth/media-servers";

    /// <summary>Throws <see cref="MelodexApiException"/> with the server's own explanation for anything but success.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(MelodexJson.Options, ct);
            if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == System.Text.Json.JsonValueKind.Object)
                message = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            else if (problem.TryGetProperty("detail", out var detail))
                message = detail.GetString();
            else if (problem.TryGetProperty("title", out var title))
                message = title.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        throw new MelodexApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : message);
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(MelodexJson.Options, ct);
    }
}

/// <summary>The server refused a request, with its reason.</summary>
public sealed class MelodexApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
