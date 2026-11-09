using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using System.Runtime.Versioning;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class LibraryScannerTests : IDisposable
{
    private readonly string _music = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MelodexDbContext _db;
    private readonly LibraryScanner _scanner;

    public LibraryScannerTests()
    {
        Directory.CreateDirectory(_music);
        _connection.Open();
        _db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(_connection, MelodexDbContext.Sqlite).Options);
        _db.Database.Migrate();
        _scanner = new LibraryScanner(_db, Options.Create(new MelodexOptions { LibraryPath = _music }), new ScanFeed(), NullLogger<LibraryScanner>.Instance);
    }

    public void Dispose()
    {
        // Unlock the root before listing it, then everything under it, so the folder can be deleted.
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(_music, owner);
        foreach (var dir in Directory.EnumerateDirectories(_music, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            File.SetUnixFileMode(dir, owner);
        Directory.Delete(_music, recursive: true);
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_empty_music_folder_fails_and_says_to_check_the_path()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("is empty", error.Message);
        Assert.Contains("MUSIC_PATH", error.Message);
    }

    [Fact]
    public async Task A_folder_with_no_audio_fails_and_says_so()
    {
        File.WriteAllText(Path.Combine(_music, "notes.txt"), "");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("no audio files", error.Message);
    }

    [NotRootFact]
    public async Task An_unreadable_music_folder_fails_and_says_to_fix_permissions()
    {
        Directory.CreateDirectory(Path.Combine(_music, "Artist"));
        File.SetUnixFileMode(_music, UnixFileMode.UserExecute);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Contains("can't read", error.Message);
    }

    [NotRootFact]
    public async Task An_unreadable_file_is_skipped_and_counted_rather_than_failing_the_scan()
    {
        var locked = Path.Combine(_music, "Locked Song.flac");
        File.WriteAllText(locked, "");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var result = await _scanner.ScanAsync(null, default);

        Assert.Equal(1, result.Unreadable);
        Assert.Equal(0, await _db.LibraryFiles.CountAsync());
    }

    [Fact]
    public async Task An_empty_scan_never_removes_known_files()
    {
        _db.LibraryFiles.Add(new LibraryFile { Path = "Artist/Album/01 Song.flac", Format = "flac", TagsJson = "{}" });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.ScanAsync(null, default));
        Assert.Equal(1, await _db.LibraryFiles.CountAsync());
    }

    [TestLibraryFact]
    public async Task Files_in_an_unreadable_folder_are_kept_and_the_folder_is_counted()
    {
        if (Environment.UserName == "root")
            return; // root reads everything, so there's nothing to test.
        var song = Directory.EnumerateFiles(TestLibrary.Music, "*.flac", SearchOption.AllDirectories).First();
        Directory.CreateDirectory(Path.Combine(_music, "Readable"));
        File.Copy(song, Path.Combine(_music, "Readable", "01 Song.flac"));
        _db.LibraryFiles.Add(new LibraryFile { Path = "Locked/01 Song.flac", Format = "flac", TagsJson = "{}" });
        await _db.SaveChangesAsync();
        var locked = Directory.CreateDirectory(Path.Combine(_music, "Locked")).FullName;
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var result = await _scanner.ScanAsync(null, default);

        Assert.Equal(1, result.SkippedFolders);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, await _db.LibraryFiles.CountAsync());
    }

    [TestLibraryFact]
    public async Task Each_file_s_quality_is_read_as_it_s_scanned()
    {
        foreach (var (from, to) in new[]
                 {
                     ("daft punk - homework [FLAC]/01. Daftendirekt.flac", "a.flac"),
                     ("Portishead/Roseland NYC Live (1998)/01 Humming.m4a", "b.m4a"),
                     ("Unknown/Track 01.mp3", "c.mp3"),
                 })
            File.Copy(Path.Combine(TestLibrary.Music, from), Path.Combine(_music, to));

        await _scanner.ScanAsync(null, default);

        var files = await _db.LibraryFiles.ToDictionaryAsync(f => f.Path);
        // The test library's files are small: 16 bits at 22.05 kHz, and lossy ones at a few kbps.
        var flac = files["a.flac"].Quality;
        Assert.Equal(("flac", 16, 22050), (flac.Codec, flac.BitDepth, flac.SampleRateHz));
        Assert.Equal("FLAC 16/22.05", flac.Label);
        Assert.Equal(QualityLevel.Lossless, flac.Level);
        foreach (var (path, codec) in new[] { ("b.m4a", "aac"), ("c.mp3", "mp3") })
        {
            var lossy = files[path].Quality;
            Assert.Equal(codec, lossy.Codec);
            Assert.Null(lossy.BitDepth);
            Assert.Equal(22050, lossy.SampleRateHz);
        }
        // The MP4's too short a file to say its bitrate, or even its length.
        Assert.Equal(QualityLevel.Low, files["c.mp3"].Quality.Level);
    }

    [TestLibraryFact]
    public async Task Files_scanned_before_quality_was_kept_are_read_for_it_once_without_matching_them_again()
    {
        File.Copy(Directory.EnumerateFiles(TestLibrary.Music, "*.flac", SearchOption.AllDirectories).First(), Path.Combine(_music, "a.flac"));
        await _scanner.ScanAsync(null, default);
        var file = await _db.LibraryFiles.SingleAsync();
        // As an upgrade leaves it: matched, with no quality.
        var matched = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        file.MatchedAt = matched;
        file.Codec = null;
        file.BitDepth = file.SampleRate = file.BitrateKbps = null;
        await _db.SaveChangesAsync();

        var result = await _scanner.ScanAsync(null, default);

        Assert.Equal(0, result.Changed);
        Assert.Equal("flac", file.Codec);
        Assert.Equal(16, file.BitDepth);
        Assert.Equal(matched, file.MatchedAt);
    }

    [Fact]
    public void A_file_that_can_t_be_read_for_its_quality_is_marked_so_it_isn_t_read_every_scan()
    {
        var file = new LibraryFile { Path = "a.flac" };
        file.SetQuality(new FileQuality(null, null, null, null));
        Assert.Equal(LibraryFile.UnknownCodec, file.Codec);
        Assert.Equal(QualityLevel.Unknown, file.Quality.Level);
        Assert.Null(file.Quality.Codec);
    }
}

/// <summary>A permissions test, skipped as root, which can read anything.</summary>
public sealed class NotRootFactAttribute : FactAttribute
{
    public NotRootFactAttribute()
    {
        if (Environment.UserName == "root")
            Skip = "Running as root, which ignores file permissions.";
    }
}
