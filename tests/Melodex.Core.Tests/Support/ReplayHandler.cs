using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Melodex.Core.Tests.Support;

/// <summary>
/// Answers plugin HTTP requests from recorded responses, so tests that talk to MusicBrainz run offline and give the same
/// answer every time. With <c>MELODEX_RECORD=1</c> it passes requests through to the real service and records them;
/// do that after changing what the plugin asks for, and commit the new fixtures. With <c>MELODEX_RECORD=missing</c> it
/// records only what hasn't been recorded, so a new test's requests are added without the others' answers changing as
/// MusicBrainz's data does.
/// </summary>
public sealed class ReplayHandler(string fixtures) : DelegatingHandler(new SocketsHttpHandler())
{
    public static bool Recording => Environment.GetEnvironmentVariable("MELODEX_RECORD") is "1" or "missing";

    private static bool OnlyMissing => Environment.GetEnvironmentVariable("MELODEX_RECORD") == "missing";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Keyed by method and path, not host, so a fixture serves any mirror the plugin is pointed at.
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        var path = Path.Combine(fixtures, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".json.gz");

        if (Recording && !(OnlyMissing && File.Exists(path)))
        {
            var response = await base.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Overload responses are retried by the core; recording one would replay the failure.
            if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
            {
                Directory.CreateDirectory(fixtures);
                await using var file = new GZipStream(File.Create(path), CompressionLevel.SmallestSize);
                await JsonSerializer.SerializeAsync(file, new Fixture(key, (int)response.StatusCode, body), cancellationToken: cancellationToken);
            }
            return Rebuild(request, (int)response.StatusCode, body);
        }

        if (!File.Exists(path))
            throw new InvalidOperationException($"No recorded response for {key}. Run the tests with MELODEX_RECORD=1 to record it.");
        await using (var file = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        {
            var fixture = (await JsonSerializer.DeserializeAsync<Fixture>(file, cancellationToken: cancellationToken))!;
            return Rebuild(request, fixture.Status, fixture.Body);
        }
    }

    private static HttpResponseMessage Rebuild(HttpRequestMessage request, int status, string body) =>
        new((HttpStatusCode)status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Fixture(string Request, int Status, string Body);
}
