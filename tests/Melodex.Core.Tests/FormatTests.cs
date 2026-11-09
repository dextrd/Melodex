using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Formatting files from the catalogue, with the fake beets standing in for the writing.</summary>
[Collection(RequestCollection.Name)]
public sealed class FormatTests(RequestFixture fixture)
{
    private MelodexClient Api => fixture.Api;

    private async Task<AlbumDetail> OwnedAlbumAsync(string title)
    {
        var album = (await Api.GetLibraryAlbumsAsync()).First(a => a.Album.Title == title).Album;
        return (await Api.GetAlbumAsync(album.Id))!;
    }

    private async Task<TaskInfo> WaitAsync(TaskInfo task)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (task.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            task = (await Api.GetTaskAsync(task.Id))!;
        }
        return task;
    }

    [TestLibraryFact]
    public async Task The_plan_counts_the_files_and_lists_the_sources()
    {
        var album = await OwnedAlbumAsync("Discovery");
        var plan = await Api.GetFormatPlanAsync(new FormatScope(FormatScopeKind.Album, album.Album.Id));

        Assert.Equal("Discovery", plan.Title);
        Assert.True(plan.Files > 0);
        Assert.Equal(plan.Files, plan.Formattable);
        Assert.Equal([FormatSources.Catalogue, FormatSources.Keep], plan.Tags.Select(s => s.Id));
        Assert.Equal(FormatSources.Keep, plan.Artwork[0].Id);
        Assert.Contains(plan.Artwork, s => s.Id == FormatSources.Remove);
        Assert.True(plan.WorkerReady);

        var library = await Api.GetFormatPlanAsync(new FormatScope(FormatScopeKind.Library));
        Assert.Equal("the whole library", library.Title);
        Assert.True(library.Files > plan.Files);
        Assert.True(library.Formattable < library.Files, "Unmatched files aren't formatted");
    }

    [TestLibraryFact]
    public async Task The_whole_librarys_plan_lists_the_folders_with_no_music_in_them()
    {
        var empty = Path.Combine(fixture.Music, "Old Rips", "Nothing Here");
        Directory.CreateDirectory(empty);
        try
        {
            var library = await Api.GetFormatPlanAsync(new FormatScope(FormatScopeKind.Library));
            Assert.Equal(["Old Rips"], library.MusiclessFolders);
            Assert.False(fixture.Importer.Sweeps.Last().Remove, "It's only looked at until the format runs");

            var album = await OwnedAlbumAsync("Discovery");
            Assert.Null((await Api.GetFormatPlanAsync(new FormatScope(FormatScopeKind.Album, album.Album.Id))).MusiclessFolders);
        }
        finally
        {
            Directory.Delete(Path.Combine(fixture.Music, "Old Rips"), recursive: true);
        }
    }

    [TestLibraryFact]
    public async Task An_album_is_tagged_from_the_catalogue_and_moved_without_being_matched_again()
    {
        var album = await OwnedAlbumAsync("Random Access Memories");
        var options = new FormatOptions { Numbers = FormatSources.Keep, Move = true };
        var task = await WaitAsync(await Api.StartFormatAsync(new StartFormat(new FormatScope(FormatScopeKind.Album, album.Album.Id), options)));
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.StartsWith("Formatted", task.Progress);

        var sent = fixture.Importer.Formats.Last();
        Assert.Equal("$albumartist/$album%aunique{}/$track $title", sent.PathTemplate);
        var first = sent.Files[0];
        Assert.Equal("Random Access Memories", first.Tags["album"]);
        Assert.Equal("Daft Punk", first.Tags["albumartist"]);
        Assert.True(first.Tags.ContainsKey("mb_trackid"));
        Assert.True(first.Tags.ContainsKey("mb_albumid"));
        Assert.False(first.Tags.ContainsKey("track"), "Numbers were kept as the files have them");
        Assert.Null(first.Artwork);

        // The files are where beets put them, and a scan finds them unchanged rather than new.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var files = await db.LibraryFiles.Where(f => f.Edition!.AlbumId == album.Album.Id).ToListAsync();
            Assert.All(files, f => Assert.StartsWith("Daft Punk/Random Access Memories/", f.Path));
            Assert.All(files, f => Assert.NotNull(f.FormattedAt));
        }
        var scan = await WaitAsync(await Api.ScanLibraryAsync());
        Assert.Contains("0 newly matched", scan.Progress);
        Assert.Equal(album.Album.Status, (await OwnedAlbumAsync("Random Access Memories")).Album.Status);
        Assert.Equal(album.Album.OwnedTracks, (await OwnedAlbumAsync("Random Access Memories")).Album.OwnedTracks);
    }

    [TestLibraryFact]
    public async Task A_resumed_format_skips_the_files_it_already_did()
    {
        var album = await OwnedAlbumAsync("Homework");
        var scope = new FormatScope(FormatScopeKind.Album, album.Album.Id);
        var options = new FormatOptions { Release = FormatSources.Keep };
        List<string> done;
        using (var services = fixture.Factory.Services.CreateScope())
        {
            // Queued paused, so it waits while half the album is marked as done, as a pause part-way through leaves it.
            var db = services.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var task = new BackgroundTask
            {
                Kind = FormatTask.TaskKind, Paused = true,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new FormatTask.Job(scope, options, "Homework"), MelodexJson.Options),
            };
            db.BackgroundTasks.Add(task);
            var files = await db.LibraryFiles.Where(f => f.Edition!.AlbumId == album.Album.Id).OrderBy(f => f.Path).ToListAsync();
            done = files.Take(files.Count / 2).Select(f => f.Path).ToList();
            foreach (var file in files.Take(files.Count / 2))
                file.FormattedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var sentBefore = fixture.Importer.Formats.Count;

            await Api.ResumeTaskAsync(task.Id);
            var finished = await WaitAsync((await Api.GetTaskAsync(task.Id))!);
            Assert.Equal(TaskState.Succeeded, finished.State);
            var sent = fixture.Importer.Formats.Skip(sentBefore).SelectMany(r => r.Files).Select(f => f.Path).ToList();
            Assert.Equal(files.Count - done.Count, sent.Count);
            Assert.Empty(sent.Intersect(done));
        }
    }

    [TestLibraryFact]
    public async Task Unknown_sources_are_refused()
    {
        var album = await OwnedAlbumAsync("Discovery");
        var scope = new FormatScope(FormatScopeKind.Album, album.Album.Id);
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() =>
            Api.StartFormatAsync(new StartFormat(scope, new FormatOptions { Artwork = Guid.NewGuid().ToString(), Titles = "deezer" })));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);

        var missing = await Assert.ThrowsAsync<MelodexApiException>(() =>
            Api.StartFormatAsync(new StartFormat(new FormatScope(FormatScopeKind.Album, Guid.NewGuid()), new FormatOptions())));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }
}
