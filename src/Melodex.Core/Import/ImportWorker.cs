using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Retrieval;

namespace Melodex.Core.Import;

// The beets worker: the only thing that writes the music library. It runs in its own container behind a small HTTP
// API (docs/import-worker.md). The core says exactly which MusicBrainz release and recordings the files are, so beets
// confirms a known target instead of guessing, then tags and files them.

/// <summary>Body of <c>POST /v1/import</c>.</summary>
/// <param name="StagingDirectory">Where the files are, relative to the staging volume.</param>
/// <param name="Release">The edition the files belong to.</param>
/// <param name="Files">Each file and the recording it is.</param>
/// <param name="DestinationDirectory">
/// Library-relative folder to put the files in instead of the one the path template gives, so a missing track joins
/// the album you already have wherever it lives. Null to follow the template.
/// </param>
/// <param name="PathTemplate">Beets path format for new albums, for example <c>$albumartist/$album%aunique{}/$track $title</c>.</param>
/// <param name="MusicBrainzServer">The MusicBrainz server beets should ask, so a mirror set for the core applies too.</param>
public sealed record ImportRequest(
    string StagingDirectory,
    ImportRelease Release,
    IReadOnlyList<ImportFile> Files,
    string? DestinationDirectory,
    string PathTemplate,
    string? MusicBrainzServer);

/// <param name="MusicBrainzId">
/// The release's MusicBrainz ID, which pins beets' match. Without one (an edition only another catalogue has), the files
/// are filed with the tags sent.
/// </param>
public sealed record ImportRelease(string? MusicBrainzId, string Title, string Artist, string? Date, int TrackCount);

/// <param name="Path">Relative to the staging directory.</param>
/// <param name="RecordingId">The MusicBrainz recording the file is.</param>
/// <param name="TrackId">The MusicBrainz track (the recording's place on this release), when known.</param>
public sealed record ImportFile(string Path, string RecordingId, string? TrackId, string Title, string Artist, int Disc, int Position, string? Isrc = null);

public sealed record ImportResult(IReadOnlyList<ImportedFile> Files);

/// <param name="Source">The staged file's path, as sent.</param>
/// <param name="Path">Where it is now, relative to the library root.</param>
/// <param name="Tags">The tags beets wrote, by beets field name.</param>
public sealed record ImportedFile(string Source, string Path, double? DurationSeconds, string? Format, IReadOnlyDictionary<string, string> Tags);

/// <summary>Body of <c>POST /v1/delete</c>.</summary>
/// <param name="Paths">Files to delete, relative to the library root.</param>
public sealed record DeleteRequest(IReadOnlyList<string> Paths);

/// <param name="Deleted">The paths deleted, including any already gone.</param>
/// <param name="Failed">Those that couldn't be, with why.</param>
public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<DeleteFailure> Failed);

public sealed record DeleteFailure(string Path, string Error);

/// <summary>Body of <c>POST /v1/format</c>.</summary>
/// <param name="Artwork">Images the files name, by key, so a cover shared by an album is sent once.</param>
/// <param name="PathTemplate">Move each file to where this beets path format puts it, by its new tags; null to leave it.</param>
/// <param name="FolderArtwork">The key of an image in <paramref name="Artwork"/> to write as each file's folder image too.</param>
public sealed record FormatRequest(IReadOnlyList<FormatFile> Files, IReadOnlyDictionary<string, FormatArtwork> Artwork, string? PathTemplate,
    string? FolderArtwork = null);

/// <param name="Path">Relative to the library root.</param>
/// <param name="Tags">Tags to write, by beets field name; the rest stay as they are. An empty value clears one.</param>
/// <param name="Artwork">The key of the image to embed as the front cover; null to leave the embedded art.</param>
/// <param name="RemoveArtwork">Take the embedded art out.</param>
/// <param name="Lrc">Lyrics to write to an .lrc file beside it, with the same name; null to leave any there.</param>
public sealed record FormatFile(string Path, IReadOnlyDictionary<string, string> Tags, string? Artwork, bool RemoveArtwork, string? Lrc = null);

/// <param name="Data">The image, base64.</param>
public sealed record FormatArtwork(string Data);

/// <param name="RemovedFolders">Folders moving the files left with no music in them, and so removed, with their artwork and
/// the like, by the topmost of each. Relative to the library root.</param>
public sealed record FormatResult(IReadOnlyList<FormattedFile> Files, IReadOnlyList<string>? RemovedFolders = null);

