using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// The library-wide views (every artist, album or track you have) compare the whole catalogue with every file, which
/// takes a while on a big library. They're kept in memory, ready to send, and marked stale by any write to library
/// data, which this watches for as a database interceptor so no write path can forget to say so.
/// </summary>
/// <remarks>
/// A stale view is recomputed when next asked for, one computation at a time, and in the background once writes pause
/// for a moment, so it's usually fresh by the time a page asks. The caller waits a moment for a computation and
/// otherwise gets the last answer, so pages load at once even during a scan and catch up on their next refresh.
/// Each answer is saved under <c>data/cache</c> too, so after a restart the last one is served at once while it's
/// worked out again, and carries an ETag, so a client that has it already isn't sent it again.
/// </remarks>
public sealed class LibraryCache(IServiceScopeFactory scopes, IOptions<MelodexOptions> options, ILogger<LibraryCache> logger) : DbCommandInterceptor
{
    private static readonly string[] LibraryTables =
        ["\"Artists\"", "\"Albums\"", "\"Editions\"", "\"Tracks\"", "\"EditionTracks\"", "\"Crosswalk\"", "\"LibraryFiles\"", "\"Settings\"",
         // Turning the last catalogue off or on changes whether unmatched files are listed as their tags say.
         "\"PluginInstances\""];

    /// <summary>How long a caller waits for a fresher answer before taking the last one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(500);

    /// <summary>How long writes must pause before stale views are recomputed without being asked for.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SlowView = TimeSpan.FromSeconds(1);

    public const string Artists = "artists";
    public const string Albums = "albums";
    public const string Tracks = "tracks";

    // Each view: what it holds, as saved, and how it's worked out.
    private static readonly Dictionary<string, (Type Type, Func<LibraryView, CancellationToken, Task<object>> Compute)> Views = new()
    {
        [Artists] = (typeof(List<LibraryArtist>), async (view, ct) => await view.GetLibraryArtistsAsync(ct)),
        [Albums] = (typeof(List<LibraryAlbum>), async (view, ct) => await view.GetLibraryAlbumsAsync(ct)),
        [Tracks] = (typeof(List<LibraryTrack>), async (view, ct) => await view.GetLibraryTracksAsync(ct)),
    };

    private long _generation;
    private long _lastWrite;
    private int _warming;
    private int _refreshing;

    // Working out a view reads much of the catalogue into memory for a moment: one at a time, so they don't all hold
    // theirs at once.
    private readonly SemaphoreSlim _computing = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = Views.Keys.ToDictionary(k => k, k => new Entry(k));

    private sealed class Entry(string name)
    {
        public readonly string Name = name;
        public readonly Lock Gate = new();
        public object? Value;
        public byte[]? Json;
        public string? ETag;
        public long Generation = -1;
        public Task? Refresh;
    }

    /// <summary>A view as the API sends it.</summary>
    /// <param name="ETag">Quoted, from the content, so it's the same for the same answer even after a restart.</param>
    public sealed record Answer(object Value, byte[] Json, string ETag);

    private string Folder => Path.Combine(options.Value.DataPath, "cache");

    /// <summary>Whether a view has yet to be worked out since starting, with none saved from before to show meanwhile.</summary>
    public bool Preparing => _entries.Values.Any(e => Volatile.Read(ref e.Value) is null);

    public async Task<IReadOnlyList<LibraryArtist>> GetArtistsAsync(CancellationToken ct) => (List<LibraryArtist>)(await GetAsync(Artists, ct)).Value;

    public async Task<IReadOnlyList<LibraryAlbum>> GetAlbumsAsync(CancellationToken ct) => (List<LibraryAlbum>)(await GetAsync(Albums, ct)).Value;

    public async Task<IReadOnlyList<LibraryTrack>> GetTracksAsync(CancellationToken ct) => (List<LibraryTrack>)(await GetAsync(Tracks, ct)).Value;

    /// <summary>Sends a view, or 304 Not Modified when the client's copy (by If-None-Match) is current.</summary>
    public async Task<IResult> SendAsync(string view, HttpContext http, CancellationToken ct)
    {
        var answer = await GetAsync(view, ct);
        http.Response.Headers.ETag = answer.ETag;
        http.Response.Headers.CacheControl = "no-cache";
        return http.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Tag == answer.ETag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(answer.Json, "application/json; charset=utf-8");
    }

