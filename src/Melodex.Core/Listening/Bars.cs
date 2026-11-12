using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Listening;

/// <summary>
/// The visualiser's bars for a song (#89): how loud each of <see cref="Count"/> bands of pitch is, <see cref="Fps"/>
/// times a second, worked out once from the song so the browser needn't download and decode it a second time.
/// A .bars file is gzipped: an 8-byte header (<c>MDXB</c>, the version, the frames a second, the bars a frame and a
/// spare byte), then each frame's bars, a byte each from quietest to loudest, bass first.
/// </summary>
public static class BarsFile
{
    public const int Rate = 22050, Size = 2048, Fps = 30, Count = 64, HeaderSize = 8;
    public const byte Version = 1;
    public const string Extension = ".bars";
    public static ReadOnlySpan<byte> Magic => "MDXB"u8;

    /// <summary>About how many bytes bars take for each second of a song, gzipped.</summary>
    public const int BytesPerSecond = 1400;

    /// <summary>Where a song's .bars file goes: beside it, with the same name.</summary>
    public static string SidecarOf(string path) => Path.ChangeExtension(path, Extension);

    /// <summary>A .bars file of the frames, gzipped.</summary>
    public static byte[] Write(byte[] frames)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            Magic.CopyTo(header);
            header[4] = Version;
            header[5] = Fps;
            header[6] = Count;
            gzip.Write(header);
            gzip.Write(frames);
        }
        return output.ToArray();
    }

    /// <summary>Whether the file is bars this version of Melodex can read; false if it's anything else, or can't be read.</summary>
    public static bool IsBars(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return IsBars(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsBars(Stream stream)
    {
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            Span<byte> header = stackalloc byte[HeaderSize];
            gzip.ReadExactly(header);
            return header[..4].SequenceEqual(Magic) && header[4] == Version && header[5] == Fps && header[6] == Count;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Works out bars from a song's samples, one channel at <see cref="BarsFile.Rate"/>, fed in as they're decoded. Each
/// frame is the spectrum of the <see cref="BarsFile.Size"/> samples up to its moment, smoothed over time as a Web Audio
/// analyser does, in decibels from -100 to -30 over 0 to 255; each bar is the loudest of the frequencies it covers, on a
/// logarithmic scale, as we hear pitch, from about 40 Hz to 10 kHz.
/// </summary>
public sealed class BarsAnalyser
{
    private const int Hop = BarsFile.Rate / BarsFile.Fps, Size = BarsFile.Size, Half = Size / 2;
    private const double MinDb = -100, MaxDb = -30;

    // An analyser smooths by 0.8 for each frame drawn at 60 a second; two of those for each of ours.
    private const double Smoothing = 0.64;

    private static readonly double[] Window = new double[Size];
    private static readonly double[] Cos = new double[Half], Sin = new double[Half];
    private static readonly int[] Reversed = new int[Size];
    private static readonly (int From, int To)[] Bands = new (int, int)[BarsFile.Count];

    static BarsAnalyser()
    {
        for (var i = 0; i < Size; i++)
            Window[i] = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / Size) + 0.08 * Math.Cos(4 * Math.PI * i / Size);
        for (var k = 0; k < Half; k++)
        {
            Cos[k] = Math.Cos(-2 * Math.PI * k / Size);
            Sin[k] = Math.Sin(-2 * Math.PI * k / Size);
        }
        var bits = (int)Math.Log2(Size);
        for (var i = 0; i < Size; i++)
        {
            int r = 0, x = i;
            for (var b = 0; b < bits; b++, x >>= 1)
                r = (r << 1) | (x & 1);
            Reversed[i] = r;
        }
        const double hz = (double)BarsFile.Rate / Size;
        int low = (int)(40 / hz), high = (int)(10000 / hz);
        for (var i = 0; i < BarsFile.Count; i++)
        {
            var from = (int)(low * Math.Pow((double)high / low, (double)i / BarsFile.Count));
            var to = Math.Max(from + 1, (int)(low * Math.Pow((double)high / low, (double)(i + 1) / BarsFile.Count)));
            Bands[i] = (from, to);
        }
    }

    // The samples so far, from the start of the song: frames look back a window's length from their moment.
    private readonly List<float> _samples = [];
    private readonly MemoryStream _frames = new();
    private readonly double[] _smoothed = new double[Half], _re = new double[Size], _im = new double[Size];
    // How many samples have been forgotten from the start, and the next frame to work out.
    private int _offset, _next;

    /// <summary>Takes the next samples, and works out every frame they complete.</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
            _samples.Add(s);
        while (_next * Hop <= _offset + _samples.Count)
            Frame(_next++ * Hop);
        // Forget what no frame looks back to.
        var drop = _next * Hop - Size - _offset;
        if (drop > 1 << 16)
        {
            _samples.RemoveRange(0, drop);
            _offset += drop;
        }
    }

    /// <summary>Every frame's bars, the last one at the song's end.</summary>
    public byte[] Finish()
    {
        if ((_next - 1) * Hop < _offset + _samples.Count)
            Frame(_next++ * Hop);
        return _frames.ToArray();
    }

    private void Frame(int end)
    {
        var start = end - Size - _offset;
        for (var i = 0; i < Size; i++)
        {
            var j = start + i;
            var r = Reversed[i];
            _re[r] = j >= 0 && j < _samples.Count ? _samples[j] * Window[i] : 0;
            _im[r] = 0;
        }
        for (var length = 2; length <= Size; length <<= 1)
        {
            var half = length / 2;
            var step = Size / length;
            for (var i = 0; i < Size; i += length)
                for (var k = 0; k < half; k++)
                {
                    double wr = Cos[k * step], wi = Sin[k * step];
                    int a = i + k, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi, ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
        }
        for (var k = 0; k < Half; k++)
            _smoothed[k] = Smoothing * _smoothed[k] + (1 - Smoothing) * Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / Size;
        foreach (var (from, to) in Bands)
        {
            var peak = 0.0;
            for (var k = from; k < to; k++)
                peak = Math.Max(peak, _smoothed[k]);
            var db = 20 * Math.Log10(Math.Max(peak, 1e-12));
            _frames.WriteByte((byte)Math.Clamp(255 * (db - MinDb) / (MaxDb - MinDb), 0, 255));
        }
    }
}

/// <summary>The visualiser's bars couldn't be made: ffmpeg is missing, or the song can't be decoded.</summary>
public sealed class BarsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Makes songs' bars with ffmpeg, a couple at a time, and keeps those made on demand in the data folder, the least
/// lately played forgotten first once they take more than <see cref="CacheLimit"/>.
/// </summary>
public sealed class BarsMaker(IOptions<MelodexOptions> options, ILogger<BarsMaker> logger)
{
    /// <summary>The most room bars made on demand take: a thousand songs or so.</summary>
    public const long CacheLimit = 256L * 1024 * 1024;

    private readonly SemaphoreSlim _running = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _making = new();
    private readonly SemaphoreSlim _trimming = new(1);

    private string CacheFolder => Path.Combine(options.Value.DataPath, "bars");

    /// <summary>
    /// The song's bars, as a .bars file: from <paramref name="sidecar"/> when that's bars, else from the cache, else
    /// made now and cached.
    /// </summary>
    /// <exception cref="BarsException">They couldn't be made.</exception>
    public async Task<byte[]> GetAsync(string path, string? sidecar, CancellationToken ct)
    {
        if (sidecar is not null && File.Exists(sidecar) && BarsFile.IsBars(sidecar))
        {
            try
            {
                return await File.ReadAllBytesAsync(sidecar, ct);
            }
            catch (IOException)
            {
                // Being written, perhaps: made afresh instead.
            }
        }
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new BarsException("The song isn't there.");
        var cached = Path.Combine(CacheFolder, Key(info) + BarsFile.Extension);
        if (File.Exists(cached))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(cached, ct);
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return bytes;
            }
            catch (IOException)
            {
                // Made again below.
            }
        }
        return await _making.GetOrAdd(cached, _ => new Lazy<Task<byte[]>>(() => MakeAndCacheAsync(path, cached))).Value.WaitAsync(ct);
    }

    // Not cancelled by whoever asked first: another may be waiting for it too, and it's kept for next time.
    private async Task<byte[]> MakeAndCacheAsync(string path, string cached)
    {
        try
        {
            var bars = await MakeAsync(path, CancellationToken.None);
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var part = cached + ".part";
                await File.WriteAllBytesAsync(part, bars);
                File.Move(part, cached, true);
                _ = TrimAsync();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't keep the visualiser's bars for {Path}: {Reason}", path, e.Message);
            }
            return bars;
        }
        finally
        {
            _making.TryRemove(cached, out _);
        }
    }

    /// <summary>Makes the song's bars, as a .bars file.</summary>
    /// <exception cref="BarsException">ffmpeg is missing, or couldn't decode it.</exception>
    public async Task<byte[]> MakeAsync(string path, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            var start = new ProcessStartInfo(options.Value.FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-threads", "1", "-i", path, "-vn", "-sn", "-dn", "-ac", "1", "-ar", BarsFile.Rate.ToString(), "-f", "f32le", "-" })
                start.ArgumentList.Add(arg);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new BarsException("ffmpeg didn't start.");
            }
            catch (Win32Exception e)
            {
                throw new BarsException($"There's no ffmpeg at {options.Value.FfmpegPath}.", e);
            }
            using (process)
            {
                var errors = process.StandardError.ReadToEndAsync(ct);
                var analyser = new BarsAnalyser();
                try
                {
                    var stream = process.StandardOutput.BaseStream;
                    var buffer = new byte[64 * 1024];
                    var held = 0;
                    var samples = new float[buffer.Length / 4];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(held), ct)) > 0)
                    {
                        held += read;
                        var whole = held / 4;
                        for (var i = 0; i < whole; i++)
                            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * 4));
                        analyser.Add(samples.AsSpan(0, whole));
                        // A sample split between reads waits for the rest.
                        var left = held - whole * 4;
                        buffer.AsSpan(whole * 4, left).CopyTo(buffer);
                        held = left;
                    }
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }
                if (process.ExitCode != 0)
                    throw new BarsException($"ffmpeg couldn't decode it: {(await errors).Trim()}");
                var frames = analyser.Finish();
                logger.LogDebug("Made the visualiser's bars for {Path} in {Elapsed} ms", path, watch.ElapsedMilliseconds);
                return BarsFile.Write(frames);
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Forgets the least lately played bars once they take more than the limit.</summary>
    private async Task TrimAsync()
    {
        if (!await _trimming.WaitAsync(0))
            return;
        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(CacheFolder).GetFiles("*" + BarsFile.Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (total > CacheLimit)
                        file.Delete();
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't tidy the visualiser's bars: {Reason}", e.Message);
        }
        finally
        {
            _trimming.Release();
        }
    }

    // A changed file is a new key, and its old bars are forgotten in time.
    private static string Key(FileInfo file) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..32];
}
