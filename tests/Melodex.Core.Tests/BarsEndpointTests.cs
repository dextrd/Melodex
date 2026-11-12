using System.Net;
using Melodex.Client;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The visualiser's bars as the player fetches them: by a song's stream URL, while the visualiser is on (#89).</summary>
[Collection(RequestCollection.Name)]
public sealed class BarsEndpointTests(RequestFixture fixture)
{
    private MelodexClient Api => fixture.Api;

    [TestLibraryFact]
    public async Task A_songs_bars_come_by_its_stream_url_unless_the_visualiser_is_off()
    {
        var track = (await Api.GetLibraryTracksAsync()).First(t => t.Path.EndsWith(".flac"));
        var play = (await Api.GetFilePlayInfoAsync(track.FileId))!;
        var url = MelodexClient.BarsUrl(play.StreamUrl);
        Assert.Contains($"library/files/{track.FileId}/bars?", url);
        // As an audio element's would be: signed, without a login.
        using var http = fixture.Factory.CreateClient();
        var playback = await Api.GetPlaybackAsync();
        Assert.Equal((VisualiserBars.OnDemand, GaplessPlayback.SameAlbum), (playback.Visualiser, playback.Gapless));

        using (var bars = await http.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, bars.StatusCode);
            Assert.Contains("gzip", bars.Content.Headers.ContentEncoding);
            var body = await bars.Content.ReadAsByteArrayAsync();
            Assert.True(Melodex.Core.Listening.BarsFile.IsBars(new MemoryStream(body)));
        }
        using (var forged = await http.GetAsync(url.Replace("sig=", "sig=x")))
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using (var warmed = await http.GetAsync(url + "&warm=true"))
            Assert.Equal(HttpStatusCode.Accepted, warmed.StatusCode);

        try
        {
            await Api.SavePlaybackSettingsAsync(new SavePlayback(VisualiserBars.Off, Gapless: GaplessPlayback.Always));
            playback = await Api.GetPlaybackAsync();
            Assert.Equal((VisualiserBars.Off, GaplessPlayback.Always), (playback.Visualiser, playback.Gapless));
            using var off = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
            var status = await Api.GetPlaybackSettingsAsync();
            Assert.True(status.Songs > 0);
            Assert.True(status.EstimatedBytes > 0);
            Assert.Equal(0, status.BarsFiles);
        }
        finally
        {
            await Api.SavePlaybackSettingsAsync(new SavePlayback(VisualiserBars.OnDemand));
        }
    }
}
