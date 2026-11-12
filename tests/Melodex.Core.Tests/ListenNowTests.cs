using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Tests.Support;
using Xunit.Abstractions;
using ListenNowState = Melodex.Client.ListenNowState;

namespace Melodex.Core.Tests;

/// <summary>Listen now, end to end through the client contract: request, follow, play, add or discard, and expiry.</summary>
[Collection(RequestCollection.Name)]
public sealed class ListenNowTests(RequestFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    // Albums the request tests fetch, so these tests stay out of their way.
    private static readonly string[] Taken = ["Human After All", "Third", "Homework", "Dummy", "Portishead", "Roseland NYC Live", "Magic Doors", "Random Access Memories"];

    private MelodexClient Api => fixture.Api;

    public Task InitializeAsync() => TestLibrary.Exists ? fixture.RemoveSourcesAsync() : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [TestLibraryFact]
    public async Task An_album_is_fetched_to_play_in_order_and_one_track_added_without_fetching_again()
    {
        var source = new FakeSource("listen");
        await fixture.AddSourceAsync(source);
        var album = await MissingAlbumAsync(0);

        // The album's editions are refreshed when it's asked for, so compare with the edition it chose.
        var started = await Api.StartListeningAsync(new StartListen(RequestKind.Album, album.Id));
        var tracks = (await Api.GetAlbumAsync(album.Id, started[0].EditionId))!.Tracks;
        Assert.Equal(tracks.Where(t => t.Status == TrackStatus.Missing).Select(t => t.TrackId), started.Select(i => i.TrackId));
        Assert.All(started, i => Assert.Equal(album.Id, i.AlbumId));
        var playable = await WaitAsync(started, ListenNowState.Playable);
        var fetches = source.Fetches;
        Assert.Equal(started.Count, fetches);

        // Asking again gives the same tracks, without fetching them again.
        var again = await Api.StartListeningAsync(new StartListen(RequestKind.Album, album.Id));
        Assert.Equal(started.Select(i => i.Id), again.Select(i => i.Id));
        Assert.Equal(fetches, source.Fetches);

        // Nothing reached the library, and a Get button wouldn't think it's on its way.
        Assert.Equal(AlbumStatus.Missing, (await fixture.AlbumAsync(album.ArtistName(), album.Title)).Status);
        Assert.DoesNotContain(await Api.ListRequestsAsync(activeOnly: true), r => r.TargetId == album.Id);

        // Play: the stream needs its signature, and seeks.
        var first = playable[0];
        Assert.Equal("audio/wav", first.MediaType);
        Assert.NotNull(first.ExpiresAt);
        var http = fixture.Factory.CreateSignedInClient();
        using (var response = await http.GetAsync(first.StreamUrl))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 1000);
        }
        using (var ranged = new HttpRequestMessage(HttpMethod.Get, first.StreamUrl) { Headers = { Range = new RangeHeaderValue(0, 99) } })
        using (var response = await http.SendAsync(ranged))
        {
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(100, (await response.Content.ReadAsByteArrayAsync()).Length);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(first.StreamUrl!.Split('?')[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(first.StreamUrl.Replace("sig=", "sig=x"))).StatusCode);

        // Add one: it's filed from the file it has, and the listen-now copy goes.
        var adding = await Api.AddListeningAsync(first.Id);
        Assert.Equal(ListenNowState.Importing, adding.State);
        var added = (await WaitAsync([adding], ListenNowState.InLibrary))[0];
        Assert.Null(added.StreamUrl);
        Assert.Equal(fetches, source.Fetches);
        Assert.False(File.Exists(Path.Combine(fixture.ListenNow, first.Id.ToString("N") + ".wav")));
        Assert.Equal(AlbumStatus.Partial, (await fixture.AlbumAsync(album.ArtistName(), album.Title)).Status);
        Assert.NotNull(await Api.GetPlayInfoAsync(first.TrackId));

        // Discard another: its file goes at once.
        var second = playable[1];
        await Api.DiscardListeningAsync(second.Id);
        Assert.Equal(ListenNowState.Expired, (await Api.GetListeningAsync(second.Id))!.State);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(second.StreamUrl)).StatusCode);
        Assert.DoesNotContain(await Api.ListListeningAsync(), i => i.Id == second.Id);
        // It's still in the list of every download.
        Assert.Contains(await Api.ListListeningAsync(all: true), i => i.Id == second.Id && i.State == ListenNowState.Expired);
    }

    [TestLibraryFact]
    public async Task Tracks_you_have_play_from_the_library_and_cant_be_listened_to()
    {
        var owned = (await Api.GetLibraryTracksAsync()).First();
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => Api.StartListeningAsync(new StartListen(RequestKind.Track, owned.TrackId)));
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);

        var play = (await Api.GetPlayInfoAsync(owned.TrackId))!;
        Assert.Equal(owned.FileId, play.FileId);
        using var response = await fixture.Factory.CreateSignedInClient().GetAsync(play.StreamUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new FileInfo(Path.Combine(fixture.Music, owned.Path)).Length, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestLibraryFact]
    public async Task Unplayed_tracks_expire_and_their_files_are_deleted()
    {
        await fixture.AddSourceAsync(new FakeSource("expiry"));
        var album = await MissingAlbumAsync(1);
        var track = (await Api.GetAlbumAsync(album.Id))!.Tracks.First(t => t.Status == TrackStatus.Missing);
        var item = (await WaitAsync(await Api.StartListeningAsync(new StartListen(RequestKind.Track, track.TrackId)), ListenNowState.Playable))[0];
        var file = Directory.GetFiles(fixture.ListenNow, item.Id.ToString("N") + ".*").Single();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            await db.ListenNowItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await scope.ServiceProvider.GetRequiredService<ListenNowService>().SweepAsync(default);
        }
        Assert.False(File.Exists(file));
        Assert.Equal(ListenNowState.Expired, (await Api.GetListeningAsync(item.Id))!.State);
    }

    [TestLibraryFact]
    public async Task Events_say_when_listen_now_tracks_change()
    {
        await fixture.AddSourceAsync(new FakeSource("events"));
        var album = await MissingAlbumAsync(2);
        var track = (await Api.GetAlbumAsync(album.Id))!.Tracks.First(t => t.Status == TrackStatus.Missing);

        using var events = await fixture.Factory.CreateSignedInClient().GetAsync("api/v1/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());

        var started = await Api.StartListeningAsync(new StartListen(RequestKind.Track, track.TrackId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? line;
        do
            line = await reader.ReadLineAsync(timeout.Token);
        while (line is not null && line != "event: listen");
        Assert.Equal("event: listen", line);
        Assert.StartsWith("data: {", await reader.ReadLineAsync(timeout.Token));

        // Let it finish, so the next test can remove its source.
        await WaitAsync(started, ListenNowState.Playable);
    }

    [TestLibraryFact]
    public async Task An_album_played_from_a_track_fetches_from_that_track_on_first()
    {
        await fixture.AddSourceAsync(new FakeSource("from"));
        var album = await MissingAlbumAsync(3);
        var edition = (await Api.GetAlbumAsync(album.Id))!.ShownEditionId;
        var tracks = (await Api.GetAlbumAsync(album.Id, edition))!.Tracks.Where(t => t.Status == TrackStatus.Missing).Select(t => t.TrackId).ToList();
        Assert.True(tracks.Count >= 3);

        var started = await Api.StartListeningAsync(new StartListen(RequestKind.Album, album.Id, edition, tracks[2]));

        Assert.Equal([.. tracks.Skip(2), .. tracks.Take(2)], started.Select(i => i.TrackId));
        // Let them finish, so the next test can remove its source.
        await WaitAsync(started, ListenNowState.Playable);
    }

    /// <summary>A missing album of a library artist that no other test fetches; <paramref name="index"/> picks another for each test.</summary>
    private async Task<AlbumSummary> MissingAlbumAsync(int index)
    {
        var albums = new List<AlbumSummary>();
        foreach (var artist in await Api.GetLibraryArtistsAsync())
            albums.AddRange((await Api.GetArtistAsync(artist.Id))!.Albums
                .Where(a => a.Status == AlbumStatus.Missing && !a.HiddenByDefault && !a.Ignored && !Taken.Contains(a.Title)));
        return albums.OrderBy(a => a.Title, StringComparer.Ordinal).ElementAt(index);
    }

    /// <summary>Follows tracks, as an app would, until each reaches the state.</summary>
    private async Task<IReadOnlyList<ListenNowInfo>> WaitAsync(IReadOnlyList<ListenNowInfo> items, ListenNowState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var latest = new List<ListenNowInfo>();
            foreach (var item in items)
                latest.Add((await Api.GetListeningAsync(item.Id))!);
            if (latest.All(i => i.State == state))
                return latest;
            if (latest.FirstOrDefault(i => i.State is ListenNowState.Failed or ListenNowState.Expired) is { } stuck || DateTime.UtcNow > deadline)
            {
                foreach (var i in latest)
                    output.WriteLine($"{i.Title}: {i.State} ({i.Detail})");
                throw new InvalidOperationException($"Waiting for {state}, but {latest.First(i => i.State != state).State}");
            }
            await Task.Delay(50);
        }
    }
}

internal static class AlbumSummaryExtensions
{
    /// <summary>The album's artist name as the library lists it, for <see cref="RequestFixture.AlbumAsync"/>.</summary>
    public static string ArtistName(this AlbumSummary album) => album.ArtistCredit;
}
