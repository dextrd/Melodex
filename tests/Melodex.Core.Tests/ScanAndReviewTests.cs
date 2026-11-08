using Melodex.Client;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>What a scan shows as it goes, and files excluded from matching.</summary>
[Collection(RequestCollection.Name)]
public sealed class ScanAndReviewTests(RequestFixture fixture)
{
    private MelodexClient Api => fixture.Api;

    [TestLibraryFact]
    public async Task A_scan_shows_each_file_and_the_song_it_matched()
    {
        // A scan only reads files that changed, so change one that matches and one that doesn't.
        var dummy = Directory.GetFiles(Path.Combine(fixture.Music, "Portishead"), "*.*", SearchOption.AllDirectories).First(f => f.Contains("Dummy"));
        var review = (await Api.GetReviewAsync()).Last();
        // Each file on the list brings its own picture and when it last changed, to show and sort by.
        Assert.Equal($"api/v1/library/files/{review.FileId}/cover", review.CoverUrl);
        Assert.NotNull(review.ModifiedAt);
        foreach (var file in new[] { dummy, Path.Combine(fixture.Music, review.Path) })
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        await ScanAsync();
        var page = await Api.GetScanFilesAsync();
        var files = page.Files;

        var matched = files.First(f => f.Path.StartsWith("Portishead/Dummy", StringComparison.Ordinal) && f.State == ScanFileState.Matched);
        Assert.Equal("Dummy", matched.AlbumTitle);
        Assert.Equal("Portishead", matched.ArtistCredit);
        Assert.NotNull(matched.AlbumId);
        Assert.NotNull(matched.CoverUrl);
        Assert.Contains(files, f => f.Path == review.Path && f.State == ScanFileState.Review && f.Detail is not null);
        Assert.DoesNotContain(files, f => f.State is ScanFileState.Reading or ScanFileState.Matching or ScanFileState.Queued);
        // Each file keeps the place it was first read in, which is path order.
        Assert.Equal(files.Select(f => f.Order).Order(), files.Select(f => f.Order));
        Assert.Equal(files.Select(f => f.Path).Order(StringComparer.Ordinal), files.Select(f => f.Path));
        // Nothing has changed since, so asking for later changes gets none.
        var later = await Api.GetScanFilesAsync(page.Latest);
        Assert.Equal(page.Scan, later.Scan);
        Assert.Empty(later.Files);
    }

    [TestLibraryFact]
    public async Task An_excluded_file_leaves_review_and_isnt_matched_until_its_included()
    {
        var item = (await Api.GetReviewAsync()).First(i => i.Path.StartsWith("Unknown/", StringComparison.Ordinal));
        var before = (await Api.GetLibraryStatusAsync())!;

        await Api.SetFileExcludedAsync(item.FileId, true);
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Contains(await ExcludedAsync(), i => i.FileId == item.FileId && i.Excluded && i.SetAside == SetAside.Excluded);
        var excluded = (await Api.GetLibraryStatusAsync())!;
        Assert.Equal(before.Review - 1, excluded.Review);
        Assert.Equal(before.Excluded + 1, excluded.Excluded);

        // A scan leaves it alone.
        await ScanAsync();
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);

        // Included again, the next scan matches it, and it's back for review.
        await Api.SetFileExcludedAsync(item.FileId, false);
        Assert.Equal(before.Excluded, (await Api.GetLibraryStatusAsync())!.Excluded);
        await ScanAsync();
        Assert.Contains(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
    }

