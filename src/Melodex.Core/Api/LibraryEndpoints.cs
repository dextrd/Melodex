using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Plugins;
using Melodex.Core.Artwork;
using Melodex.Core.Catalogue;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;

namespace Melodex.Core.Api;

public static class LibraryEndpoints
{
    /// <summary>How long a request for a picture waits for it to be fetched before answering 503.</summary>
    private static readonly TimeSpan ArtworkPatience = TimeSpan.FromMilliseconds(100);

    // Covers show from the browser's cache straight away, and are checked with the server behind: a week of that.
    private const string CoverCaching = "max-age=0, stale-while-revalidate=604800";

    /// <summary>How long the artwork status waits for pictures it was asked about before answering.</summary>
    private static readonly TimeSpan ArtworkStatusPatience = TimeSpan.FromMilliseconds(250);

    private static readonly System.Text.RegularExpressions.Regex ArtworkPath =
        new(@"artwork/(artist|album|edition)/([0-9a-fA-F-]{36})", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<IResult> TaskActionAsync(Guid id, MelodexDbContext db, CancellationToken ct, Func<BackgroundTask, Task<bool>> action)
    {
        if (await db.BackgroundTasks.FindAsync([id], ct) is not { } task)
            return Results.NotFound();
        return await action(task) ? Results.Ok((await TitledAsync(db, [task], ct))[0]) : Results.Conflict();
    }

    // The first of the files with lyrics, or none.
    private static TrackLyrics LyricsOf(IReadOnlyList<string> paths, string libraryPath)
    {
        var root = Path.GetFullPath(libraryPath);
        return paths.Select(p => LyricsFiles.Lyrics(Path.Combine(root, p))).FirstOrDefault(l => l.Kind != LyricsKind.None)
               ?? new TrackLyrics(LyricsKind.None, [], null);
    }

    // A song's lyrics as its files have them, with what's been decided about them.
    private static TrackLyrics LyricsOf(IReadOnlyList<LibraryFile> files, string libraryPath)
    {
        var root = Path.GetFullPath(libraryPath);
        var lyrics = files.Select(f => LyricsFiles.Read(Path.Combine(root, f.Path))).FirstOrDefault(l => l is not null) is { } found
            ? new TrackLyrics(LyricsFiles.KindOfText(found.Text), LyricsFiles.Parse(found.Text), found.Source, found.Text)
            : new TrackLyrics(LyricsKind.None, [], null);
        return lyrics with
        {
            Excluded = files.Any(f => f.LyricsExcludedAt != null),
            CheckedAt = files.Max(f => f.LyricsCheckedAt),
            Instrumental = lyrics.Kind == LyricsKind.None && files.Any(f => f.Lyrics == LyricsKind.Instrumental),
        };
    }

    private static Task<List<LibraryFile>> SongFilesAsync(MelodexDbContext db, Guid trackId, CancellationToken ct) =>
        db.LibraryFiles.Where(f => f.TrackId == trackId && f.ExcludedAt == null).OrderBy(f => f.Path).ToListAsync(ct);

    private static IResult WorkerDown(HttpRequestException e) =>
        Results.Problem($"The beets worker, which writes the music folder, couldn't be reached: {e.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);

    // Far longer than any song's lyrics.
    private const int MaxLyrics = 200_000;

    private static async Task<IResult> SaveLyricsAsync(Guid id, string? text, LyricsFinder finder, MelodexDbContext db, IOptions<MelodexOptions> options,
        CancellationToken ct)
    {
        var files = await SongFilesAsync(db, id, ct);
        if (files.Count == 0)
            return Results.NotFound();
        string? problem;
        try
        {
            problem = await finder.SaveAsync(files, text, ct);
        }
        catch (HttpRequestException e)
        {
            return WorkerDown(e);
        }
        return problem is null
            ? Results.Ok(LyricsOf(await SongFilesAsync(db, id, ct), options.Value.LibraryPath))
            : Results.Problem($"Couldn't write the lyrics: {problem}", statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> ExcludeLyricsAsync(Guid id, bool excluded, MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct)
    {
        var files = await SongFilesAsync(db, id, ct);
        if (files.Count == 0)
            return Results.NotFound();
        foreach (var file in files)
            file.LyricsExcludedAt = excluded ? file.LyricsExcludedAt ?? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(LyricsOf(files, options.Value.LibraryPath));
    }

    private static CoverSubject CoverSubjectOf(string subject) => subject == "albums" ? CoverSubject.Album : CoverSubject.Track;

    private static EntityKind? ArtworkKind(string kind) =>
        kind switch { "artist" => EntityKind.Artist, "album" => EntityKind.Album, "edition" => EntityKind.Edition, "track" => EntityKind.Track, _ => null };

    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var library = api.MapGroup("/library").WithTags("Library");

        library.MapGet("", async (MelodexDbContext db, LibraryReading reading, LibraryCache cache, CancellationToken ct) =>
            {
                var hidden = await db.LibraryFiles.CountAsync(f => f.HiddenAt != null, ct);
                var files = await db.LibraryFiles.CountAsync(ct) - hidden;
                var matched = await db.LibraryFiles.CountAsync(f => f.TrackId != null, ct);
                var pending = await db.LibraryFiles.CountAsync(f => f.MatchedAt == null && f.ExcludedAt == null && f.HiddenAt == null, ct);
                var excluded = await db.LibraryFiles.CountAsync(f => f.ExcludedAt != null, ct);
                // Deferred files being tried again by a scan count as pending, not deferred.
                var deferred = await db.LibraryFiles.CountAsync(f => f.DeferredAt != null && f.TrackId == null && f.MatchedAt != null, ct);
                var lastScan = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Succeeded)
                    .MaxAsync(t => (DateTime?)t.FinishedAt, ct);
                var current = await db.BackgroundTasks.Where(t => t.Status == BackgroundTaskStatus.Running || t.Status == BackgroundTaskStatus.Queued)
                    .OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
                var lastFinished = await db.BackgroundTasks.Where(t => t.Kind == LibraryScanTask.TaskKind && t.FinishedAt != null)
                    .OrderByDescending(t => t.FinishedAt).FirstOrDefaultAsync(ct);
                var titled = await TitledAsync(db, new[] { current, lastFinished }.OfType<BackgroundTask>().ToList(), ct);
                return new LibraryStatus(files, matched, files - matched - pending - excluded - deferred, pending, lastScan,
                    current is null ? null : titled[0], lastFinished is null ? null : titled[^1], await reading.IsBusyAsync(db, ct), excluded, deferred, hidden,
                    cache.Preparing);
            })
            .WithName("GetLibraryStatus").WithSummary("File counts, the last scan, and any task in progress.");

        library.MapPost("/scan", async ([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ScanLibrary? body, TaskQueue queue, CancellationToken ct) =>
            {
                var task = await queue.EnqueueAsync(LibraryScanTask.TaskKind, LibraryScanTask.Payload(body?.Rematch ?? Rematch.None), ct);
                return Results.Accepted(null, TaskQueue.ToInfo(task) with { Title = LibraryScanTask.Title(task.PayloadJson) });
            })
            .RequireScope(Scopes.Scan).WithName("ScanLibrary")
            .WithSummary("Scan the music folder, match new and changed files and update the catalogue. With rematch, match files already "
                         + "matched or under review again too. The body can be left out. Returns the queued task.")
            .Produces<TaskInfo>(StatusCodes.Status202Accepted);

        // Big lists, so each carries an ETag: send it back as If-None-Match and an unchanged list is a 304 with no body.
        library.MapGet("/artists", (LibraryCache cache, HttpContext http, CancellationToken ct) => cache.SendAsync(LibraryCache.Artists, http, ct))
            .WithName("GetLibraryArtists").Produces<List<LibraryArtist>>().Produces(StatusCodes.Status304NotModified)
            .WithSummary("Artists in the library, with owned, missing and covered album counts. May lag a scan by a few seconds. Supports If-None-Match.");

        library.MapGet("/albums", (LibraryCache cache, HttpContext http, CancellationToken ct) => cache.SendAsync(LibraryCache.Albums, http, ct))
            .WithName("GetLibraryAlbums").Produces<List<LibraryAlbum>>().Produces(StatusCodes.Status304NotModified)
            .WithSummary("Albums you have some or all of, by artist. May lag a scan by a few seconds. Supports If-None-Match.");

        library.MapGet("/tracks", (LibraryCache cache, HttpContext http, CancellationToken ct) => cache.SendAsync(LibraryCache.Tracks, http, ct))
            .WithName("GetLibraryTracks").Produces<List<LibraryTrack>>().Produces(StatusCodes.Status304NotModified)
            .WithSummary("Every matched file, by artist and album. May lag a scan by a few seconds. Supports If-None-Match.");

        library.MapGet("/missing", (LibraryView view, CancellationToken ct) => view.GetMissingAsync(ct))
            .WithName("GetMissing").WithSummary("Missing albums across the library, after the default filters and ignores.");

        library.MapGet("/scan/files", (ScanFeed feed, long? since) => feed.Since(since ?? 0))
            .WithName("ScanFiles")
            .WithSummary("The files the running or last scan read or matched, in the order it first saw them: each file's name while it's read, "
                         + "then the song it matched or why it needs review. Pass since (the last response's latest) to get only what changed.");

        library.MapGet("/removal", (LibraryItemKind kind, Guid id, LibraryRemover remover, CancellationToken ct) => remover.PreviewAsync(kind, id, ct))
            .RequireScope(Scopes.Remove).WithName("GetRemoval").WithSummary("What removing an artist, album or file from the library would affect: how many files, their size and folders.");

        library.MapPost("/remove", async (RemoveFromLibrary body, LibraryRemover remover, CancellationToken ct) =>
            {
                var result = await remover.RemoveAsync(body, ct);
                return result.Removed == 0 && result.Failed.Count == 0 ? Results.NotFound() : Results.Ok(result);
            })
            .RequireScope(Scopes.Remove).WithName("RemoveFromLibrary")
            .WithSummary("Remove an artist, album or file from the library: delete its files from the music folder and tell the media server, "
                         + "or keep them and exclude them from matching. Files that couldn't be deleted are listed and stay in the library.")
            .Produces<RemovalResult>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/format", async (FormatScopeKind kind, Guid? id, LibraryFormatter formatter, CancellationToken ct) =>
                await formatter.PlanAsync(new FormatScope(kind, id), ct) is { } plan ? Results.Ok(plan) : Results.NotFound())
            .RequireScope(Scopes.Format).WithName("GetFormatPlan")
            .WithSummary("Before formatting the whole library or an artist, album or track: how many files it covers and which of them can be formatted "
                         + "(those matched to the catalogue), the sources each part of the metadata can come from, and whether the beets worker is ready.")
            .Produces<FormatPlan>().Produces(StatusCodes.Status404NotFound);

        library.MapPost("/format", async (StartFormat body, LibraryFormatter formatter, CancellationToken ct) =>
            {
                if (body.Scope.Kind != FormatScopeKind.Library && body.Scope.Id is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["scope.id"] = ["Say which artist, album or track."] });
                if (await formatter.ValidateAsync(body.Options, ct) is { Count: > 0 } errors)
                    return Results.ValidationProblem(errors);
                try
                {
                    var task = await formatter.StartAsync(body, ct);
                    return Results.Ok(TaskQueue.ToInfo(task) with { Title = FormatTask.Title(task.PayloadJson) });
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound();
                }
            })
            .RequireScope(Scopes.Format).WithName("StartFormat")
            .WithSummary("Rewrite files in the library: their tags from the catalogue, their embedded cover from an artwork plugin, and, if asked, "
                         + "where they live, by the path template. Each part can keep the file's own instead. It runs in the background "
                         + "like a scan; only matched files are formatted. This writes your music files.")
            .Produces<TaskInfo>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/{subject:regex(^(albums|tracks)$)}/{id:guid}/cover", async (string subject, Guid id, CoverChooser covers, CancellationToken ct) =>
                await covers.ChoicesAsync(CoverSubjectOf(subject), id, ct) is { } choices ? Results.Ok(choices) : Results.NotFound())
            .RequireScope(Scopes.Format).WithName("GetCoverChoices")
            .WithSummary("The covers an album or song could have: the usual cover and every edition's, of every release it's on (for a song, its "
                         + "singles, EPs and compilations too), and its files' own picture. Pictures not fetched yet load like other covers.")
            .Produces<CoverChoices>().Produces(StatusCodes.Status404NotFound);

        library.MapPut("/{subject:regex(^(albums|tracks)$)}/{id:guid}/cover", async (string subject, Guid id, ChooseCover body, CoverChooser covers, CancellationToken ct) =>
            {
                try
                {
                    return await covers.ChooseAsync(CoverSubjectOf(subject), id, body, ct) is { } chosen ? Results.Ok(chosen) : Results.NotFound();
                }
                catch (CoverRefusedException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
                }
                catch (HttpRequestException e)
                {
                    return Results.Problem($"The beets worker, which writes the music folder, couldn't be reached: {e.Message}",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .RequireScope(Scopes.Format).WithName("ChooseCover")
            .WithSummary("Set an album's or song's cover, from one of its cover choices or an uploaded picture (base64, up to 10 MB). It's embedded in "
                         + "the files straight away, an album's folders get it as their folder image, and Melodex shows it from then on. "
                         + "This writes your music files.")
            .Produces<CoverChosen>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        library.MapGet("/tracks/{id:guid}/lyrics", async (Guid id, MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
                await SongFilesAsync(db, id, ct) is { Count: > 0 } files
                    ? Results.Ok(LyricsOf(files, options.Value.LibraryPath))
                    : Results.NotFound())
            .RequireScope(Scopes.Read).WithName("GetTrackLyrics")
            .WithSummary("A song's lyrics, as its files in the library have them: from an .lrc file beside one, or its lyrics tag. Synced lyrics come "
                         + "line by line with when each is sung, and as the text stored. Says too whether the song is excluded from lyrics matching, "
                         + "and when the lyrics sources last had none. 404 when the song has no files.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/files/{id:guid}/lyrics", async (Guid id, MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
                await db.LibraryFiles.Where(f => f.Id == id).Select(f => f.Path).FirstOrDefaultAsync(ct) is { } path
                    ? Results.Ok(LyricsOf([path], options.Value.LibraryPath))
                    : Results.NotFound())
            .RequireScope(Scopes.Read).WithName("GetFileLyrics")
            .WithSummary("A file's lyrics: from an .lrc file beside it, or its lyrics tag.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound);

        library.MapPost("/tracks/{id:guid}/lyrics", async (Guid id, LyricsFinder finder, MelodexDbContext db, IOptions<MelodexOptions> options,
                CancellationToken ct) =>
            {
                var files = await db.LibraryFiles.Where(f => f.TrackId == id && f.ExcludedAt == null).OrderBy(f => f.Path).ToListAsync(ct);
                if (files.Count == 0)
                    return Results.NotFound();
                if (!await finder.HaveSourcesAsync(ct))
                    return Results.Problem("There are no lyrics sources. Add one on the Plugins page.", statusCode: StatusCodes.Status409Conflict);
                LyricsFinder.Outcome outcome;
                try
                {
                    outcome = await finder.FindAsync(files.Select(f => f.Id).ToList(), again: true, ct);
                }
                catch (HttpRequestException e)
                {
                    return WorkerDown(e);
                }
                if (outcome is { Found: 0, Failed: > 0 })
                    return Results.Problem($"Couldn't get the lyrics: {outcome.Problem}", statusCode: StatusCodes.Status502BadGateway);
                return Results.Ok(LyricsOf(await SongFilesAsync(db, id, ct), options.Value.LibraryPath));
            })
            .RequireScope(Scopes.Format).WithName("FindTrackLyrics")
            .WithSummary("Look for a song's lyrics now through the lyrics plugins, replacing any it has, and write them where the settings say: an .lrc "
                         + "file beside each of its files, their lyrics tag, or both. Answers with the lyrics as they now are; none, if no source has "
                         + "any. This writes your music files.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway).Produces(StatusCodes.Status503ServiceUnavailable);

        library.MapPut("/tracks/{id:guid}/lyrics", async (Guid id, SaveLyrics body, LyricsFinder finder, MelodexDbContext db,
                IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                if (body.Text.Length > MaxLyrics)
                    return Results.Problem("Those lyrics are too long.", statusCode: StatusCodes.Status400BadRequest);
                return await SaveLyricsAsync(id, string.IsNullOrWhiteSpace(body.Text) ? null : body.Text, finder, db, options, ct);
            })
            .RequireScope(Scopes.Format).WithName("SaveTrackLyrics")
            .WithSummary("Write a song's lyrics, LRC or plain text, to its files where the settings say, replacing any they have: an .lrc file "
                         + "beside each, their lyrics tag, or both. Empty text takes them away. This writes your music files.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway).Produces(StatusCodes.Status503ServiceUnavailable);

        library.MapDelete("/tracks/{id:guid}/lyrics", (Guid id, LyricsFinder finder, MelodexDbContext db, IOptions<MelodexOptions> options,
                CancellationToken ct) => SaveLyricsAsync(id, null, finder, db, options, ct))
            .RequireScope(Scopes.Format).WithName("DeleteTrackLyrics")
            .WithSummary("Take a song's lyrics away: the .lrc file beside each of its files, and their lyrics tag. Scans look for them again "
                         + "unless the song is excluded from lyrics matching. This writes your music files.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway).Produces(StatusCodes.Status503ServiceUnavailable);

        library.MapGet("/tracks/{id:guid}/lyrics/search", async (Guid id, string? q, LyricsFinder finder, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await SongFilesAsync(db, id, ct) is not [var file, ..])
                    return Results.NotFound();
                try
                {
                    return Results.Ok(await finder.SearchAsync(file, q, ct));
                }
                catch (HttpRequestException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
                }
            })
            .RequireScope(Scopes.Format).WithName("SearchTrackLyrics")
            .WithSummary("Lyrics the lyrics plugins that can search have that might be the song's, to choose from: for q, or else the song's title "
                         + "and artist. Those of the song's length come first. Choosing one is saving its text.")
            .Produces<IReadOnlyList<LyricsChoice>>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status502BadGateway);

        library.MapPut("/tracks/{id:guid}/lyrics/excluded", (Guid id, MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
                ExcludeLyricsAsync(id, true, db, options, ct))
            .RequireScope(Scopes.Format).WithName("ExcludeTrackLyrics")
            .WithSummary("Exclude a song from lyrics matching: no scan looks for its lyrics. What it has stays.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound);

        library.MapDelete("/tracks/{id:guid}/lyrics/excluded", (Guid id, MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
                ExcludeLyricsAsync(id, false, db, options, ct))
            .RequireScope(Scopes.Format).WithName("IncludeTrackLyrics")
            .WithSummary("Include a song in lyrics matching again, so scans look for its lyrics if it has none.")
            .Produces<TrackLyrics>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/review", (LibraryView view, ReviewList? list, bool? excluded, CancellationToken ct) =>
                excluded == true && list is null ? view.GetExcludedAsync(ct) : view.GetReviewAsync(list ?? ReviewList.Review, ct))
            .RequireScope(Scopes.Review).WithName("GetReview")
            .WithSummary("Files that couldn't be matched confidently, with the best suggestion. With list, the files set aside instead: excluded "
                         + "ones the library shows by their own tags, excluded ones missing a title or artist (incomplete), deferred or hidden. "
                         + "excluded=true, from before there were lists, gives every excluded file.");

        library.MapPut("/files/{id:guid}/set-aside", async (Guid id, SetFileAside body, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                SetAsides.Apply(file, body.As);
                await db.SaveChangesAsync(ct);
                await LibraryMatcher.UpdateLibraryArtistsAsync(db, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Review).WithName("SetFileAside")
            .WithSummary("Exclude a file from matching (it stays in the library as its own tags say), defer it (it leaves the review list, "
                         + "and each scan tries it again, matching it only when it beats the threshold) or hide it from the library. "
                         + "None puts it back, to be matched at the next scan.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        library.MapPost("/review/set-aside", async (SetFilesAside body, MelodexDbContext db, CancellationToken ct) =>
            {
                var ids = body.FileIds.Distinct().ToList();
                var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
                foreach (var file in files)
                    SetAsides.Apply(file, body.As);
                await db.SaveChangesAsync(ct);
                await LibraryMatcher.UpdateLibraryArtistsAsync(db, ct);
                return new ReviewDone(files.Count);
            })
            .RequireScope(Scopes.Review).WithName("SetFilesAside").WithSummary("Set each of these files aside, or put them back, as doing each would.");

        library.MapPut("/files/{id:guid}/excluded", async (Guid id, ExcludeFile body, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                if (body.Excluded)
                    SetAsides.Apply(file, SetAside.Excluded);
                else if (file.ExcludedAt is not null)
                    SetAsides.Apply(file, SetAside.None);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Review).WithName("ExcludeFile")
            .WithSummary("Exclude a file from matching, for music no catalogue knows: it's never matched and leaves the review list. "
                         + "Including it again matches it at the next scan. Set-aside does this and more.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        library.MapPost("/files/{id:guid}/match", async (Guid id, MatchFile body, MelodexDbContext db, RequestPlanner planner, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } file)
                    return Results.NotFound();
                if (await db.Tracks.FindAsync([body.TrackId], ct) is not { } track)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["trackId"] = ["No such track in the catalogue."] });
                var editionId = body.EditionId
                                ?? (file.SuggestedTrackId == track.Id ? file.SuggestedEditionId : null)
                                ?? await planner.ChooseEditionForTrackAsync(track.Id, ct);
                if (editionId is { } edition && !await db.EditionTracks.AnyAsync(et => et.EditionId == edition && et.TrackId == track.Id, ct))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["editionId"] = ["That track isn't on that edition."] });

                Match(file, track, editionId);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Review).WithName("MatchFile").WithSummary("Say which song a file is: accepting a review suggestion, or one picked by searching. It counts as owned at once.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        library.MapPost("/review/approve", async (ReviewFiles body, MelodexDbContext db, RequestPlanner planner, CancellationToken ct) =>
            {
                var ids = body.FileIds.Distinct().ToList();
                var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id) && f.SuggestedTrackId != null && f.ExcludedAt == null && f.HiddenAt == null).ToListAsync(ct);
                var trackIds = files.Select(f => f.SuggestedTrackId!.Value).Distinct().ToList();
                var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
                var count = 0;
                foreach (var file in files)
                {
                    if (!tracks.TryGetValue(file.SuggestedTrackId!.Value, out var track))
                        continue;
                    Match(file, track, file.SuggestedEditionId ?? await planner.ChooseEditionForTrackAsync(track.Id, ct));
                    count++;
                }
                await db.SaveChangesAsync(ct);
                return new ReviewDone(count);
            })
            .RequireScope(Scopes.Review).WithName("ApproveFiles").WithSummary("Accept the suggestion for each of these files that has one, as approving each would.");

        library.MapPost("/review/exclude", async (ReviewFiles body, MelodexDbContext db, CancellationToken ct) =>
            {
                var ids = body.FileIds.Distinct().ToList();
                var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
                foreach (var file in files)
                    SetAsides.Apply(file, SetAside.Excluded);
                await db.SaveChangesAsync(ct);
                return new ReviewDone(files.Count);
            })
            .RequireScope(Scopes.Review).WithName("ExcludeFiles").WithSummary("Exclude each of these files from matching, as excluding each would.");

        library.MapGet("/local/artists/{id:guid}", async (Guid id, LocalMusic local, CancellationToken ct) =>
                await local.GetArtistAsync(id, ct) is { } artist ? Results.Ok(artist) : Results.NotFound())
            .WithName("GetLocalArtist")
            .WithSummary("An artist only your own files name: excluded files with a title and artist, grouped into albums by their tags. "
                         + "Their IDs come from their names. An artist the library has from the catalogue lists these albums on its own page instead.")
            .Produces<LocalArtistDetail>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/local/albums/{id:guid}", async (Guid id, LocalMusic local, CancellationToken ct) =>
                await local.GetAlbumAsync(id, ct) is { } album ? Results.Ok(album) : Results.NotFound())
            .WithName("GetLocalAlbum").WithSummary("An album of your own files, as their tags say, with each song in disc and track order.")
            .Produces<LocalAlbumDetail>().Produces(StatusCodes.Status404NotFound);

        library.MapGet("/files/{id:guid}/cover", async (Guid id, MelodexDbContext db, HttpContext http,
                Microsoft.Extensions.Options.IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                if (await db.LibraryFiles.FindAsync([id], ct) is not { } row)
                    return Results.NotFound();
                var root = Path.GetFullPath(options.Value.LibraryPath);
                var path = Path.GetFullPath(Path.Combine(root, row.Path));
                if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path)
                    && FileArt.Find(path) is { } picture)
                {
                    http.Response.Headers.CacheControl = CoverCaching;
                    return Results.File(picture.Data, picture.ContentType, lastModified: row.ModifiedAt,
                        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{row.ModifiedAt.Ticks:x}-{picture.Data.Length:x}\""));
                }
                // Nothing of its own: the catalogue's cover for what it's matched to, if anything.
                return row.EditionId is { } edition
                    ? Results.Redirect($"{http.Request.PathBase}/api/v1/artwork/edition/{edition}")
                    : Results.NotFound();
            })
            .AllowAnyone().WithName("GetFileCover")
            .WithSummary("A file's own picture: the cover embedded in it, or an image beside it such as cover.jpg or folder.jpg. Without one, a "
                         + "redirect to the catalogue's cover for the edition it's matched to, or 404. Open to anyone, so image elements can show it.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status302Found).Produces(StatusCodes.Status404NotFound);

        api.MapGet("/artwork/{kind}/{id:guid}", async (string kind, Guid id, ArtworkCache artwork, HttpContext http, CancellationToken ct) =>
            {
                var of = ArtworkKind(kind);
                if (of is null)
                    return Results.NotFound();
                // A picture not fetched yet can take a while, as artwork sources are rate-limited. Browsers make only a
                // few requests at once to a server, so a page of pictures still on their way would hold up everything
                // else: answer 503 at once instead, and the search goes on. The page asks after them all together, below.
                var finding = artwork.GetAsync(of.Value, id, ct);
                if (await Task.WhenAny(finding, Task.Delay(ArtworkPatience, ct)) != finding)
                {
                    http.Response.Headers.RetryAfter = "2";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                if (await finding is not { } found)
                    return Results.NotFound();
                // Shown from the browser's cache at once, and checked behind, so a change of artwork plugins shows the
                // next time; unchanged pictures are a 304.
                http.Response.Headers.CacheControl = CoverCaching;
                return Results.File(found.Path, found.ContentType, lastModified: found.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{found.UpdatedAt.Ticks:x}\""));
            })
            .WithTags("Library").AllowAnyone().WithName("GetArtwork")
            .WithSummary("The picture of an artist, album or edition (kind is artist, album or edition), from the artwork plugins, or of a song (track) "
                         + "that has had one chosen. 404 when there's none; "
                         + "503 with Retry-After while it's still being fetched. Open to anyone, so image elements can show it.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg").Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/artwork/status", async (string[] urls, ArtworkCache artwork) =>
            {
                // Each is looked for at once, and those still on their way after a moment are pending.
                var asked = urls.Distinct().Take(500).Select(url => (Url: url, Match: ArtworkPath.Match(url))).ToList();
                var finding = asked.Where(a => a.Match.Success).ToDictionary(a => a.Url,
                    a => artwork.GetAsync(ArtworkKind(a.Match.Groups[1].Value)!.Value, Guid.Parse(a.Match.Groups[2].Value), CancellationToken.None));
                await Task.WhenAny(Task.WhenAll(finding.Values), Task.Delay(ArtworkStatusPatience));
                return asked.ToDictionary(a => a.Url, a => !finding.TryGetValue(a.Url, out var found) ? "none"
                    : !found.IsCompleted ? "pending"
                    : found.IsCompletedSuccessfully && found.Result is not null ? "ready" : "none");
            })
            .WithTags("Library").RequireScope(Scopes.Read).WithName("GetArtworkStatus")
            .WithSummary("For pictures that answered 503, which are ready now, which there are none of, and which are still pending, by URL. "
                         + "One request for a page of them, rather than one each.");

        api.MapGet("/tasks", async (MelodexDbContext db, bool? active, int? limit, CancellationToken ct) =>
            {
                var query = db.BackgroundTasks.AsQueryable();
                if (active is { } a)
                    query = a
                        ? query.Where(t => t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running)
                        : query.Where(t => t.Status != BackgroundTaskStatus.Queued && t.Status != BackgroundTaskStatus.Running);
                var tasks = await query.OrderByDescending(t => t.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
                return await TitledAsync(db, tasks, ct);
            })
            .WithTags("Library").WithName("ListTasks").WithSummary("Background work such as library scans, newest first.");

        api.MapGet("/tasks/{id:guid}", async (Guid id, MelodexDbContext db, CancellationToken ct) =>
                await db.BackgroundTasks.FindAsync([id], ct) is { } task ? Results.Ok((await TitledAsync(db, [task], ct))[0]) : Results.NotFound())
            .WithTags("Library").WithName("GetTask").WithSummary("Status of a background task.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound);

        // Stopping and ordering background work. Each answers 409 when the task is past it, such as cancelling a finished one.
        api.MapPost("/tasks/{id:guid}/cancel", (Guid id, MelodexDbContext db, TaskQueue queue, CancellationToken ct) =>
                TaskActionAsync(id, db, ct, task => queue.CancelAsync(task, ct)))
            .WithTags("Library").RequireScope(Scopes.Scan).WithName("CancelTask").WithSummary("Stop a queued or running task for good.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        api.MapPost("/tasks/{id:guid}/pause", (Guid id, MelodexDbContext db, TaskQueue queue, CancellationToken ct) =>
                TaskActionAsync(id, db, ct, task => queue.PauseAsync(task, ct)))
            .WithTags("Library").RequireScope(Scopes.Scan).WithName("PauseTask")
            .WithSummary("Hold a task back until resumed. A running scan stops, and carries on from where it was when resumed; shorter tasks start again.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        api.MapPost("/tasks/{id:guid}/resume", (Guid id, MelodexDbContext db, TaskQueue queue, CancellationToken ct) =>
                TaskActionAsync(id, db, ct, task => queue.ResumeAsync(task, ct)))
            .WithTags("Library").RequireScope(Scopes.Scan).WithName("ResumeTask").WithSummary("Let a paused task go again, in its place in the queue.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        api.MapPost("/tasks/{id:guid}/move", (Guid id, MoveInQueue body, MelodexDbContext db, TaskQueue queue, CancellationToken ct) =>
                TaskActionAsync(id, db, ct, task => queue.MoveAsync(task, body.Before, ct)))
            .WithTags("Library").RequireScope(Scopes.Scan).WithName("MoveTask").WithSummary("Move a queued task to just before another, or to the end. Tasks run one at a time.")
            .Produces<TaskInfo>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        var settings = api.MapGroup("/settings").WithTags("Settings").RequireScope(Scopes.Settings);
        settings.MapGet("/library", (SettingsService s, CancellationToken ct) => s.GetLibraryAsync(ct))
            .WithName("GetLibrarySettings");
        settings.MapPut("/library", async (LibrarySettings value, SettingsService s, CancellationToken ct) =>
            {
                if (value.MatchThreshold is <= 0 or > 1)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["matchThreshold"] = ["Must be between 0 and 1."] });
                if (value.AttemptsWhenBusy is < 1 or > 10)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["attemptsWhenBusy"] = ["Must be between 1 and 10."] });
                await s.SaveLibraryAsync(value, ct);
                return Results.NoContent();
            })
            .WithName("SaveLibrarySettings").WithSummary("Takes effect at once for the missing view; a new threshold applies from the next scan.");

        settings.MapGet("/artists/{id:guid}/showing", async (Guid id, SettingsService s, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Artists.FindAsync([id], ct) is null)
                    return Results.NotFound();
                var library = await s.GetLibraryAsync(ct);
                return Results.Ok(new ArtistShowing(library.ArtistShowing.GetValueOrDefault(id),
                    Enum.GetValues<ReleaseKind>().ToDictionary(k => k, library.ShowingOf)));
            })
            .WithName("GetArtistShowing").WithSummary("Which releases the missing view shows for an artist: their own choices, if they have them, and the library's.")
            .Produces<ArtistShowing>().Produces(StatusCodes.Status404NotFound);
        settings.MapPut("/artists/{id:guid}/showing", async (Guid id, Dictionary<ReleaseKind, ReleaseShowing> value, SettingsService s, MelodexDbContext db, CancellationToken ct) =>
            {
                if (await db.Artists.FindAsync([id], ct) is null)
                    return Results.NotFound();
                await s.SaveArtistShowingAsync(id, value, ct);
                return Results.NoContent();
            })
            .WithName("SaveArtistShowing").WithSummary("Give an artist their own choices for the missing view, in place of the library's. A kind left out is as the defaults say.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
        settings.MapDelete("/artists/{id:guid}/showing", async (Guid id, SettingsService s, CancellationToken ct) =>
            {
                await s.SaveArtistShowingAsync(id, null, ct);
                return Results.NoContent();
            })
            .WithName("ResetArtistShowing").WithSummary("Have an artist follow the library's choices for the missing view again.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }

    public static RouteGroupBuilder MapCatalogueEndpoints(this RouteGroupBuilder api)
    {
        var catalogue = api.MapGroup("").WithTags("Catalogue");

        catalogue.MapGet("/catalogue/search", async (string q, string? kinds, bool? local, CatalogueSearch search, CancellationToken ct) =>
            {
                var wanted = kinds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(k => Enum.TryParse<SearchKind>(k.TrimEnd('s'), ignoreCase: true, out var kind) ? kind : (SearchKind?)null)
                    .OfType<SearchKind>().ToHashSet();
                return Results.Ok(await search.SearchAsync(q, wanted, local == true, ct));
            })
            .WithName("SearchCatalogue")
            .WithSummary("Search artists, albums and tracks through the index plugin, with owned status. kinds limits it to some of artists, albums "
                         + "and tracks, which is quicker; local=true searches only what Melodex already holds, which answers at once.")
            .Produces<SearchResults>();

        catalogue.MapGet("/artists/{id:guid}", async (Guid id, bool? quick, LibraryView view, MelodexDbContext db, CatalogueService service, TaskQueue queue,
                OnDemandCatalogue fetcher, CancellationToken ct) =>
            {
                var artist = await db.Artists.FindAsync([id], ct);
                if (artist is null)
                    return Results.NotFound();
                // First visit: the discography is fetched now, or meanwhile when asked to be quick; the editions in a task.
                if (artist.AlbumsRefreshedAt is null && quick == true)
                    fetcher.FetchDiscography(id);
                else if (artist.AlbumsRefreshedAt is null)
                {
                    PluginHttp.Interactive.Value = true;
                    await service.RefreshDiscographyAsync(artist, false, ct);
                    await queue.EnqueueAsync(ArtistRefreshTask.TaskKind, ArtistRefreshTask.Payload(id), ct);
                }
                return Results.Ok(await view.GetArtistAsync(id, ct) is { } detail ? detail with { Fetching = fetcher.IsFetching(id) } : null);
            })
            .WithName("GetArtist").WithSummary("An artist's full discography, each album with its owned or missing status. A first visit fetches it "
                         + "first; with quick=true it answers at once with what's known, and fetching is true while the rest is fetched: ask again for it.")
            .Produces<ArtistDetail>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapGet("/artists/{id:guid}/albums", async (Guid id, LibraryView view, CancellationToken ct) =>
                await view.GetArtistAlbumsAsync(id, ct) is { } albums ? Results.Ok(albums) : Results.NotFound())
            .WithName("GetArtistAlbums").Produces<IReadOnlyList<AlbumSummary>>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapGet("/albums/{id:guid}", async (Guid id, Guid? edition, bool? quick, LibraryView view, MelodexDbContext db, CatalogueService service,
                OnDemandCatalogue fetcher, CancellationToken ct) =>
            {
                var album = await db.Albums.FindAsync([id], ct);
                if (album is null)
                    return Results.NotFound();
                // Editions unknown or old are fetched now, or meanwhile when asked to be quick.
                if (quick == true && !(album.EditionsRefreshedAt > DateTime.UtcNow - CatalogueService.EditionsTtl))
                    fetcher.FetchEditions(id);
                else if (quick != true)
                {
                    PluginHttp.Interactive.Value = true;
                    await service.RefreshEditionsAsync(album, false, ct);
                }
                return Results.Ok(await view.GetAlbumAsync(id, edition, ct) is { } detail ? detail with { Fetching = fetcher.IsFetching(id) } : null);
            })
            .WithName("GetAlbum").WithSummary("An album's editions, and the tracks of the owned (or requested) edition with their status. With quick=true it "
                         + "answers at once with what's known, and fetching is true while its editions are fetched: ask again for them.")
            .Produces<AlbumDetail>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapGet("/tracks/{id:guid}", async (Guid id, LibraryView view, CancellationToken ct) =>
                await view.GetTrackAsync(id, ct) is { } track ? Results.Ok(track) : Results.NotFound())
            .WithName("GetTrack").WithSummary("A song: the albums it's on, and its files in the library with the tags they have.")
            .Produces<TrackDetail>().Produces(StatusCodes.Status404NotFound);

        catalogue.MapPut("/albums/{id:guid}/ignore", (Guid id, MelodexDbContext db, CancellationToken ct) => SetIgnored(db, id, true, ct))
            .RequireScope(Scopes.Request).WithName("IgnoreAlbum").WithSummary("Never list this album as missing.");
        catalogue.MapDelete("/albums/{id:guid}/ignore", (Guid id, MelodexDbContext db, CancellationToken ct) => SetIgnored(db, id, false, ct))
            .RequireScope(Scopes.Request).WithName("UnignoreAlbum");
        catalogue.MapPut("/tracks/{id:guid}/ignore", (Guid id, MelodexDbContext db, CancellationToken ct) => SetTrackIgnored(db, id, true, ct))
            .RequireScope(Scopes.Request).WithName("IgnoreTrack").WithSummary("Stop counting this song, on every album it's on, as missing.");
        catalogue.MapDelete("/tracks/{id:guid}/ignore", (Guid id, MelodexDbContext db, CancellationToken ct) => SetTrackIgnored(db, id, false, ct))
            .RequireScope(Scopes.Request).WithName("UnignoreTrack");

        return api;
    }

    /// <summary>Tasks as the API shows them, each with a title saying what it's working on.</summary>
    private static async Task<List<TaskInfo>> TitledAsync(MelodexDbContext db, IReadOnlyList<BackgroundTask> tasks, CancellationToken ct)
    {
        // Artist refreshes say whose catalogue they fill in.
        var artistIds = tasks.Where(t => t.Kind == ArtistRefreshTask.TaskKind && t.PayloadJson is not null)
            .Select(t => JsonSerializer.Deserialize<Guid>(t.PayloadJson!)).Distinct().ToList();
        var names = await db.Artists.Where(x => artistIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return tasks.Select(t => TaskQueue.ToInfo(t) with
        {
            Title = t.Kind switch
            {
                LibraryScanTask.TaskKind => LibraryScanTask.Title(t.PayloadJson),
                ArtistRefreshTask.TaskKind when t.PayloadJson is not null && names.TryGetValue(JsonSerializer.Deserialize<Guid>(t.PayloadJson), out var name)
                    => $"Catalogue update · {name}",
                ArtistRefreshTask.TaskKind => "Catalogue update",
                FormatTask.TaskKind => FormatTask.Title(t.PayloadJson),
                LyricsTask.TaskKind => LyricsTask.Title,
                _ => t.Kind,
            },
        }).ToList();
    }

    private static async Task<IResult> SetIgnored(MelodexDbContext db, Guid id, bool ignored, CancellationToken ct) =>
        await db.Albums.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Ignored, ignored), ct) == 0
            ? Results.NotFound()
            : Results.NoContent();

    private static async Task<IResult> SetTrackIgnored(MelodexDbContext db, Guid id, bool ignored, CancellationToken ct) =>
        await db.Tracks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Ignored, ignored), ct) == 0
            ? Results.NotFound()
            : Results.NoContent();

    /// <summary>Settled by hand: this file is this track, so it counts as owned.</summary>
    private static void Match(LibraryFile file, Track track, Guid? editionId)
    {
        file.TrackId = track.Id;
        file.EditionId = editionId;
        file.ArtistId = track.ArtistId;
        file.MatchMethod = MatchMethod.Manual;
        file.MatchConfidence = 1;
        file.ReviewReason = null;
        file.SuggestedTrackId = null;
        file.SuggestedEditionId = null;
        file.MatchedAt = DateTime.UtcNow;
        file.ExcludedAt = file.DeferredAt = file.HiddenAt = null;
    }
}
