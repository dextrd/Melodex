using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Playlists;

/// <summary>Why a playlist change can't be made, by the field that's wrong.</summary>
public sealed class PlaylistRefusedException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Each user's playlists: of songs in the library, files of their own, and songs only the media server has. Every change
/// marks the playlist changed, and the syncer sends it to the media server when playlists are synced.
/// </summary>
public sealed class PlaylistService(MelodexDbContext db, SettingsService settings, LocalMusic local, PlaylistSyncer syncer, PlaylistCovers covers)
{
    public const int MaxName = 200;

    public async Task<IReadOnlyList<PlaylistSummary>> ListAsync(Guid userId, CancellationToken ct)
    {
        var playlists = await db.Playlists.AsNoTracking().Include(p => p.Entries)
            .Where(p => p.UserId == userId && p.DeletedAt == null).ToListAsync(ct);
        var syncing = await SyncingAsync(userId, ct);
        var albumCovers = await CoversAsync(playlists.SelectMany(p => p.Entries.OrderBy(e => e.Position)).ToList(), ct);
        return playlists
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => Summary(p, syncing, p.Entries.OrderBy(e => e.Position).Select(e => albumCovers.GetValueOrDefault(e.Id)).OfType<string>().Distinct().Take(4).ToList()))
            .ToList();
    }

    public async Task<PlaylistDetail?> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        var songs = await SongsAsync(playlist.Entries.OrderBy(e => e.Position).ToList(), ct);
        return new PlaylistDetail(
            Summary(playlist, await SyncingAsync(userId, ct), songs.Select(s => s.CoverUrl).OfType<string>().Distinct().Take(4).ToList()),
            songs);
    }

    public async Task<PlaylistDetail> CreateAsync(Guid userId, CreatePlaylist body, CancellationToken ct)
    {
        var playlist = new Playlist { UserId = userId, Name = CheckName(body.Name), Comment = Comment(body.Comment) };
        db.Playlists.Add(playlist);
        if (body.Songs is { Count: > 0 } songs)
            await AddEntriesAsync(playlist, songs, duplicates: true, ct);
        await db.SaveChangesAsync(ct);
        syncer.Changed(userId);
        return (await GetAsync(userId, playlist.Id, ct))!;
    }

    public async Task<PlaylistSummary?> UpdateAsync(Guid userId, Guid id, UpdatePlaylist body, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        if (body.Name is not null)
            playlist.Name = CheckName(body.Name);
        if (body.Comment is not null)
            playlist.Comment = Comment(body.Comment);
        await ChangedAsync(playlist, ct);
        return (await GetAsync(userId, id, ct))!.Playlist;
    }

    public async Task<PlaylistAdded?> AddAsync(Guid userId, Guid id, AddToPlaylist body, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        var added = await AddEntriesAsync(playlist, body.Songs, body.Duplicates, ct);
        if (added.Added > 0)
            await ChangedAsync(playlist, ct);
        return added;
    }

    /// <summary>Keeps the entries given, in that order, and removes the rest.</summary>
    public async Task<PlaylistDetail?> ArrangeAsync(Guid userId, Guid id, ArrangePlaylist body, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        var byId = playlist.Entries.ToDictionary(e => e.Id);
        if (body.EntryIds.FirstOrDefault(e => !byId.ContainsKey(e)) is var unknown && unknown != Guid.Empty)
            throw new PlaylistRefusedException("entryIds", $"{unknown} isn't in this playlist.");
        if (body.EntryIds.Distinct().Count() != body.EntryIds.Count)
            throw new PlaylistRefusedException("entryIds", "Each song can only be listed once.");
        var keep = body.EntryIds.ToHashSet();
        db.PlaylistEntries.RemoveRange(playlist.Entries.Where(e => !keep.Contains(e.Id)));
        for (var i = 0; i < body.EntryIds.Count; i++)
            byId[body.EntryIds[i]].Position = i;
        await ChangedAsync(playlist, ct);
        return await GetAsync(userId, id, ct);
    }

    /// <summary>Pins it to show first, or unpins it. It's only for its owner: the media server doesn't hear of it.</summary>
    public async Task<PlaylistSummary?> PinAsync(Guid userId, Guid id, bool pinned, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        playlist.PinnedAt = pinned ? playlist.PinnedAt ?? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return (await GetAsync(userId, id, ct))!.Playlist;
    }

    /// <summary>The playlist's own picture, if it has one.</summary>
    public async Task<(string Path, string ContentType)?> CoverAsync(Guid userId, Guid id, CancellationToken ct) =>
        await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.DeletedAt == null, ct) is { } playlist
        && covers.PathOf(playlist) is { } path && File.Exists(path)
            ? (path, playlist.CoverType ?? "image/jpeg")
            : null;

    /// <summary>Gives the playlist a picture of its own (base64, up to 10 MB), or with null, takes it away.</summary>
    public async Task<PlaylistSummary?> SetCoverAsync(Guid userId, Guid id, string? upload, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return null;
        if (upload is null)
            covers.Remove(playlist);
        else
        {
            var (data, type) = Picture(upload);
            await covers.SetAsync(playlist, data, type, ct);
        }
        playlist.CoverChangedAt = DateTime.UtcNow;
        playlist.CoverNeedsPush = true;
        await db.SaveChangesAsync(ct);
        syncer.Changed(userId);
        return (await GetAsync(userId, id, ct))!.Playlist;
    }

    /// <summary>Deletes a playlist. One on the media server is kept, marked deleted, until a sync deletes it there too.</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (await FindAsync(userId, id, ct) is not { } playlist)
            return false;
        if (playlist.ServerId is null)
        {
            covers.Remove(playlist);
            db.Playlists.Remove(playlist);
        }
        else
            playlist.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        syncer.Changed(userId);
        return true;
    }

    /// <summary>Renames a folder: every playlist in it, or in folders in it, has its name changed to match.</summary>
    public async Task<int> RenameFolderAsync(Guid userId, RenamePlaylistFolder body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body.Delimiter))
            throw new PlaylistRefusedException("delimiter", "Say what separates folders in names.");
        var from = body.From.Trim();
        var to = body.To.Trim().TrimEnd(body.Delimiter.ToCharArray());
        if (from.Length == 0)
            throw new PlaylistRefusedException("from", "Say which folder to rename.");
        var prefix = from + body.Delimiter;
        var playlists = await db.Playlists.Where(p => p.UserId == userId && p.DeletedAt == null).ToListAsync(ct);
        var moved = 0;
        foreach (var playlist in playlists.Where(p => p.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = playlist.Name[prefix.Length..];
            playlist.Name = CheckName(to.Length == 0 ? rest : to + body.Delimiter + rest);
            playlist.UpdatedAt = DateTime.UtcNow;
            moved++;
        }
        // Folders' pictures go with them; taking everything out of one leaves it nothing to be the picture of.
        foreach (var folder in await db.PlaylistFolders.Where(f => f.UserId == userId).ToListAsync(ct))
        {
            if (folder.Path != from && !folder.Path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (to.Length == 0 && folder.Path == from)
            {
                covers.Remove(folder);
                db.PlaylistFolders.Remove(folder);
            }
            else
                folder.Path = folder.Path == from ? to : to.Length == 0 ? folder.Path[prefix.Length..] : to + body.Delimiter + folder.Path[prefix.Length..];
        }
        await db.SaveChangesAsync(ct);
        if (moved > 0)
            syncer.Changed(userId);
        return moved;
    }

    // An uploaded picture, checked.
    private static (byte[] Data, string Type) Picture(string upload)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(upload);
        }
        catch (FormatException)
        {
            throw new PlaylistRefusedException("upload", "The picture must be base64.");
        }
        if (data.Length > CoverChooser.MaxUpload)
            throw new PlaylistRefusedException("upload", "The picture is bigger than 10 MB.");
        return (data, CoverChooser.ImageType(data) ?? throw new PlaylistRefusedException("upload", "That isn't a JPEG, PNG, WebP or GIF picture."));
    }

    // ---- Folders ----

    /// <summary>The user's folders that have pictures of their own.</summary>
    public async Task<IReadOnlyList<PlaylistFolderInfo>> FoldersAsync(Guid userId, CancellationToken ct) =>
        (await db.PlaylistFolders.AsNoTracking().Where(f => f.UserId == userId && f.CoverFile != null).OrderBy(f => f.Path).ToListAsync(ct))
        .Select(Info).ToList();

    private static PlaylistFolderInfo Info(PlaylistFolder folder) =>
        new(folder.Id, folder.Path, folder.CoverHash is { } hash ? $"api/v1/playlists/folders/{folder.Id}/cover?v={hash[..12]}" : null);

    public async Task<(string Path, string ContentType)?> FolderCoverAsync(Guid userId, Guid id, CancellationToken ct) =>
        await db.PlaylistFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct) is { } folder
        && covers.PathOf(folder) is { } path && File.Exists(path)
            ? (path, folder.CoverType ?? "image/jpeg")
            : null;

    /// <summary>
    /// Gives a folder a picture of its own (base64, up to 10 MB), or with null, takes it away so the folder icon stands
    /// for it again. Folders are the user's alone: the media server has none.
    /// </summary>
    public async Task<PlaylistFolderInfo> SetFolderCoverAsync(Guid userId, string path, string? upload, CancellationToken ct)
    {
        path = path.Trim();
        if (path.Length == 0)
            throw new PlaylistRefusedException("path", "Say which folder.");
        var folder = await db.PlaylistFolders.FirstOrDefaultAsync(f => f.UserId == userId && f.Path == path, ct);
        if (upload is null)
        {
            if (folder is null)
                return new PlaylistFolderInfo(Guid.Empty, path, null);
            covers.Remove(folder);
            db.PlaylistFolders.Remove(folder);
            await db.SaveChangesAsync(ct);
            return new PlaylistFolderInfo(folder.Id, path, null);
        }
        var (data, type) = Picture(upload);
        if (folder is null)
            db.PlaylistFolders.Add(folder = new PlaylistFolder { UserId = userId, Path = path });
        await covers.SetAsync(folder, data, type, ct);
        await db.SaveChangesAsync(ct);
        return Info(folder);
    }

    private Task<Playlist?> FindAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.DeletedAt == null, ct);

    private async Task ChangedAsync(Playlist playlist, CancellationToken ct)
    {
        playlist.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        syncer.Changed(playlist.UserId);
    }

    private static string CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw new PlaylistRefusedException("name", "Give the playlist a name.");
        if (trimmed.Length > MaxName)
            throw new PlaylistRefusedException("name", $"Keep the name to {MaxName} characters.");
        return trimmed;
    }

    private static string? Comment(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    // Whether the user's playlists are synced, for each playlist's state.
    private async Task<bool> SyncingAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AnyAsync(u => u.Id == userId && u.MediaServerUsername != null, ct);

    // Its own picture stands for it when it has one; otherwise its albums' covers do.
    private static PlaylistSummary Summary(Playlist p, bool syncing, IReadOnlyList<string> albumCovers) => new(
        p.Id, p.Name, p.Comment, p.Entries.Count, p.Entries.Sum(e => e.DurationSeconds ?? 0),
        p.CoverHash is { } hash ? [$"api/v1/playlists/{p.Id}/cover?v={hash[..12]}"] : albumCovers, p.UpdatedAt,
        !syncing ? PlaylistSyncState.Off
        : p.SyncedAt is { } synced && synced >= p.UpdatedAt && !p.NeedsPush && !p.CoverNeedsPush && p.ServerId is not null ? PlaylistSyncState.Synced
        : PlaylistSyncState.Waiting,
        p.PinnedAt is not null, p.CoverHash is not null);

    // ---- Adding ----

    private async Task<PlaylistAdded> AddEntriesAsync(Playlist playlist, IReadOnlyList<PlaylistItem> items, bool duplicates, CancellationToken ct)
    {
        var fileIds = items.Select(i => i.FileId).OfType<Guid>().ToList();
        var trackIds = items.Select(i => i.TrackId).OfType<Guid>().ToList();
        var files = await db.LibraryFiles.AsNoTracking()
            .Where(f => (fileIds.Contains(f.Id) || f.TrackId != null && trackIds.Contains(f.TrackId.Value)) && f.HiddenAt == null)
            .Select(f => new { f.Id, f.TrackId, f.Duration, f.TagsJson, f.Path, Album = f.Edition == null ? null : f.Edition.Album.Title })
            .ToListAsync(ct);
        var allTrackIds = trackIds.Concat(files.Select(f => f.TrackId).OfType<Guid>()).Distinct().ToList();
        var tracks = await db.Tracks.AsNoTracking().Where(t => allTrackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.Duration }).ToDictionaryAsync(t => t.Id, ct);

        var position = playlist.Entries.Count == 0 ? 0 : playlist.Entries.Max(e => e.Position) + 1;
        int added = 0, already = 0, missing = 0;
        foreach (var item in items)
        {
            var file = item.FileId is { } fileId ? files.FirstOrDefault(f => f.Id == fileId) : files.FirstOrDefault(f => f.TrackId == item.TrackId);
            var trackId = file?.TrackId ?? (item.FileId is null ? item.TrackId : null);
            if (file is null || trackId is { } t && !tracks.ContainsKey(t))
            {
                missing++;
                continue;
            }
            if (!duplicates && playlist.Entries.Any(e => trackId is not null ? e.TrackId == trackId : e.FileId == file.Id))
            {
                already++;
                continue;
            }
            var track = trackId is { } id ? tracks[id] : null;
            var tags = track is null ? FileTags.Parse(file.TagsJson) : null;
            // Added to the context too: with its ID already set, one found only through the list would be taken for one already saved.
            var entry = new PlaylistEntry
            {
                Position = position++,
                TrackId = trackId,
                // A track keeps the copy it was added as only for files of your own; others play whichever copy is best.
                FileId = item.FileId is not null || trackId is null ? file.Id : null,
                Title = track?.Title ?? tags?.Title ?? Path.GetFileNameWithoutExtension(file.Path),
                Artist = track?.ArtistCredit ?? tags?.Artist ?? tags?.AlbumArtist ?? "",
                Album = file.Album ?? tags?.Album,
                DurationSeconds = (file.Duration ?? track?.Duration)?.TotalSeconds,
            };
            playlist.Entries.Add(entry);
            db.PlaylistEntries.Add(entry);
            added++;
        }
        return new PlaylistAdded(added, already, missing);
    }

    // ---- Showing ----

    /// <summary>The songs as they are now: what the library has for each, or what was kept of those it no longer has.</summary>
    private async Task<IReadOnlyList<PlaylistSong>> SongsAsync(IReadOnlyList<PlaylistEntry> entries, CancellationToken ct)
    {
        var found = await FindFilesAsync(entries, ct);
        var preferFileCovers = (await settings.GetLibraryAsync(ct)).PreferFileCovers;
        var trackIds = entries.Select(e => e.TrackId).OfType<Guid>().Distinct().ToList();
        var tracks = await db.Tracks.AsNoTracking().Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.ArtistId }).ToDictionaryAsync(t => t.Id, ct);
        Dictionary<Guid, (LocalMusic.Album Album, LocalMusic.Song Song)>? own = null;
        if (entries.Any(e => e.TrackId is null && e.FileId is not null))
            own = (await local.LoadAsync(ct)).Albums.SelectMany(a => a.Songs.Select(s => (a, s))).ToDictionary(x => x.s.File.Id);

        var songs = new List<PlaylistSong>();
        foreach (var e in entries)
        {
            var file = found.GetValueOrDefault(e.Id);
            if (e.TrackId is { } trackId && file is not null && tracks.TryGetValue(trackId, out var track))
            {
                songs.Add(new PlaylistSong(e.Id, trackId, null, track.Title, track.ArtistCredit, track.ArtistId, file.AlbumId, file.AlbumTitle,
                    preferFileCovers ? LocalMusic.CoverUrl(file.Id) : file.AlbumId is { } album ? LibraryView.CoverUrl(album, file.EditionId) : null,
                    e.DurationSeconds ?? file.Duration?.TotalSeconds, PlaylistSongState.Library));
            }
            else if (e.TrackId is null && e.FileId is { } fileId && own?.TryGetValue(fileId, out var mine) == true)
            {
                songs.Add(new PlaylistSong(e.Id, null, fileId, mine.Song.Title, mine.Song.Artist, mine.Album.ArtistLocal ? null : mine.Album.ArtistId,
                    mine.Album.Id, mine.Album.Title, LocalMusic.CoverUrl(fileId), e.DurationSeconds, PlaylistSongState.Local));
            }
            else
            {
                songs.Add(new PlaylistSong(e.Id, e.TrackId, e.FileId, e.Title, e.Artist, null, null, e.Album, null, e.DurationSeconds,
                    e.TrackId is null && e.FileId is null ? PlaylistSongState.ServerOnly : PlaylistSongState.Gone));
            }
        }
        return songs;
    }

    private sealed record FoundFile(Guid Id, Guid? TrackId, Guid? EditionId, Guid? AlbumId, string? AlbumTitle, TimeSpan? Duration);

    /// <summary>The file each entry plays: the one it was added as, or else the best of its track's.</summary>
    private async Task<Dictionary<Guid, FoundFile>> FindFilesAsync(IReadOnlyList<PlaylistEntry> entries, CancellationToken ct)
    {
        var fileIds = entries.Select(e => e.FileId).OfType<Guid>().ToList();
        var trackIds = entries.Select(e => e.TrackId).OfType<Guid>().Distinct().ToList();
        var files = await db.LibraryFiles.AsNoTracking()
            .Where(f => f.HiddenAt == null && (fileIds.Contains(f.Id) || f.TrackId != null && trackIds.Contains(f.TrackId.Value)))
            .Select(f => new FoundFile(f.Id, f.TrackId, f.EditionId, f.Edition == null ? null : f.Edition.AlbumId, f.Edition == null ? null : f.Edition.Album.Title, f.Duration))
            .ToListAsync(ct);
        var byId = files.ToDictionary(f => f.Id);
        var byTrack = files.Where(f => f.TrackId is not null).GroupBy(f => f.TrackId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Id).First());
        var found = new Dictionary<Guid, FoundFile>();
        foreach (var e in entries)
        {
            if (e.FileId is { } id && byId.TryGetValue(id, out var file) && (e.TrackId is null || file.TrackId == e.TrackId))
                found[e.Id] = file;
            else if (e.TrackId is { } track && byTrack.TryGetValue(track, out var best))
                found[e.Id] = best;
        }
        return found;
    }

    // Each entry's cover, for the mosaics on the list.
    private async Task<Dictionary<Guid, string>> CoversAsync(IReadOnlyList<PlaylistEntry> entries, CancellationToken ct)
    {
        var found = await FindFilesAsync(entries, ct);
        var preferFileCovers = (await settings.GetLibraryAsync(ct)).PreferFileCovers;
        var covers = new Dictionary<Guid, string>();
        foreach (var (entryId, file) in found)
        {
            if (preferFileCovers || file.TrackId is null)
                covers[entryId] = LocalMusic.CoverUrl(file.Id);
            else if (file.AlbumId is { } album)
                covers[entryId] = LibraryView.CoverUrl(album, file.EditionId);
        }
        return covers;
    }
}
