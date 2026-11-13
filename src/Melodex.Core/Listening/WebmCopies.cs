using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Listening;

/// <summary>
/// Opus songs in an Ogg file, put in a WebM one for Safari (#93), which gets their time wrong: it stops counting short of
/// the end while the music plays on (WebKit bug 293310). The sound isn't touched, only what it's packed in. Copies are
/// made when first played and kept a while in Melodex's data folder, the least lately played forgotten first.
/// </summary>
public sealed class WebmCopies(IOptions<MelodexOptions> options, ILogger<WebmCopies> logger)
{
    /// <summary>The most room the copies take: two hundred songs or so.</summary>
    public const long CacheLimit = 1024L * 1024 * 1024;

    /// <summary>What a stream URL asks with, <c>as=webm</c>, for a WebM copy if the song needs one.</summary>
    public const string Ask = "webm";

    private readonly SemaphoreSlim _running = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _making = new();
    private readonly SemaphoreSlim _trimming = new(1);

    private string CacheFolder => Path.Combine(options.Value.DataPath, "webm");

    /// <summary>Whether it's a file Safari gets the time of wrong: Opus in Ogg.</summary>
    public static bool Needs(string path, string? codec = null) => MediaTypes.For(path, codec) == "audio/ogg; codecs=opus";

    /// <summary>The song as it's best sent: a WebM copy if asked for one and it needs it, otherwise itself.</summary>
    public async Task<IResult> ServeAsync(string path, string? codec, string? asked, CancellationToken ct)
    {
        if (asked == Ask && Needs(path, codec) && await GetAsync(path, ct) is { } copy)
            return Results.File(copy, "audio/webm", enableRangeProcessing: true);
        return Results.File(path, MediaTypes.Bare(MediaTypes.For(path, codec)), enableRangeProcessing: true);
    }

    /// <summary>The path of the song's WebM copy, made now if there isn't one; null if it can't be made.</summary>
    public async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        var cached = Path.Combine(CacheFolder, Key(info) + ".webm");
        if (File.Exists(cached))
        {
            try
            {
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return cached;
            }
            catch (IOException)
            {
                // Made again below.
            }
        }
        return await _making.GetOrAdd(cached, _ => new Lazy<Task<string?>>(() => MakeAsync(path, cached))).Value.WaitAsync(ct);
    }

    // Not cancelled by whoever asked first: another may be waiting for it too, and it's kept for next time.
    private async Task<string?> MakeAsync(string path, string cached)
    {
        await _running.WaitAsync();
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var part = cached + ".part";
            var start = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-y", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "webm", part })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new Win32Exception("ffmpeg didn't start.");
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                File.Delete(part);
                logger.LogWarning("Couldn't put {Path} in WebM for Safari: {Reason}", path, errors.Trim());
                return null;
            }
            File.Move(part, cached, true);
            _ = TrimAsync();
            return cached;
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't put {Path} in WebM for Safari: {Reason}", path, e.Message);
            return null;
        }
        finally
        {
            _running.Release();
            _making.TryRemove(cached, out _);
        }
    }

    /// <summary>Forgets the least lately played copies once they take more than the limit.</summary>
    private async Task TrimAsync()
    {
        if (!await _trimming.WaitAsync(0))
            return;
        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(CacheFolder).GetFiles("*.webm").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (total > CacheLimit)
                        file.Delete();
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't tidy the WebM copies: {Reason}", e.Message);
        }
        finally
        {
            _trimming.Release();
        }
    }

    // A changed file is a new key, and its old copy is forgotten in time.
    private static string Key(FileInfo file) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..32];
}
