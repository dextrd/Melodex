using System.Collections.Concurrent;
using Melodex.Contracts.Plugins;
using Melodex.Core.Import;

namespace Melodex.Core.Tests.Support;

/// <summary>
/// Stands in for the beets worker: moves staged files into the library under the destination folder, or
/// <c>Artist/Album</c>, named <c>disc-position title</c>. Records every request so tests can check what beets was told.
/// </summary>
public sealed class FakeImporter(string stagingRoot, string libraryRoot) : IImportWorker
{
    public ConcurrentQueue<ImportRequest> Requests { get; } = new();

    public Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        var folder = request.DestinationDirectory ?? $"{Safe(request.Release.Artist)}/{Safe(request.Release.Title)}";
        var files = new List<ImportedFile>();
        foreach (var file in request.Files)
        {
            var path = $"{folder}/{file.Disc}-{file.Position:00} {Safe(file.Title)}{Path.GetExtension(file.Path)}";
            var target = Path.Combine(libraryRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(stagingRoot, request.StagingDirectory, file.Path), target, overwrite: true);
            // Without a release, the worker files them as tagged, with no MusicBrainz IDs.
            var tags = new Dictionary<string, string> { ["title"] = file.Title };
            if (request.Release.MusicBrainzId is { } release)
            {
                tags["mb_trackid"] = file.RecordingId;
                tags["mb_albumid"] = release;
            }
            if (file.Isrc is { } isrc)
                tags["isrc"] = isrc;
            files.Add(new ImportedFile(file.Path, path, null, "wav", tags));
        }
        return Task.FromResult(new ImportResult(files));
    }

    public Task<HealthReport> CheckHealthAsync(CancellationToken ct) => Task.FromResult(HealthReport.Healthy());

    /// <summary>What the reflink check answers.</summary>
    public ReflinkCheck Reflinks { get; set; } = new(true);

    public Task<ReflinkCheck> CheckReflinksAsync(CancellationToken ct) => Task.FromResult(Reflinks);

    // As the worker does: the files, then any folder left with only artwork in it.
    public Task<DeleteResult> DeleteAsync(DeleteRequest request, CancellationToken ct)
    {
        foreach (var path in request.Paths)
        {
            var full = Path.Combine(libraryRoot, path);
            File.Delete(full);
            for (var folder = Path.GetDirectoryName(full)!; folder != libraryRoot && Directory.Exists(folder); folder = Path.GetDirectoryName(folder)!)
            {
                if (Directory.EnumerateDirectories(folder).Any() || Directory.EnumerateFiles(folder).Any(f => !f.EndsWith(".jpg")))
                    break;
                Directory.Delete(folder, recursive: true);
            }
        }
        return Task.FromResult(new DeleteResult(request.Paths, []));
    }

    public ConcurrentQueue<FormatRequest> Formats { get; } = new();

    // Tags and embedded covers aren't written, but folder images and .lrc files are; moving goes to Artist/Album/title, as the tags say.
    public Task<FormatResult> FormatAsync(FormatRequest request, CancellationToken ct)
    {
        Formats.Enqueue(request);
        var results = new List<FormattedFile>();
        foreach (var file in request.Files)
        {
            var full = Path.Combine(libraryRoot, file.Path);
            if (!File.Exists(full))
            {
                results.Add(new FormattedFile(file.Path, null, null, "It isn't in the music folder any more."));
                continue;
            }
            if (file.Lrc == "")
                File.Delete(Path.ChangeExtension(full, ".lrc"));
            else if (file.Lrc is not null)
                File.WriteAllText(Path.ChangeExtension(full, ".lrc"), file.Lrc);
            var path = file.Path;
            if (request.PathTemplate is not null && file.Tags.TryGetValue("albumartist", out var artist) && file.Tags.TryGetValue("album", out var album)
                && file.Tags.TryGetValue("title", out var title))
            {
                path = $"{Safe(artist)}/{Safe(album)}/{Safe(title)}{Path.GetExtension(file.Path)}";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(libraryRoot, path))!);
                File.Move(full, Path.Combine(libraryRoot, path), overwrite: true);
            }
            results.Add(new FormattedFile(file.Path, path, new FileInfo(Path.Combine(libraryRoot, path)).Length, null));
            if (request.FolderArtwork is { } folderArt)
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.Combine(libraryRoot, path))!, "cover.jpg"),
                    Convert.FromBase64String(request.Artwork[folderArt].Data));
        }
        return Task.FromResult(new FormatResult(results));
    }

    private static string Safe(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars()));
}