/// <summary>Body of <c>POST /v1/folders/musicless</c>.</summary>
/// <param name="Remove">Remove them, rather than only list them.</param>
public sealed record MusiclessRequest(bool Remove);

/// <summary>Answer to <c>POST /v1/folders/musicless</c>.</summary>
/// <param name="Folders">The library's folders holding nothing but artwork, notes and the like, or nothing at all: the
/// topmost of each, relative to the library root. Removed, when asked to.</param>
/// <param name="Failed">Those that couldn't be removed, with why.</param>
public sealed record MusiclessResult(IReadOnlyList<string> Folders, IReadOnlyList<DeleteFailure> Failed);

/// <param name="Path">The file's path, as sent.</param>
/// <param name="NewPath">Where it is now; null when it couldn't be formatted.</param>
/// <param name="Error">Why it couldn't be.</param>
public sealed record FormattedFile(string Path, string? NewPath, long? Size, string? Error);

/// <summary>Imports staged files into the library.</summary>
public interface IImportWorker
{
    Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct);

    /// <summary>Deletes files from the library, and any folder left with nothing but artwork and the like.</summary>
    Task<DeleteResult> DeleteAsync(DeleteRequest request, CancellationToken ct);

    /// <summary>Rewrites files in the library: their tags, embedded cover and place.</summary>
    Task<FormatResult> FormatAsync(FormatRequest request, CancellationToken ct);

    /// <summary>The library's folders with no music in them, and with <see cref="MusiclessRequest.Remove"/>, removes them.</summary>
    Task<MusiclessResult> MusiclessAsync(MusiclessRequest request, CancellationToken ct);

    Task<HealthReport> CheckHealthAsync(CancellationToken ct);

    /// <summary>Whether files can go from staging into the library as reflinks, sharing their data on disk.</summary>
    Task<ReflinkCheck> CheckReflinksAsync(CancellationToken ct);
}

/// <summary>Answer to <c>POST /v1/reflinks</c>.</summary>
/// <param name="Reason">Why not, when they can't.</param>
public sealed record ReflinkCheck(bool Works, string? Reason = null);

public sealed class ImportWorkerOptions
{
    /// <summary>The beets worker's base URL.</summary>
    public string Url { get; set; } = "http://beets-worker:8080";

    /// <summary>A beets import asks MusicBrainz for the release, and moves files; albums take a while.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>The beets worker over HTTP.</summary>
public sealed class HttpImportWorker(HttpClient http) : IImportWorker
{
    public const string HttpClientName = "import";

    public static void Configure(IServiceProvider services, HttpClient http)
    {
        var options = services.GetRequiredService<IOptions<ImportWorkerOptions>>().Value;
        http.BaseAddress = new Uri(options.Url.EndsWith('/') ? options.Url : options.Url + "/");
        http.Timeout = options.Timeout;
    }

    public async Task<ImportResult> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/import", request, RetrievalJson.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<ImportResult>(RetrievalJson.Options, ct))!;
    }

    public async Task<DeleteResult> DeleteAsync(DeleteRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/delete", request, RetrievalJson.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<DeleteResult>(RetrievalJson.Options, ct))!;
    }

    public async Task<FormatResult> FormatAsync(FormatRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/format", request, RetrievalJson.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<FormatResult>(RetrievalJson.Options, ct))!;
    }

    public async Task<MusiclessResult> MusiclessAsync(MusiclessRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/folders/musicless", request, RetrievalJson.Options, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<MusiclessResult>(RetrievalJson.Options, ct))!;
    }

    public async Task<ReflinkCheck> CheckReflinksAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("v1/reflinks", null, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImportException(await ErrorAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<ReflinkCheck>(RetrievalJson.Options, ct))!;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("v1/health", ct);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<HealthReport>(RetrievalJson.Options, ct))!
                : new HealthReport(HealthStatus.Unhealthy, await ErrorAsync(response, ct));
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach the beets worker at {http.BaseAddress}: {e.Message}");
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<RetrievalError>(RetrievalJson.Options, ct) is { } error)
                return error.Error;
        }
        catch (JsonException)
        {
        }
        return $"The beets worker answered {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}

/// <summary>Beets couldn't import the files, with its reason.</summary>
public sealed class ImportException(string message) : Exception(message);