    [TestLibraryFact]
    public async Task Files_can_be_approved_and_excluded_together()
    {
        var review = await Api.GetReviewAsync();
        var suggested = review.First(i => i.Suggestion is not null);
        var others = review.Where(i => i.FileId != suggested.FileId).Take(2).Select(i => i.FileId).ToList();
        var before = (await Api.GetLibraryStatusAsync())!;

        // Only files with a suggestion are approved; the rest stay for review.
        var unsuggested = review.FirstOrDefault(i => i.Suggestion is null);
        var approved = await Api.ApproveFilesAsync([suggested.FileId, .. unsuggested is null ? [] : new[] { unsuggested.FileId }]);
        Assert.Equal(1, approved.Count);
        var after = await Api.GetReviewAsync();
        Assert.DoesNotContain(after, i => i.FileId == suggested.FileId);
        if (unsuggested is not null)
            Assert.Contains(after, i => i.FileId == unsuggested.FileId);
        Assert.Equal(before.Matched + 1, (await Api.GetLibraryStatusAsync())!.Matched);

        var excluded = await Api.ExcludeFilesAsync(others);
        Assert.Equal(others.Count, excluded.Count);
        var nowExcluded = await ExcludedAsync();
        Assert.All(others, id => Assert.Contains(nowExcluded, i => i.FileId == id));

        // Put them all back for the other tests: excluded and included again, the next scan reviews them as before.
        await Api.SetFileExcludedAsync(suggested.FileId, true);
        foreach (var id in others.Append(suggested.FileId))
            await Api.SetFileExcludedAsync(id, false);
        await ScanAsync();
        var restored = await Api.GetReviewAsync();
        Assert.All(others.Append(suggested.FileId), id => Assert.Contains(restored, i => i.FileId == id));
    }