    /// <summary>
    /// Takes up the views saved before the last stop, then works out every view in the background, all at once, so the
    /// Library page has them from the start. The saved ones are read before this returns.
    /// </summary>
    public Task WarmAsync(CancellationToken ct)
    {
        foreach (var entry in _entries.Values)
            LoadSaved(entry);
        return Task.WhenAll(_entries.Keys.Select(async view =>
        {
            try
            {
                await GetAsync(view, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Logged by the refresh; the first page to ask tries again.
            }
        }));
    }

    private void LoadSaved(Entry entry)
    {
        var path = Path.Combine(Folder, entry.Name + ".json");
        try
        {
            if (!File.Exists(path))
                return;
            var json = File.ReadAllBytes(path);
            var value = JsonSerializer.Deserialize(json, Views[entry.Name].Type, MelodexJson.Options);
            if (value is null)
                return;
            lock (entry.Gate)
            {
                // Stale, so it's worked out again, but there to show meanwhile.
                entry.Value ??= value;
                entry.Json ??= json;
                entry.ETag ??= TagOf(json);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogWarning("Couldn't read the library's saved {View}, so it's worked out afresh: {Message}", entry.Name, e.Message);
        }
    }

    private async Task SaveAsync(Entry entry, byte[] json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, entry.Name + ".json");
            var temporary = path + ".new";
            await File.WriteAllBytesAsync(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't save the library's {View}, so it's worked out afresh after a restart: {Message}", entry.Name, e.Message);
        }
    }

    private static string TagOf(byte[] json) => $"\"{Convert.ToHexStringLower(SHA256.HashData(json).AsSpan(0, 12))}\"";

    /// <summary>Raised after library data is written, as views are marked stale. Handlers must return quickly.</summary>
    public event Action? Changed;

    /// <summary>Marks every view stale.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _lastWrite, Environment.TickCount64);
        Changed?.Invoke();
        if (Interlocked.Exchange(ref _warming, 1) == 0)
            _ = WarmStaleAsync();
    }

    // Once writes pause, recomputes the stale views.
    private async Task WarmStaleAsync()
    {
        try
        {
            long wait;
            while ((wait = (long)Quiet.TotalMilliseconds - (Environment.TickCount64 - Interlocked.Read(ref _lastWrite))) > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait));
        }
        finally
        {
            Interlocked.Exchange(ref _warming, 0);
        }
        foreach (var entry in _entries.Values)
        {
            Task? refresh;
            lock (entry.Gate)
            {
                if (entry.Generation == Interlocked.Read(ref _generation))
                    continue;
                refresh = entry.Refresh ??= RefreshAsync(entry);
            }
            try
            {
                await refresh;
            }
            catch (Exception)
            {
                // Logged by the refresh; the next caller tries again.
            }
        }
    }

    private async Task<Answer> GetAsync(string view, CancellationToken ct)
    {
        var entry = _entries[view];
        Task refresh;
        lock (entry.Gate)
        {
            if (entry.Value is not null && entry.Generation == Interlocked.Read(ref _generation))
                return new Answer(entry.Value, entry.Json!, entry.ETag!);
            refresh = entry.Refresh ??= RefreshAsync(entry);
        }

        if (Volatile.Read(ref entry.Value) is null)
            await refresh.WaitAsync(ct);
        else
            await Task.WhenAny(refresh, Task.Delay(Patience, ct));
        ct.ThrowIfCancellationRequested();
        lock (entry.Gate)
            return new Answer(entry.Value!, entry.Json!, entry.ETag!);
    }

    private async Task RefreshAsync(Entry entry)
    {
        await Task.Yield();
        Interlocked.Increment(ref _refreshing);
        try
        {
            await _computing.WaitAsync();
            long generation;
            object value;
            byte[] json;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Read before computing, so a write while it runs leaves the answer stale.
                generation = Interlocked.Read(ref _generation);
                using var scope = scopes.CreateScope();
                value = await Views[entry.Name].Compute(scope.ServiceProvider.GetRequiredService<LibraryView>(), CancellationToken.None);
                json = JsonSerializer.SerializeToUtf8Bytes(value, Views[entry.Name].Type, MelodexJson.Options);
            }
            finally
            {
                _computing.Release();
            }
            var tag = TagOf(json);
            if (watch.Elapsed > SlowView)
                logger.LogInformation("Working out the library's {View} took {Seconds:0.0} s", entry.Name, watch.Elapsed.TotalSeconds);
            bool changed;
            lock (entry.Gate)
            {
                changed = entry.ETag != tag;
                entry.Value = value;
                entry.Json = json;
                entry.ETag = tag;
                entry.Generation = generation;
            }
            if (changed)
                await SaveAsync(entry, json);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't work out the library view: {Message}", e.Message);
            throw;
        }
        finally
        {
            lock (entry.Gate)
                entry.Refresh = null;
            // What was read to work the views out is let go and its memory handed back, rather than kept for the next time.
            if (Interlocked.Decrement(ref _refreshing) == 0)
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    // ---- Watching for writes ----

    private void Saw(DbCommand command)
    {
        // A command can hold several statements, so look for a write anywhere in it.
        var sql = command.CommandText;
        if (!(sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal)
              || sql.Contains("DELETE FROM", StringComparison.Ordinal)))
            return;
        foreach (var table in LibraryTables)
        {
            if (sql.Contains(table, StringComparison.Ordinal))
            {
                Invalidate();
                return;
            }
        }
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }

    // SaveChanges sends inserts and updates as readers, to read generated values back.
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Saw(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken ct = default)
    {
        Saw(command);
        return ValueTask.FromResult(result);
    }
}
