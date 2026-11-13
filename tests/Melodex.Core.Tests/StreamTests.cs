using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Core.Listening;

namespace Melodex.Core.Tests;

public sealed class StreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_signed_url_works_for_its_own_path_until_it_expires()
    {
        var signer = new StreamSigner();
        var url = signer.Sign("api/v1/listen/abc/stream", Now);
        var query = url.Split('?')[1].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var exp = long.Parse(query["exp"]);

        Assert.True(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now));
        Assert.True(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now + StreamSigner.Lifetime));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"], Now + StreamSigner.Lifetime + TimeSpan.FromSeconds(1)));
        Assert.False(signer.Verify("api/v1/listen/abd/stream", exp, query["sig"], Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp + 3600, query["sig"], Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, query["sig"][..^1] + "x", Now));
        Assert.False(signer.Verify("api/v1/listen/abc/stream", exp, null, Now));
    }

    [Theory]
    [InlineData("a.opus", null, "audio/ogg; codecs=opus")]
    [InlineData("a.ogg", null, "audio/ogg; codecs=vorbis")]
    [InlineData("a.webm", "opus", "audio/webm; codecs=opus")]
    [InlineData("a.m4a", null, "audio/mp4; codecs=mp4a.40.2")]
    [InlineData("a.m4a", "alac", "audio/mp4; codecs=alac")]
    [InlineData("a.MP3", null, "audio/mpeg")]
    [InlineData("a.flac", null, "audio/flac")]
    [InlineData("a.wav", null, "audio/wav")]
    [InlineData("a.xyz", null, "application/octet-stream")]
    public void Files_are_served_as_a_type_browsers_can_check(string path, string? codec, string expected)
    {
        Assert.Equal(expected, MediaTypes.For(path, codec));
        Assert.Equal(expected.Split(';')[0], MediaTypes.Bare(expected));
    }

    // Safari misjudges an Ogg Opus file's length, which doesn't say it up front, so the player goes by the server's (#93).
    [FfmpegFact]
    public async Task A_listen_now_files_length_is_measured_from_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"melodex-{Guid.NewGuid():N}.opus");
        try
        {
            using (var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=7.5", "-c:a", "libopus", path])
                   { RedirectStandardError = true })!)
                await ffmpeg.WaitForExitAsync();

            var probed = MediaTypes.Probe(path);

            Assert.Equal("opus", probed.Codec);
            Assert.NotNull(probed.Duration);
            Assert.InRange(probed.Duration.Value.TotalSeconds, 7.4, 7.6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Safari stops counting short of the end of Opus in Ogg, so it's sent a WebM copy, the sound untouched (#93).
    [FfmpegFact]
    public async Task Opus_in_ogg_is_repacked_in_webm_once_and_nothing_else_is()
    {
        var root = Path.Combine(Path.GetTempPath(), $"melodex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var song = Path.Combine(root, "song.opus");
            using (var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=7.5", "-c:a", "libopus", song])
                   { RedirectStandardError = true })!)
                await ffmpeg.WaitForExitAsync();
            var copies = new WebmCopies(Options.Create(new MelodexOptions { DataPath = Path.Combine(root, "data") }), NullLogger<WebmCopies>.Instance);

            Assert.True(WebmCopies.Needs(song));
            Assert.True(WebmCopies.Needs("song.ogg", "opus"));
            Assert.False(WebmCopies.Needs("song.ogg"));
            Assert.False(WebmCopies.Needs("song.flac"));
            Assert.False(WebmCopies.Needs("song.webm", "opus"));

            var copy = await copies.GetAsync(song, CancellationToken.None);
            Assert.NotNull(copy);
            Assert.Equal(Path.Combine(root, "data", "webm"), Path.GetDirectoryName(copy));
            var probed = MediaTypes.Probe(copy);
            Assert.Equal("opus", probed.Codec);
            Assert.InRange(probed.Duration!.Value.TotalSeconds, 7.4, 7.6);
            // Made once: asked again, the same file, left as it was.
            var made = File.GetLastWriteTimeUtc(copy);
            Assert.Equal(copy, await Task.WhenAll(copies.GetAsync(song, CancellationToken.None), copies.GetAsync(song, CancellationToken.None)).ContinueWith(t => t.Result.Distinct().Single()));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(copy)!));
            Assert.True(File.GetLastWriteTimeUtc(copy) >= made);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
