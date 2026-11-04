using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Retrieval;

namespace Melodex.Core.Tests.Support;

/// <summary>
/// Retrieval plugins for tests, speaking the real HTTP contract in process. Each host name is one plugin. Search
/// offers candidates of the target's own length (or off by <see cref="FakeSource.OffBySeconds"/>), and fetch writes
/// silent WAV files of that length into staging, so verification runs on real files.
/// </summary>
public sealed class FakeRetrieval(string stagingRoot) : HttpMessageHandler
{
    public ConcurrentDictionary<string, FakeSource> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, FetchStatus> _jobs = new();
    private readonly ConcurrentDictionary<string, (FakeSource Source, int Polls)> _running = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!Sources.TryGetValue(request.RequestUri!.Host, out var source))
            return new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = request };
        var path = request.RequestUri.AbsolutePath;

        if (path == "/v1/manifest")
            return Json(request, source.Manifest);
        if (path == "/v1/health")
            return Json(request, HealthReport.Healthy());
        if (path == "/v1/search")
        {
            var search = (await request.Content!.ReadFromJsonAsync<SearchRequest>(RetrievalJson.Options, ct))!;
            Interlocked.Increment(ref source.Searches);
            if (!source.Finds)
                return Json(request, new SearchResponse([]));
            var target = search.Target;
            var candidates = Enumerable.Range(0, source.Candidates).Select(i => new Candidate(
                $"{i}|{JsonSerializer.Serialize(target, RetrievalJson.Options)}", target.Kind, target.Title, target.Artist, target.Album,
                target.DurationSeconds, source.Name, null, "wav", 64, 0.9 - i * 0.1)).ToList();
            return Json(request, new SearchResponse(candidates));
        }
        if (path == "/v1/fetch" && request.Method == HttpMethod.Post)
        {
            var fetch = (await request.Content!.ReadFromJsonAsync<FetchRequest>(RetrievalJson.Options, ct))!;
            var index = int.Parse(fetch.Candidate.CandidateId.Split('|')[0]);
            Interlocked.Increment(ref source.Fetches);
            var directory = Path.Combine(stagingRoot, fetch.StagingDirectory);
            var off = source.OffBySeconds(index);
            var files = new List<FetchedFile>();
            if (fetch.Target.Kind == RetrievalKind.Album)
                foreach (var track in fetch.Target.Tracks!)
                {
                    var name = $"{track.Disc}-{track.Position:00}.wav";
                    var seconds = (track.DurationSeconds ?? 60) + off;
                    Wav.Write(Path.Combine(directory, name), seconds);
                    files.Add(new FetchedFile(name, "pcm", "wav", 64, seconds, track.Disc, track.Position));
                }
            else
            {
                var seconds = (fetch.Target.DurationSeconds ?? 60) + off;
                Wav.Write(Path.Combine(directory, "track.wav"), seconds);
                files.Add(new FetchedFile("track.wav", "pcm", "wav", 64, seconds));
            }
            var status = new FetchStatus(fetch.JobId, FetchState.Completed, 1, files);
            _jobs[fetch.JobId] = status;
            if (source.ProgressSteps is not null)
                _running[fetch.JobId] = (source, 0);
            return Json(request, status with { State = FetchState.Running, Progress = null, Files = null }, HttpStatusCode.Accepted);
        }
        if (path.StartsWith("/v1/fetch/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/v1/fetch/".Length..]);
            if (request.Method == HttpMethod.Delete && _running.TryRemove(id, out var cancelled))
            {
                Interlocked.Increment(ref cancelled.Source.Cancels);
                return Json(request, _jobs[id] = new FetchStatus(id, FetchState.Cancelled));
            }
            // A slow fetch moves through its steps, a poll each, and then stays on the last one.
            if (_running.TryGetValue(id, out var running))
            {
                _running[id] = running with { Polls = running.Polls + 1 };
                var steps = running.Source.ProgressSteps!;
                return Json(request, new FetchStatus(id, FetchState.Running, steps[Math.Min(running.Polls, steps.Count - 1)]));
            }
            return _jobs.TryGetValue(id, out var status)
                ? Json(request, status)
                : Json(request, new RetrievalError("unknown job", "unknown-job"), HttpStatusCode.NotFound);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private static HttpResponseMessage Json<T>(HttpRequestMessage request, T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { RequestMessage = request, Content = JsonContent.Create(body, options: RetrievalJson.Options) };
}

/// <summary>One fake retrieval plugin's behaviour.</summary>
public sealed class FakeSource(string name, bool albums = false)
{
    public string Name { get; } = name;

    /// <summary>Whether search finds anything.</summary>
    public bool Finds { get; set; } = true;

    /// <summary>How many candidates search offers.</summary>
    public int Candidates { get; set; } = 3;

    /// <summary>How far each candidate's file is from the target's length, by candidate index.</summary>
    public Func<int, double> OffBySeconds { get; set; } = _ => 0;

    /// <summary>When set, a fetch reports these progress values one poll at a time and then never finishes.</summary>
    public IReadOnlyList<double>? ProgressSteps { get; set; }

    public int Searches;
    public int Fetches;
    public int Cancels;

    public PluginManifest Manifest { get; } = new()
    {
        Id = $"test.retrieval.{name.ToLowerInvariant()}",
        Name = name,
        Version = "1.0.0",
        Type = PluginType.Retrieval,
        ContractVersion = "1.0",
        Capabilities = albums
            ? [RetrievalCapabilities.FetchTrack, RetrievalCapabilities.FetchAlbum, RetrievalCapabilities.Cancel]
            : [RetrievalCapabilities.FetchTrack, RetrievalCapabilities.Cancel],
        SettingsSchema = JsonDocument.Parse("""{"type":"object","properties":{"format":{"type":"string","enum":["wav"],"default":"wav"}}}""").RootElement,
    };
}

/// <summary>Writes silent WAV files of a given length: 8 kHz, mono, 8-bit, small enough for tests.</summary>
public static class Wav
{
    public static void Write(string path, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int rate = 8000;
        var samples = (int)(Math.Max(0.1, seconds) * rate);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + samples);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1); // PCM
        file.Write((short)1); // mono
        file.Write(rate);
        file.Write(rate); // bytes per second
        file.Write((short)1); // block align
        file.Write((short)8); // bits per sample
        file.Write("data"u8);
        file.Write(samples);
        file.Write(Enumerable.Repeat((byte)128, samples).ToArray());
    }
}
