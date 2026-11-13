using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Tasks;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The visualiser's bars (#89): worked out from each song, kept on demand or beside it.</summary>
public sealed class BarsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MelodexDbContext _db;
    private readonly MelodexOptions _options;

    public BarsTests()
    {
        _options = new MelodexOptions { LibraryPath = Path.Combine(_root, "music"), DataPath = Path.Combine(_root, "data") };
        Directory.CreateDirectory(_options.LibraryPath);
        _connection.Open();
        _db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(_connection, MelodexDbContext.Sqlite).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static float[] Tone(double hz, double seconds) =>
        Enumerable.Range(0, (int)(seconds * BarsFile.Rate)).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / BarsFile.Rate))).ToArray();

    private static byte[] Frames(byte[] file)
    {
        using var gzip = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray()[BarsFile.HeaderSize..];
    }

    [Fact]
    public void A_tone_lights_the_bar_its_pitch_is_in_and_silence_none()
    {
        var analyser = new BarsAnalyser();
        // Fed in pieces, as ffmpeg's output comes, split mid-frame.
        var tone = Tone(1000, 2);
        foreach (var piece in tone.Chunk(1000))
            analyser.Add(piece);
        var frames = analyser.Finish();
        // A frame for each moment, from the start to the end.
        Assert.Equal(0, frames.Length % BarsFile.Count);
        Assert.InRange(frames.Length / BarsFile.Count, 2 * BarsFile.Fps, 2 * BarsFile.Fps + 2);
        // Nothing has sounded at the very start.
        Assert.All(frames[..BarsFile.Count], b => Assert.Equal(0, b));
        // A second in, 1 kHz is loudest, and the bass and highs are quiet.
        var second = frames.AsSpan(BarsFile.Fps * BarsFile.Count, BarsFile.Count).ToArray();
        var loudest = Array.IndexOf(second, second.Max());
        Assert.InRange(loudest, 30, 50);
        Assert.True(second[0] < second[loudest] / 2 && second[^1] < second[loudest] / 2);

        var silence = new BarsAnalyser();
        silence.Add(new float[BarsFile.Rate]);
        Assert.All(silence.Finish(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_bars_file_says_what_it_is_and_other_files_dont_pass_for_one()
    {
        var path = Path.Combine(_root, "a.bars");
        File.WriteAllBytes(path, BarsFile.Write(new byte[BarsFile.Count * 3]));
        Assert.True(BarsFile.IsBars(path));
        Assert.Equal(BarsFile.Count * 3, Frames(File.ReadAllBytes(path)).Length);

        File.WriteAllBytes(path, "not bars"u8.ToArray());
        Assert.False(BarsFile.IsBars(path));
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
            gzip.Write("MDXA\x01\x1e\x40\0"u8);
        Assert.False(BarsFile.IsBars(path));
        Assert.False(BarsFile.IsBars(Path.Combine(_root, "missing.bars")));
    }

    private async Task<string> SongAsync(string name, double seconds = 3)
    {
        var path = Path.Combine(_options.LibraryPath, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", path])
               { RedirectStandardError = true })!)
            await ffmpeg.WaitForExitAsync();
        Assert.True(File.Exists(path));
        _db.LibraryFiles.Add(new LibraryFile { Path = name, Size = new FileInfo(path).Length, Duration = TimeSpan.FromSeconds(seconds) });
        await _db.SaveChangesAsync();
        return path;
    }

    private BarsMaker Maker() => new(Options.Create(_options), NullLogger<BarsMaker>.Instance);

    [FfmpegFact]
    public async Task Bars_made_on_demand_are_kept_and_made_once()
    {
        var song = await SongAsync("tone.flac");
        var maker = Maker();
        var made = await Task.WhenAll(maker.GetAsync(song, null, default), maker.GetAsync(song, null, default));
        Assert.Equal(made[0], made[1]);
        Assert.InRange(Frames(made[0]).Length / BarsFile.Count, 3 * BarsFile.Fps, 3 * BarsFile.Fps + 2);
        // Kept in the data folder, once.
        var kept = Assert.Single(Directory.GetFiles(Path.Combine(_options.DataPath, "bars")));
        Assert.True(BarsFile.IsBars(kept));

        // Bars beside the song are used when there are some.
        var sidecar = BarsFile.SidecarOf(song);
        var own = BarsFile.Write(new byte[BarsFile.Count]);
        File.WriteAllBytes(sidecar, own);
        Assert.Equal(own, await maker.GetAsync(song, sidecar, default));

        // A file that isn't a song can't be.
        var notSong = Path.Combine(_options.LibraryPath, "notes.flac");
        File.WriteAllText(notSong, "hello");
        await Assert.ThrowsAsync<BarsException>(() => maker.GetAsync(notSong, null, default));
        // Nor can any without ffmpeg.
        var without = new BarsMaker(Options.Create(new MelodexOptions { DataPath = _options.DataPath, FfmpegPath = "/nowhere/ffmpeg" }), NullLogger<BarsMaker>.Instance);
        var error = await Assert.ThrowsAsync<BarsException>(() => without.MakeAsync(song, default));
        Assert.Contains("no ffmpeg", error.Message);
    }

    [FfmpegFact]
    public async Task Made_when_matching_every_song_gets_bars_beside_it_until_theyre_deleted()
    {
        var songs = new[] { await SongAsync("A/one.flac"), await SongAsync("A/two.mp3"), await SongAsync("B/three.m4a") };
        var settings = new SettingsService(_db);
        var importer = new FakeImporter(Path.Combine(_root, "staging"), _options.LibraryPath);
        var task = new BarsTask(_db, settings, Maker(), importer, Options.Create(_options), NullLogger<BarsTask>.Instance);
        var progress = new Progress<string>();

        // Only while they're made when matching.
        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind }, progress, default);
        Assert.All(songs, s => Assert.False(File.Exists(BarsFile.SidecarOf(s))));

        await settings.SavePlaybackAsync(new PlaybackSettings { Visualiser = VisualiserBars.WhenMatching }, default);
        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind }, progress, default);
        Assert.All(songs, s => Assert.True(BarsFile.IsBars(BarsFile.SidecarOf(s))));
        var status = await BarsTask.StatusAsync(_db, settings, _options.LibraryPath, default);
        Assert.Equal((3, 3, VisualiserBars.WhenMatching), (status.Songs, status.BarsFiles, status.Settings.Visualiser));
        Assert.Equal(songs.Sum(s => new FileInfo(BarsFile.SidecarOf(s)).Length), status.BarsBytes);
        Assert.Equal(9 * BarsFile.BytesPerSecond, status.EstimatedBytes);

        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind, PayloadJson = BarsTask.Delete }, progress, default);
        Assert.All(songs, s => Assert.False(File.Exists(BarsFile.SidecarOf(s))));
        Assert.Equal(0, (await BarsTask.StatusAsync(_db, settings, _options.LibraryPath, default)).BarsFiles);
    }
}

/// <summary>A test that needs ffmpeg, to make songs and decode them.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        if (!path.Any(p => File.Exists(Path.Combine(p, "ffmpeg"))))
            Skip = "ffmpeg isn't installed.";
    }
}
