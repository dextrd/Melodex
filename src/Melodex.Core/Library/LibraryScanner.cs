using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Core.Data;
using LyricsKind = Melodex.Client.LyricsKind;
using ScanFileState = Melodex.Client.ScanFileState;

namespace Melodex.Core.Library;

/// <summary>The tags matching uses, as read from a file.</summary>
public sealed record FileTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? Track { get; init; }
    public int? Disc { get; init; }
    public string? Date { get; init; }
    public string? Isrc { get; init; }
    public string? MusicBrainzRecordingId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }
    public string? MusicBrainzArtistId { get; init; }
    public string? MusicBrainzAlbumArtistId { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static FileTags Parse(string json) => JsonSerializer.Deserialize<FileTags>(json, Json) ?? new FileTags();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Walks the music folder and records every audio file and its tags. It never writes to the library.</summary>
public sealed class LibraryScanner(MelodexDbContext db, IOptions<MelodexOptions> options, ScanFeed feed, ILogger<LibraryScanner> logger)
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aif", ".aiff", ".ape", ".wv", ".mpc", ".dsf",
    };

    /// <summary>How the beets worker names the copy of a file it's formatting, beside it (its formatter.py).</summary>
    public const string FormattingCopyPrefix = ".melodex-";

    /// <param name="SkippedFolders">Folders that couldn't be read; their files are kept as they were.</param>
    public sealed record Result(int Files, int Added, int Changed, int Removed, int Unreadable, int SkippedFolders);

    public async Task<Result> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var root = Path.GetFullPath(options.Value.LibraryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The music folder {root} doesn't exist.");

        var known = await db.LibraryFiles.ToDictionaryAsync(f => f.Path, ct);
        var seen = new HashSet<string>();
        int added = 0, changed = 0, unreadable = 0;

        var walk = Walk(root);
        // In path order, so the Activity page lists them as they'd appear in a file browser.
        var files = walk.Files.Where(f => AudioExtensions.Contains(Path.GetExtension(f))).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0)
            // Never treat an empty or unreadable folder as "everything was deleted".
            throw new InvalidOperationException(NothingFound(root, walk));
        logger.LogInformation("Found {Count} audio files in {Root}", files.Count, root);
        foreach (var folder in walk.Skipped.Take(50))
            logger.LogWarning("Couldn't open folder {Folder}: permission denied (the container's user needs read access)", RelativePath(folder));
        if (walk.Skipped.Count > 50)
            logger.LogWarning("...and {Count} more folders that couldn't be opened", walk.Skipped.Count - 50);

        var index = 0;
        foreach (var fullPath in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++index % 50 == 0)
                progress?.Report($"Reading files: {index} of {files.Count}");
            var path = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            seen.Add(path);
            var info = new FileInfo(fullPath);
            var modified = info.LastWriteTimeUtc;

            if (known.TryGetValue(path, out var existing) && existing.Size == info.Length && existing.ModifiedAt == modified)
                continue;

            if (existing?.HiddenAt is not null)
            {
                // Hidden files are kept as they are: they're not in the library.
                existing.Size = info.Length;
                existing.ModifiedAt = modified;
                continue;
            }
            feed.Set(path, ScanFileState.Reading);
            var read = Read(fullPath);
            if (read is null)
            {
                unreadable++;
                feed.Set(path, ScanFileState.Review, detail: "Couldn't read the file");
                continue;
            }
            feed.Set(path, ScanFileState.Queued, read.Value.Tags.Title, read.Value.Tags.Artist);

            if (existing is null)
            {
                existing = new LibraryFile { Path = path };
                db.LibraryFiles.Add(existing);
                added++;
            }
            else
            {
                changed++;
            }
            existing.Size = info.Length;
            existing.ModifiedAt = modified;
            existing.Format = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
            existing.Duration = read.Value.Duration;
            existing.TagsJson = read.Value.Tags.ToJson();
            existing.Lyrics = read.Value.Lyrics;
            existing.LyricsCheckedAt = null;
            existing.ScannedAt = DateTime.UtcNow;
            // Changed files are matched again from scratch. Excluded ones keep their new tags, which the library shows,
            // but nothing matches them.
            if (existing.ExcludedAt is null)
            {
                existing.MatchedAt = null;
                existing.TrackId = existing.EditionId = existing.SuggestedTrackId = existing.SuggestedEditionId = null;
                existing.MatchMethod = MatchMethod.Unmatched;
            }

            if ((added + changed) % 200 == 0)
                await db.SaveChangesAsync(ct);
        }

        var skipped = walk.Skipped.Select(d => Path.GetRelativePath(root, d).Replace('\\', '/') + "/").ToList();
        var removed = known.Values.Where(f => !seen.Contains(f.Path) && !skipped.Any(f.Path.StartsWith)).ToList();
        db.LibraryFiles.RemoveRange(removed);
        await db.SaveChangesAsync(ct);

        var result = new Result(seen.Count - unreadable, added, changed, removed.Count, unreadable, walk.Skipped.Count);
        logger.LogInformation("Read the library: {Files} files, {Added} new, {Changed} changed, {Removed} removed, {Unreadable} unreadable, {Skipped} folders not opened",
            result.Files, added, changed, removed.Count, unreadable, walk.Skipped.Count);
        return result;
    }

    private sealed record Walked(List<string> Files, List<string> Skipped, int Entries);

    /// <summary>Every file under the root, noting the folders that couldn't be read rather than silently skipping them.</summary>
    private static Walked Walk(string root)
    {
        var files = new List<string>();
        var skipped = new List<string>();
        var entries = 0;
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var dir))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    entries++;
                    if (entry is DirectoryInfo sub)
                        pending.Push(sub.FullName);
                    // A copy the beets worker is formatting, about to take the file's place.
                    else if (!entry.Name.StartsWith(FormattingCopyPrefix, StringComparison.Ordinal))
                        files.Add(entry.FullName);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                skipped.Add(dir);
            }
        }
        // A fixed order, whatever the filesystem returns, so scans are repeatable.
        files.Sort(StringComparer.Ordinal);
        return new Walked(files, skipped, entries);
    }

    private static string NothingFound(string root, Walked walk)
    {
        if (walk.Skipped.Contains(root))
            return $"Melodex can't read the music folder {root}. Give the container's user read access to it (for example chmod -R o+rX on the host), or on SELinux hosts mount it with :z.";
        if (walk.Entries == 0)
            return $"The music folder {root} is empty. Check that MUSIC_PATH points at your music library.";
        if (walk.Skipped.Count > 0)
            return $"Found no audio files in {root}, and couldn't read {walk.Skipped.Count} folders in it, for example {walk.Skipped[0]}. Give the container's user read access (for example chmod -R o+rX on the host).";
        return $"Found no audio files in {root} ({walk.Files.Count} other files). Check that MUSIC_PATH points at your music library.";
    }

    private (FileTags Tags, TimeSpan? Duration, LyricsKind Lyrics)? Read(string path)
    {
        try
        {
            return ReadTags(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One bad or locked file never stops a scan; it's listed in the log and skipped.
            var reason = e is UnauthorizedAccessException
                ? "permission denied (the container's user needs read access)"
                : e.Message;
            logger.LogWarning("Couldn't read {Path}: {Reason}", RelativePath(path), reason);
            return null;
        }
    }

    /// <summary>A file's tags, length and lyrics (from an .lrc beside it, else its tags). Throws if it can't be read as audio.</summary>
    public static (FileTags Tags, TimeSpan? Duration, LyricsKind Lyrics) ReadTags(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var sidecar = LyricsFiles.SidecarOf(path);
        var lyrics = File.Exists(sidecar) ? LyricsFiles.KindOf(path) : LyricsFiles.KindOfText(tag.Lyrics);
        return (new FileTags
        {
            Title = Blank(tag.Title),
            Artist = Blank(tag.JoinedPerformers),
            AlbumArtist = Blank(tag.JoinedAlbumArtists),
            Album = Blank(tag.Album),
            Track = tag.Track == 0 ? null : (int)tag.Track,
            Disc = tag.Disc == 0 ? null : (int)tag.Disc,
            Date = tag.Year == 0 ? null : tag.Year.ToString(),
            Isrc = Blank(tag.ISRC),
            MusicBrainzRecordingId = Blank(tag.MusicBrainzTrackId),
            MusicBrainzReleaseId = Blank(tag.MusicBrainzReleaseId),
            MusicBrainzReleaseGroupId = Blank(tag.MusicBrainzReleaseGroupId),
            MusicBrainzArtistId = Blank(tag.MusicBrainzArtistId?.Split('/', ';')[0]),
            MusicBrainzAlbumArtistId = Blank(tag.MusicBrainzReleaseArtistId?.Split('/', ';')[0]),
        }, file.Properties?.Duration is { Ticks: > 0 } d ? d : null, lyrics);
    }

    private string RelativePath(string path) =>
        Path.GetRelativePath(Path.GetFullPath(options.Value.LibraryPath), path).Replace('\\', '/');

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
