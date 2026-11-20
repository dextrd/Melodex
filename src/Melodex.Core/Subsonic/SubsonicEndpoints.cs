using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Artwork;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Playlists;
using Melodex.Core.Tasks;

namespace Melodex.Core.Subsonic;

/// <summary>What a Subsonic call has to work with.</summary>
/// <param name="Username">Who it's for: the caller, or the owner of the key it came with.</param>
public sealed record SubsonicCall(HttpContext Http, SubsonicParams P, Caller Caller, string Username, CancellationToken Ct)
{
    public IServiceProvider Services => Http.RequestServices;
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public Guid UserId => Caller.UserId!.Value;
}

/// <summary>
/// The Subsonic API, with OpenSubsonic's extensions (#7), under <c>/rest</c>: so any Subsonic player can browse your
/// library, play it and keep its playlists, signing in with an API key, alone or as your password. Every call needs the
/// listen permission, and answers in XML, or JSON with <c>f=json</c>; errors are a 200 with a code, as players expect.
/// </summary>
public static partial class SubsonicEndpoints
{
    private delegate Task<object> Handler(SubsonicCall call);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = _ => Answer(SubsonicResponse.Ok()),
        ["getLicense"] = _ => Answer(SubsonicResponse.Ok().With("license", new JsonObject { ["valid"] = true })),
        ["getOpenSubsonicExtensions"] = _ => Answer(SubsonicResponse.Ok().With("openSubsonicExtensions", new JsonArray(
            Extension("apiKeyAuthentication"), Extension("formPost"), Extension("indexBasedQueue"), Extension("melodex"), Extension("songLyrics"), Extension("transcodeOffset")))),
        ["getMusicFolders"] = _ => Answer(SubsonicResponse.Ok().With("musicFolders", new JsonObject
        {
            ["musicFolder"] = new JsonArray(new JsonObject { ["id"] = MusicFolderId, ["name"] = "Music" }),
        })),
        ["getUser"] = GetUserAsync,
        ["getScanStatus"] = GetScanStatusAsync,
        ["getIndexes"] = GetIndexesAsync,
        ["getArtists"] = GetArtistsAsync,
        ["getArtist"] = GetArtistAsync,
        ["getAlbum"] = GetAlbumAsync,
        ["getSong"] = GetSongAsync,
        ["getMusicDirectory"] = GetMusicDirectoryAsync,
        ["getAlbumList"] = c => GetAlbumListAsync(c, "albumList", asDirectories: true),
        ["getAlbumList2"] = c => GetAlbumListAsync(c, "albumList2", asDirectories: false),
        ["getRandomSongs"] = GetRandomSongsAsync,
        ["getGenres"] = GetGenresAsync,
        ["getSongsByGenre"] = GetSongsByGenreAsync,
        ["search2"] = c => SearchAsync(c, "searchResult2"),
        ["search3"] = c => SearchAsync(c, "searchResult3"),
        // What Melodex doesn't know yet answers empty rather than failing, so players carry on.
        ["getArtistInfo"] = _ => Empty("artistInfo"),
        ["getArtistInfo2"] = _ => Empty("artistInfo2"),
        ["getAlbumInfo"] = _ => Empty("albumInfo"),
        ["getAlbumInfo2"] = _ => Empty("albumInfo"),
        ["getTopSongs"] = _ => Empty("topSongs"),
        ["getSimilarSongs"] = c => GetSimilarSongsAsync(c, "similarSongs"),
        ["getSimilarSongs2"] = c => GetSimilarSongsAsync(c, "similarSongs2"),
        ["getNowPlaying"] = GetNowPlayingAsync,
        ["scrobble"] = ScrobbleAsync,
        ["stream"] = StreamAsync,
        ["download"] = DownloadAsync,
        ["getCoverArt"] = GetCoverArtAsync,
        ["getLyrics"] = GetLyricsAsync,
        ["getLyricsBySongId"] = GetLyricsBySongIdAsync,
        ["getPlaylists"] = GetPlaylistsAsync,
        ["getPlaylist"] = GetPlaylistAsync,
        ["createPlaylist"] = CreatePlaylistAsync,
        ["updatePlaylist"] = UpdatePlaylistAsync,
        ["deletePlaylist"] = DeletePlaylistAsync,
        ["star"] = c => StarAsync(c, starred: true),
        ["unstar"] = c => StarAsync(c, starred: false),
        ["setRating"] = SetRatingAsync,
        ["getStarred"] = c => GetStarredAsync(c, "starred"),
        ["getStarred2"] = c => GetStarredAsync(c, "starred2"),
        ["getPlayQueue"] = c => GetPlayQueueAsync(c, byIndex: false),
        ["savePlayQueue"] = c => SavePlayQueueAsync(c, byIndex: false),
        ["getPlayQueueByIndex"] = c => GetPlayQueueAsync(c, byIndex: true),
        ["savePlayQueueByIndex"] = c => SavePlayQueueAsync(c, byIndex: true),
    };

    /// <summary>The one music folder Melodex has, for players that browse by folders.</summary>
    public const int MusicFolderId = 1;

    /// <summary>The calls Melodex answers, for the guide and its tests.</summary>
    public static IReadOnlyCollection<string> Methods => Handlers.Keys;

    public static IEndpointRouteBuilder MapSubsonicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/rest/{method}", ["GET", "POST"], HandleAsync).ExcludeFromDescription().DisableAntiforgery();
        // Players in a browser ask first whether they may.
        app.MapMethods("/rest/{method}", ["OPTIONS"], (HttpContext http) =>
        {
            AllowAnyOrigin(http);
            return Results.NoContent();
        }).ExcludeFromDescription();
        return app;
    }

    private static void AllowAnyOrigin(HttpContext http)
    {
        http.Response.Headers.AccessControlAllowOrigin = "*";
        http.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        http.Response.Headers.AccessControlAllowHeaders = "*";
    }

    private static async Task HandleAsync(string method, HttpContext http, SubsonicAuth auth, MelodexDbContext db, ILoggerFactory logs)
    {
        var ct = http.RequestAborted;
        AllowAnyOrigin(http);
        var p = await SubsonicParams.ReadAsync(http.Request, ct);
        var name = method.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? method[..^5] : method;
        object result;
        var (caller, refused) = await auth.IdentifyAsync(p, http.Connection.RemoteIpAddress?.ToString() ?? "", ct);
        if (refused is not null)
            result = refused;
        else if (!Handlers.TryGetValue(name, out var handler))
            result = SubsonicResponse.Error(SubsonicErrors.NotFound, $"Melodex doesn't answer {name}.");
        else
        {
            try
            {
                var username = caller!.Kind == CallerKind.Key && await db.Users.FindAsync([caller.UserId], ct) is { } owner ? owner.Username : caller.Name;
                var call = new SubsonicCall(http, p, caller, username, ct);
                result = await handler(call);
                if (result is SubsonicResponse answer)
                    await MarkAsync(call, answer);
            }
            catch (SubsonicException e)
            {
                result = SubsonicResponse.Error(e.Code, e.Message);
            }
            catch (PlaylistRefusedException e)
            {
                result = SubsonicResponse.Error(SubsonicErrors.Generic, e.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logs.CreateLogger("Melodex.Core.Subsonic").LogError(e, "A Subsonic player's {Method} failed", name);
                result = SubsonicResponse.Error(SubsonicErrors.Generic, "Something went wrong in Melodex: its log says what.");
            }
        }
        switch (result)
        {
            case SubsonicResponse response:
                await WriteAsync(http, p, response, ct);
                break;
            case IResult other:
                await other.ExecuteAsync(http);
                break;
            case Sent:
                break;
        }
    }

    private static async Task WriteAsync(HttpContext http, SubsonicParams p, SubsonicResponse response, CancellationToken ct)
    {
        var format = p["f"]?.ToLowerInvariant();
        string body, type;
        if (format is "json" or "jsonp")
        {
            body = response.ToJson();
            type = "application/json";
            if (format == "jsonp" && p["callback"] is { } callback && Regex.IsMatch(callback, "^[A-Za-z_$][A-Za-z0-9_$.]*$"))
            {
                body = $"{callback}({body});";
                type = "application/javascript";
            }
        }
        else
        {
            body = response.ToXml();
            type = "text/xml";
        }
        http.Response.ContentType = type + "; charset=utf-8";
        await http.Response.WriteAsync(body, Encoding.UTF8, ct);
    }

    /// <summary>The answer was sent already, as a song or a picture.</summary>
    private sealed class Sent
    {
        public static readonly Sent Instance = new();
    }

    private sealed class SubsonicException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static SubsonicException NotFound(string what) => new(SubsonicErrors.NotFound, $"There's no {what} with that ID.");

    private static SubsonicException Missing(string parameter) => new(SubsonicErrors.Missing, $"Say which, with {parameter}.");

    private static Task<object> Answer(SubsonicResponse response) => Task.FromResult<object>(response);

    private static Task<object> Empty(string name) => Answer(SubsonicResponse.Ok().With(name, new JsonObject()));

    private static JsonObject Extension(string name) => new() { ["name"] = name, ["versions"] = new JsonArray(1) };

    private static Guid IdOf(SubsonicCall c, string parameter = "id") =>
        c.P[parameter] is { } id ? Guid.TryParse(id, out var guid) ? guid : throw NotFound("such thing") : throw Missing(parameter);

    private static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.ToArray());

    // ---- System ----

    private static async Task<object> GetUserAsync(SubsonicCall c)
    {
        var name = c.P["username"] ?? c.Username;
        if (!string.Equals(name, c.Username, StringComparison.OrdinalIgnoreCase) && !c.Caller.IsAdmin)
            throw new SubsonicException(SubsonicErrors.NotAllowed, "Only the admin can see someone else.");
        var normalized = AccessService.Normalize(name);
        var user = await c.Get<MelodexDbContext>().Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, c.Ct) ?? throw NotFound("user");
        var may = AccessService.PermissionsOf(user);
        return SubsonicResponse.Ok().With("user", new JsonObject
        {
            ["username"] = user.Username,
            ["scrobblingEnabled"] = false,
            ["adminRole"] = user.IsAdmin,
            ["settingsRole"] = user.IsAdmin || may.Contains(Scopes.Settings),
            ["downloadRole"] = true,
            ["uploadRole"] = false,
            ["playlistRole"] = true,
            ["coverArtRole"] = false,
            ["commentRole"] = false,
            ["podcastRole"] = false,
            ["streamRole"] = true,
            ["jukeboxRole"] = false,
            ["shareRole"] = false,
            ["videoConversionRole"] = false,
            ["folder"] = new JsonArray(MusicFolderId),
        });
    }

    private static async Task<object> GetScanStatusAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var scanning = c.Get<MelodexDbContext>().BackgroundTasks
            .Any(t => t.Kind == LibraryScanTask.TaskKind && (t.Status == BackgroundTaskStatus.Running || t.Status == BackgroundTaskStatus.Queued));
        return SubsonicResponse.Ok().With("scanStatus", new JsonObject { ["scanning"] = scanning, ["count"] = library.Songs.Count });
    }

    // ---- Browsing ----

    private static JsonArray Indexed(IEnumerable<SubsonicArtist> artists) => Array(artists
        .GroupBy(SubsonicLibrary.IndexOf)
        .OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal)
        .Select(g => new JsonObject
        {
            ["name"] = g.Key,
            ["artist"] = Array(g.OrderBy(SubsonicLibrary.SortKey, StringComparer.CurrentCultureIgnoreCase).Select(SubsonicLibrary.ArtistNode)),
        }));

    private static async Task<object> GetIndexesAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var changed = library.Songs.Count == 0 ? DateTime.UnixEpoch : library.Songs.Max(s => s.Created);
        return SubsonicResponse.Ok().With("indexes", new JsonObject
        {
            ["lastModified"] = new DateTimeOffset(changed).ToUnixTimeMilliseconds(),
            ["ignoredArticles"] = SubsonicLibrary.IgnoredArticles,
            ["index"] = Indexed(library.Artists),
        });
    }

    private static async Task<object> GetArtistsAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        return SubsonicResponse.Ok().With("artists", new JsonObject
        {
            ["ignoredArticles"] = SubsonicLibrary.IgnoredArticles,
            ["index"] = Indexed(library.Artists),
        });
    }

    private static async Task<object> GetArtistAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var artist = library.ArtistsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("artist");
        var node = SubsonicLibrary.ArtistNode(artist);
        node["album"] = Array(artist.Albums.Select(a => SubsonicLibrary.AlbumNode(a)));
        return SubsonicResponse.Ok().With("artist", node);
    }

    private static async Task<object> GetAlbumAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var album = library.AlbumsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("album");
        var node = SubsonicLibrary.AlbumNode(album);
        node["song"] = Array(album.Songs.Select(s => SubsonicLibrary.SongNode(s, album)));
        return SubsonicResponse.Ok().With("album", node);
    }

    private static async Task<object> GetSongAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var song = library.SongsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("song");
        return SubsonicResponse.Ok().With("song", SubsonicLibrary.SongNode(song, library.AlbumOfSong.GetValueOrDefault(song.Id)));
    }

    private static JsonObject SongOf(SubsonicLibrary.View library, SubsonicSong song) =>
        SubsonicLibrary.SongNode(song, library.AlbumOfSong.GetValueOrDefault(song.Id));

    /// <summary>The library as folders: the music folder holds artists, an artist its albums, and an album its songs.</summary>
    private static async Task<object> GetMusicDirectoryAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var id = c.P["id"] ?? throw Missing("id");
        JsonObject directory;
        if (id == MusicFolderId.ToString())
            directory = new JsonObject
            {
                ["id"] = id,
                ["name"] = "Music",
                ["child"] = Array(library.Artists.OrderBy(SubsonicLibrary.SortKey, StringComparer.CurrentCultureIgnoreCase).Select(a => new JsonObject
                {
                    ["id"] = a.Id.ToString(),
                    ["parent"] = id,
                    ["isDir"] = true,
                    ["title"] = a.Name,
                    ["artist"] = a.Name,
                    ["coverArt"] = SubsonicLibrary.ArtistCover(a.Id),
                })),
            };
        else if (Guid.TryParse(id, out var guid) && library.ArtistsById.TryGetValue(guid, out var artist))
            directory = new JsonObject
            {
                ["id"] = id,
                ["parent"] = MusicFolderId.ToString(),
                ["name"] = artist.Name,
                ["child"] = Array(artist.Albums.Select(a => SubsonicLibrary.AlbumNode(a, asDirectory: true))),
            };
        else if (Guid.TryParse(id, out guid) && library.AlbumsById.TryGetValue(guid, out var album))
            directory = new JsonObject
            {
                ["id"] = id,
                ["parent"] = album.ArtistId.ToString(),
                ["name"] = album.Name,
                ["child"] = Array(album.Songs.Select(s => SubsonicLibrary.SongNode(s, album))),
            };
        else
            throw NotFound("folder");
        return SubsonicResponse.Ok().With("directory", directory);
    }

    private static async Task<object> GetAlbumListAsync(SubsonicCall c, string name, bool asDirectories)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var type = c.P["type"] ?? throw Missing("type");
        var size = c.P.Count("size", 10);
        var offset = Math.Max(0, c.P.Int("offset") ?? 0);
        IEnumerable<SubsonicAlbum> albums = library.Albums;
        albums = type switch
        {
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            "newest" => albums.OrderByDescending(a => a.Created),
            "alphabeticalByName" => albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "alphabeticalByArtist" => albums.OrderBy(a => library.ArtistsById.TryGetValue(a.ArtistId, out var artist) ? SubsonicLibrary.SortKey(artist) : a.Artist,
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Year ?? 9999).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "byYear" => ByYear(albums, c.P.Int("fromYear") ?? throw Missing("fromYear"), c.P.Int("toYear") ?? throw Missing("toYear")),
            "byGenre" => albums.Where(a => a.Genres.Contains(c.P["genre"] ?? throw Missing("genre"), StringComparer.OrdinalIgnoreCase))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "starred" => await MarkedAlbumsAsync(c, library, starred: true),
            "highest" => await MarkedAlbumsAsync(c, library, starred: false),
            "frequent" => await PlayedAlbumsAsync(c, library, frequent: true),
            "recent" => await PlayedAlbumsAsync(c, library, frequent: false),
            _ => throw new SubsonicException(SubsonicErrors.Generic, $"There's no list of albums called {type}."),
        };
        return SubsonicResponse.Ok().With(name, new JsonObject
        {
            ["album"] = Array(albums.Skip(offset).Take(size).Select(a => SubsonicLibrary.AlbumNode(a, asDirectories))),
        });
    }

    /// <summary>Albums from one year to another, oldest first, or newest first if the years are the other way round.</summary>
    private static IEnumerable<SubsonicAlbum> ByYear(IEnumerable<SubsonicAlbum> albums, int from, int to) =>
        from <= to
            ? albums.Where(a => a.Year >= from && a.Year <= to).OrderBy(a => a.Year).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            : albums.Where(a => a.Year <= from && a.Year >= to).OrderByDescending(a => a.Year).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);

    private static async Task<object> GetRandomSongsAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        IEnumerable<SubsonicSong> songs = library.Songs;
        if (c.P["genre"] is { } genre)
            songs = songs.Where(s => s.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
        if (c.P.Int("fromYear") is { } from)
            songs = songs.Where(s => s.Year >= from);
        if (c.P.Int("toYear") is { } to)
            songs = songs.Where(s => s.Year <= to);
        var picked = songs.OrderBy(_ => Random.Shared.Next()).Take(c.P.Count("size", 10));
        return SubsonicResponse.Ok().With("randomSongs", new JsonObject { ["song"] = Array(picked.Select(s => SongOf(library, s))) });
    }

    /// <summary>
    /// A players' "instant mix": a radio (#4) from a song, album or artist, of songs you have only, as players can only
    /// play those. Nothing is found for a file of your own no catalogue knows.
    /// </summary>
    private static async Task<object> GetSimilarSongsAsync(SubsonicCall c, string name)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var id = IdOf(c);
        var seed = library.SongsById.TryGetValue(id, out var song) ? song.Track.TrackId : id;
        var batch = await c.Get<RadioService>().NextAsync(c.UserId, new RadioNext(seed, [seed], c.P.Count("count", 50), OwnedOnly: true), c.Ct);
        var byTrack = library.Songs.GroupBy(s => s.Track.TrackId).ToDictionary(g => g.Key, g => g.First());
        var songs = (batch?.Songs ?? []).Select(s => byTrack.GetValueOrDefault(s.TrackId)).OfType<SubsonicSong>();
        return SubsonicResponse.Ok().With(name, new JsonObject { ["song"] = Array(songs.Select(s => SongOf(library, s))) });
    }

    private static async Task<object> GetGenresAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var genres = library.Songs.SelectMany(s => s.Genres.Select(g => (Genre: g, Song: s)))
            .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new JsonObject
            {
                ["songCount"] = g.Count(),
                ["albumCount"] = g.Select(x => library.AlbumOfSong.GetValueOrDefault(x.Song.Id)?.Id).OfType<Guid>().Distinct().Count(),
                ["value"] = g.Key,
            });
        return SubsonicResponse.Ok().With("genres", new JsonObject { ["genre"] = Array(genres) });
    }

    private static async Task<object> GetSongsByGenreAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var genre = c.P["genre"] ?? throw Missing("genre");
        var songs = library.Songs.Where(s => s.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            .Skip(Math.Max(0, c.P.Int("offset") ?? 0)).Take(c.P.Count("count", 10));
        return SubsonicResponse.Ok().With("songsByGenre", new JsonObject { ["song"] = Array(songs.Select(s => SongOf(library, s))) });
    }

    // ---- Search ----

    /// <summary>Text as search compares it: lower case, without accents.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return builder.ToString();
    }

    /// <summary>
    /// Artists, albums and songs with every word of the query in them. An empty query, or <c>""</c>, finds everything,
    /// a page at a time, as players ask when they copy a whole library.
    /// </summary>
    private static async Task<object> SearchAsync(SubsonicCall c, string name)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var query = (c.P["query"] ?? "").Trim().Trim('"', '*').Trim();
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Has(params string?[] fields)
        {
            if (words.Length == 0)
                return true;
            var text = Fold(string.Join(' ', fields.OfType<string>()));
            return words.All(text.Contains);
        }
        var artists = library.Artists.Where(a => Has(a.Name, a.SortName))
            .OrderBy(SubsonicLibrary.SortKey, StringComparer.CurrentCultureIgnoreCase)
            .Skip(Math.Max(0, c.P.Int("artistOffset") ?? 0)).Take(c.P.Count("artistCount", 20));
        var albums = library.Albums.Where(a => Has(a.Name, a.Artist))
            .Skip(Math.Max(0, c.P.Int("albumOffset") ?? 0)).Take(c.P.Count("albumCount", 20));
        var songs = library.Songs.Where(s => Has(s.Track.Title, s.Track.ArtistCredit, s.Track.AlbumTitle))
            .Skip(Math.Max(0, c.P.Int("songOffset") ?? 0)).Take(c.P.Count("songCount", 20, most: 5000));
        return SubsonicResponse.Ok().With(name, new JsonObject
        {
            ["artist"] = Array(artists.Select(SubsonicLibrary.ArtistNode)),
            ["album"] = Array(albums.Select(a => SubsonicLibrary.AlbumNode(a, asDirectory: name == "searchResult2"))),
            ["song"] = Array(songs.Select(s => SongOf(library, s))),
        });
    }

    // ---- Songs, pictures and lyrics ----

    /// <summary>Where a song's file is, if it's still there and inside the music folder.</summary>
    private static string FileOf(SubsonicCall c, SubsonicSong song)
    {
        var root = Path.GetFullPath(c.Get<IOptions<MelodexOptions>>().Value.LibraryPath);
        var path = Path.GetFullPath(Path.Combine(root, song.Track.Path));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path) ? path : throw NotFound("song");
    }

    private static async Task<object> StreamAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var song = library.SongsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("song");
        var path = FileOf(c, song);
        if (Transcoder.Decide(song.Suffix, song.BitrateKbps, c.P["format"], c.P.Int("maxBitRate")) is not { } transcode)
            return Results.File(path, song.ContentType, enableRangeProcessing: true);
        var offset = double.TryParse(c.P["timeOffset"], CultureInfo.InvariantCulture, out var seconds) ? Math.Max(0, seconds) : 0;
        await c.Get<Transcoder>().SendAsync(c.Http, path, transcode.Format, transcode.Kbps, offset, c.Ct);
        return Sent.Instance;
    }

    private static async Task<object> DownloadAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var song = library.SongsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("song");
        return Results.File(FileOf(c, song), song.ContentType, Path.GetFileName(song.Track.Path), enableRangeProcessing: true);
    }

    private static readonly Regex ArtworkUrl = new(@"^api/v1/artwork/(artist|album|edition|track)/([0-9a-fA-F-]{36})$", RegexOptions.Compiled);
    private static readonly Regex FileCoverUrl = new(@"^api/v1/library/files/([0-9a-fA-F-]{36})/cover$", RegexOptions.Compiled);

    /// <summary>
    /// The picture of an album (<c>al-</c>), an artist (<c>ar-</c>), a song (<c>mf-</c>) or a playlist (<c>pl-</c>): the
    /// same the web app shows, whole, whatever <c>size</c> asks.
    /// </summary>
    private static async Task<object> GetCoverArtAsync(SubsonicCall c)
    {
        var id = c.P["id"] ?? throw Missing("id");
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var (kind, rest) = id.Length > 3 && id[2] == '-' ? (id[..2], id[3..]) : ("", id);
        if (!Guid.TryParse(rest, out var guid))
            throw NotFound("picture");
        if (kind == "pl")
            return await c.Get<PlaylistService>().CoverAsync(c.UserId, guid, c.Ct) is { } cover
                ? Results.File(cover.Path, cover.ContentType)
                : await PlaylistMosaicAsync(c, library, guid);
        var url = kind switch
        {
            "al" => library.AlbumsById.GetValueOrDefault(guid)?.CoverUrl,
            "ar" => library.ArtistsById.GetValueOrDefault(guid)?.ImageUrl,
            "mf" => library.SongsById.GetValueOrDefault(guid)?.Track.CoverUrl,
            // An ID without a kind, from a player that made its own.
            _ => library.AlbumsById.GetValueOrDefault(guid)?.CoverUrl ?? library.SongsById.GetValueOrDefault(guid)?.Track.CoverUrl
                 ?? library.ArtistsById.GetValueOrDefault(guid)?.ImageUrl,
        };
        return await PictureAsync(c, url) ?? throw NotFound("picture");
    }

    /// <summary>A playlist without a picture of its own shows its first song's.</summary>
    private static async Task<object> PlaylistMosaicAsync(SubsonicCall c, SubsonicLibrary.View library, Guid id)
    {
        var detail = await c.Get<PlaylistService>().GetAsync(c.UserId, id, c.Ct) ?? throw NotFound("playlist");
        foreach (var song in detail.Songs)
            if (await PictureAsync(c, song.CoverUrl) is { } picture)
                return picture;
        throw NotFound("picture");
    }

    /// <summary>The picture at one of Melodex's own picture URLs, waiting for it if it's still being fetched.</summary>
    private static async Task<IResult?> PictureAsync(SubsonicCall c, string? url)
    {
        if (url is null)
            return null;
        if (FileCoverUrl.Match(url) is { Success: true } file)
        {
            var db = c.Get<MelodexDbContext>();
            if (await db.LibraryFiles.FindAsync([Guid.Parse(file.Groups[1].Value)], c.Ct) is not { } row)
                return null;
            var root = Path.GetFullPath(c.Get<IOptions<MelodexOptions>>().Value.LibraryPath);
            var path = Path.GetFullPath(Path.Combine(root, row.Path));
            if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path) && FileArt.Find(path) is { } picture)
                return Results.File(picture.Data, picture.ContentType);
            return row.EditionId is { } edition ? await PictureAsync(c, $"api/v1/artwork/edition/{edition}") : null;
        }
        if (ArtworkUrl.Match(url) is not { Success: true } artwork)
            return null;
        var kind = artwork.Groups[1].Value switch
        {
            "artist" => EntityKind.Artist,
            "album" => EntityKind.Album,
            "edition" => EntityKind.Edition,
            _ => EntityKind.Track,
        };
        return await c.Get<ArtworkCache>().GetAsync(kind, Guid.Parse(artwork.Groups[2].Value), c.Ct) is { } found
            ? Results.File(found.Path, found.ContentType, lastModified: found.UpdatedAt)
            : null;
    }

    private static async Task<object> GetLyricsAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        string? artist = c.P["artist"], title = c.P["title"];
        var song = title is null ? null : library.Songs.FirstOrDefault(s => Fold(s.Track.Title) == Fold(title)
            && (artist is null || Fold(s.Track.ArtistCredit).Contains(Fold(artist))) && LyricsOf(c, s) is { Lines.Count: > 0 });
        var node = new JsonObject();
        if (song is not null && LyricsOf(c, song) is { } lyrics)
        {
            node["artist"] = song.Track.ArtistCredit;
            node["title"] = song.Track.Title;
            node["value"] = string.Join('\n', lyrics.Lines.Select(l => l.Text));
        }
        return SubsonicResponse.Ok().With("lyrics", node);
    }

    private static TrackLyrics? LyricsOf(SubsonicCall c, SubsonicSong song)
    {
        try
        {
            return LyricsFiles.Lyrics(FileOf(c, song));
        }
        catch (SubsonicException)
        {
            return null;
        }
    }

    /// <summary>OpenSubsonic's lyrics: synced, line by line in milliseconds, when the song's are.</summary>
    private static async Task<object> GetLyricsBySongIdAsync(SubsonicCall c)
    {
        var library = await c.Get<SubsonicLibrary>().GetAsync(c.Ct);
        var song = library.SongsById.GetValueOrDefault(IdOf(c)) ?? throw NotFound("song");
        var list = new JsonArray();
        if (LyricsOf(c, song) is { Lines.Count: > 0 } lyrics)
        {
            var synced = lyrics.Lines.All(l => l.Seconds is not null);
            list.Add(new JsonObject
            {
                ["displayArtist"] = song.Track.ArtistCredit,
                ["displayTitle"] = song.Track.Title,
                ["lang"] = "xxx",
                ["synced"] = synced,
                ["line"] = Array(lyrics.Lines.Select(l =>
                {
                    var line = new JsonObject { ["value"] = l.Text };
                    if (synced)
                        line["start"] = (long)Math.Round(l.Seconds!.Value * 1000);
                    return line;
                })),
            });
        }
        return SubsonicResponse.Ok().With("lyricsList", new JsonObject { ["structuredLyrics"] = list });
    }
}