    [TestLibraryFact]
    public async Task A_deferred_file_leaves_review_and_stays_deferred_while_its_not_a_confident_match()
    {
        var item = (await Api.GetReviewAsync()).First();
        var before = (await Api.GetLibraryStatusAsync())!;

        await Api.SetFileAsideAsync(item.FileId, SetAside.Deferred);
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Deferred), i => i.FileId == item.FileId && i.SetAside == SetAside.Deferred);
        var deferred = (await Api.GetLibraryStatusAsync())!;
        Assert.Equal(before.Review - 1, deferred.Review);
        Assert.Equal(before.Deferred + 1, deferred.Deferred);

        // The scan tries it again, but it's no more confident than before, so it stays deferred, off the review list.
        var scan = await ScanAsync();
        Assert.Contains("still not a confident match", scan.Progress);
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Deferred), i => i.FileId == item.FileId);
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);

        await Api.SetFileAsideAsync(item.FileId, SetAside.None);
        await ScanAsync();
        Assert.Contains(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Equal(before.Deferred, (await Api.GetLibraryStatusAsync())!.Deferred);
    }

    [TestLibraryFact]
    public async Task A_hidden_file_is_out_of_the_library_until_its_put_back()
    {
        var item = (await Api.GetReviewAsync()).First();
        var before = (await Api.GetLibraryStatusAsync())!;

        await Api.SetFilesAsideAsync([item.FileId], SetAside.Hidden);
        var hidden = (await Api.GetLibraryStatusAsync())!;
        Assert.Equal(before.Files - 1, hidden.Files);
        Assert.Equal(before.Hidden + 1, hidden.Hidden);
        Assert.Equal(before.Review - 1, hidden.Review);
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Hidden), i => i.FileId == item.FileId && i.SetAside == SetAside.Hidden);

        // Even changed, a scan leaves it alone.
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Music, item.Path), DateTime.UtcNow);
        await ScanAsync();
        Assert.DoesNotContain(await Api.GetReviewAsync(), i => i.FileId == item.FileId);

        await Api.SetFilesAsideAsync([item.FileId], SetAside.None);
        await ScanAsync();
        Assert.Contains(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Equal(before.Files, (await Api.GetLibraryStatusAsync())!.Files);
    }

    [TestLibraryFact]
    public async Task A_file_set_aside_moves_between_the_lists_and_is_back_on_review_at_once_when_put_back()
    {
        var item = (await Api.GetReviewAsync()).First();

        // From one list to another, without going through the review list.
        await Api.SetFileAsideAsync(item.FileId, SetAside.Excluded);
        await Api.SetFileAsideAsync(item.FileId, SetAside.Deferred);
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Deferred), i => i.FileId == item.FileId);
        await Api.SetFileAsideAsync(item.FileId, SetAside.Hidden);
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Hidden), i => i.FileId == item.FileId);
        Assert.DoesNotContain(await Api.GetReviewAsync(ReviewList.Deferred), i => i.FileId == item.FileId);
        await Api.SetFileAsideAsync(item.FileId, SetAside.Excluded);

        // Put back, it's on the review list straight away, not only after a scan, and the next scan matches it again.
        await Api.SetFileAsideAsync(item.FileId, SetAside.None);
        var back = Assert.Single(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.Contains("Put back", back.Reason);
        var scan = await ScanAsync();
        Assert.Contains("1 to review", scan.Progress);
        var matched = Assert.Single(await Api.GetReviewAsync(), i => i.FileId == item.FileId);
        Assert.DoesNotContain("Put back", matched.Reason ?? "");
    }

    [TestLibraryFact]
    public async Task A_scan_can_match_unmatched_files_again()
    {
        var review = (await Api.GetReviewAsync()).Count;
        var scan = await ScanAsync(Rematch.Unmatched);
        Assert.Equal("Library scan · matching unmatched files again", scan.Title);
        Assert.Equal(review, (await Api.GetLibraryStatusAsync())!.Review);
        // Every file under review was matched again, and the feed says so.
        var feed = (await Api.GetScanFilesAsync()).Files;
        Assert.Equal(review, feed.Count(f => f.State == ScanFileState.Review));
    }

    [TestLibraryFact]
    public async Task An_excluded_file_is_in_the_library_as_its_tags_say_with_its_own_cover()
    {
        var path = await CopyAsync("Own Music/Copy", withArtwork: true);
        var file = await FileAsync(path);
        await Api.SetFileAsideAsync(file, SetAside.Excluded);

        // Its tags name Portishead, an artist the library has, so the album is theirs; its ID comes from the names.
        var album = await EventuallyAsync(async () => (await Api.GetLibraryAlbumsAsync()).FirstOrDefault(a => a.Album.Local)?.Album);
        Assert.Equal("Dummy", album.Title);
        Assert.Equal("Portishead", album.ArtistCredit);
        Assert.Equal(AlbumStatus.Complete, album.Status);
        var detail = (await Api.GetLocalAlbumAsync(album.Id))!;
        Assert.False(detail.ArtistLocal);
        Assert.Equal(path, Assert.Single(detail.Tracks).Path);
        Assert.Contains((await Api.GetArtistAsync(detail.ArtistId))!.Albums, a => a.Local && a.Id == album.Id);
        var track = await EventuallyAsync(async () => (await Api.GetLibraryTracksAsync()).FirstOrDefault(t => t.Local));
        Assert.Equal(file, track.FileId);
        Assert.Equal(album.Id, track.AlbumId);

        // The cover is the picture beside it; it plays as the file.
        using var http = fixture.Factory.CreateClient();
        var cover = await http.GetAsync(album.CoverUrl);
        Assert.Equal(System.Net.HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await cover.Content.ReadAsByteArrayAsync());
        var play = (await Api.GetFilePlayInfoAsync(file))!;
        Assert.Equal(file, play.TrackId);
        Assert.Contains($"library/files/{file}/stream", play.StreamUrl);

        // Tidy up: without the file, the next scan forgets it, and the album goes.
        Directory.Delete(Path.Combine(fixture.Music, "Own Music"), recursive: true);
        await ScanAsync();
        Assert.Null(await Api.GetLocalAlbumAsync(album.Id));
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> find) where T : class
    {
        for (var i = 0; i < 50; i++)
        {
            if (await find() is { } found)
                return found;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("It never showed up.");
    }

    // Excluded files with a title and artist, and those without, which are listed apart.
    private async Task<List<ReviewItem>> ExcludedAsync() =>
        [.. await Api.GetReviewAsync(ReviewList.Excluded), .. await Api.GetReviewAsync(ReviewList.Incomplete)];

    [TestLibraryFact]
    public async Task Removing_a_file_deletes_it_and_its_emptied_folder()
    {
        var path = await CopyAsync("Removal Test/Copy", withArtwork: true);
        var file = await FileAsync(path);

        var preview = await Api.GetRemovalAsync(LibraryItemKind.File, file);
        Assert.Equal(1, preview.Files);
        Assert.True(preview.Bytes > 0);
        Assert.Equal(["Removal Test/Copy"], preview.Folders);
        Assert.True(preview.CanDelete);

        var result = await Api.RemoveFromLibraryAsync(new RemoveFromLibrary(LibraryItemKind.File, file, DeleteFiles: true));
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Failed);
        // The folder held only artwork after that, so it went too, and so did its parent.
        Assert.False(Directory.Exists(Path.Combine(fixture.Music, "Removal Test")));
        Assert.Equal(0, (await Api.GetRemovalAsync(LibraryItemKind.File, file)).Files);
    }

    [TestLibraryFact]
    public async Task Removing_a_file_but_keeping_it_hides_it()
    {
        var path = await CopyAsync("Removal Keep/Copy", withArtwork: false);
        var file = await FileAsync(path);

        var result = await Api.RemoveFromLibraryAsync(new RemoveFromLibrary(LibraryItemKind.File, file, DeleteFiles: false));
        Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(Path.Combine(fixture.Music, path)));
        Assert.Contains(await Api.GetReviewAsync(ReviewList.Hidden), i => i.FileId == file);

        // Tidy up: without the file, the next scan forgets it.
        Directory.Delete(Path.Combine(fixture.Music, "Removal Keep"), recursive: true);
        await ScanAsync();
    }

    [TestLibraryFact]
    public async Task Removing_an_album_or_artist_covers_all_its_files()
    {
        var album = await fixture.AlbumAsync("Portishead", "Dummy");
        var albumFiles = Directory.GetFiles(fixture.Music, "*.*", SearchOption.AllDirectories).Count(f => f.Contains("Dummy") && !f.EndsWith(".jpg"));
        Assert.Equal(albumFiles, (await Api.GetRemovalAsync(LibraryItemKind.Album, album.Id)).Files);
        Assert.True((await Api.GetRemovalAsync(LibraryItemKind.Artist, album.ArtistId)).Files > albumFiles);
    }

    // Copies one of Portishead's Dummy files into a new folder and scans it in; returns its path.
    private async Task<string> CopyAsync(string folder, bool withArtwork)
    {
        var source = Directory.GetFiles(Path.Combine(fixture.Music, "Portishead"), "*.*", SearchOption.AllDirectories).First(f => f.Contains("Dummy") && !f.EndsWith(".jpg"));
        var target = Path.Combine(fixture.Music, folder);
        Directory.CreateDirectory(target);
        File.Copy(source, Path.Combine(target, Path.GetFileName(source)));
        if (withArtwork)
            await File.WriteAllBytesAsync(Path.Combine(target, "cover.jpg"), [1, 2, 3]);
        await ScanAsync();
        return $"{folder}/{Path.GetFileName(source)}";
    }

    private async Task<Guid> FileAsync(string path)
    {
        for (var i = 0; i < 50; i++)
        {
            if ((await Api.GetLibraryTracksAsync()).FirstOrDefault(t => t.Path == path) is { } track)
                return track.FileId;
            if ((await Api.GetReviewAsync()).FirstOrDefault(r => r.Path == path) is { } review)
                return review.FileId;
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"{path} isn't in the library.");
    }

    private async Task<TaskInfo> ScanAsync(Rematch rematch = Rematch.None)
    {
        var task = await Api.ScanLibraryAsync(rematch);
        while (task.Active)
        {
            await Task.Delay(100);
            task = (await Api.GetTaskAsync(task.Id))!;
        }
        Assert.Equal(TaskState.Succeeded, task.State);
        return task;
    }
}
