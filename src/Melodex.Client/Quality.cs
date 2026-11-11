using System.Globalization;

namespace Melodex.Client;

/// <summary>
/// How good a file sounds, by its encoding, worst first. Lossless is split by whether it's beyond a CD (more than 16
/// bits or 48 kHz). Lossy is by bitrate weighed by how much the codec makes of it: Opus at 160 kbps is about as good as
/// MP3 at 256, so each codec's bitrate is counted as MP3's would be (see <see cref="FileQuality.EquivalentKbps"/>).
/// </summary>
public enum QualityLevel
{
    /// <summary>Nothing's known of the file's encoding.</summary>
    Unknown,

    /// <summary>Lossy, below about MP3 at 160 kbps.</summary>
    Low,

    /// <summary>Lossy, about MP3 at 160 kbps to 220.</summary>
    Fair,

    /// <summary>Lossy, about MP3 V0 (220 kbps or so) to 300.</summary>
    Good,

    /// <summary>Lossy, about MP3 at 320 kbps and above.</summary>
    High,

    /// <summary>Lossless, as a CD is: 16 bits, 44.1 or 48 kHz.</summary>
    Lossless,

    /// <summary>Lossless beyond a CD: more bits or a higher sample rate, or DSD.</summary>
    HiRes,
}

/// <summary>A file's encoding: its codec, bitrate, and for lossless, its bit depth and sample rate.</summary>
/// <param name="Codec">The codec, lower case: flac, alac, mp3, aac, opus, vorbis, wav and so on.</param>
/// <param name="SampleRateHz">Samples a second, such as 44100.</param>
/// <param name="CutoffHz">
/// For a lossless file checked for it, where its highs are cut off sharply, as a lossy encoder's are, if they are (#19).
/// A file cut off low enough is <see cref="Suspect"/>: it seems to be lossy music passed off as lossless.
/// </param>
public sealed record FileQuality(string? Codec, int? BitrateKbps, int? BitDepth, int? SampleRateHz, int? CutoffHz = null)
{
    /// <summary>
    /// Below this, a CD-quality file's highs were cut by a lossy encoder: MP3 at 320 kbps stops at about 20 kHz, while a
    /// CD goes on to 22. Some masters are cut at 20 kHz on purpose, which is why it's suspect, not fake.
    /// </summary>
    public const int LossyCutoffHz = 20700;

    /// <summary>Below this, a file sampled faster than a CD stops where a CD does: it was made from one.</summary>
    public const int CdCutoffHz = 23000;

    private static readonly HashSet<string> LosslessCodecs = ["flac", "alac", "wav", "aiff", "ape", "wavpack", "dsd", "pcm"];

    // How much each lossy codec makes of a bitrate, against MP3's. Rough, as listening tests are; enough to rank them.
    private static readonly Dictionary<string, double> Efficiency = new()
    {
        ["mp3"] = 1.0, ["aac"] = 1.3, ["vorbis"] = 1.4, ["opus"] = 1.6, ["wma"] = 1.0, ["mpc"] = 1.4,
    };

    public bool Lossless => Codec is not null && LosslessCodecs.Contains(Codec);

    /// <summary>A lossy file's bitrate as MP3's that sounds about as good; null for lossless or unknown files.</summary>
    public double? EquivalentKbps => !Lossless && Codec is not null && BitrateKbps is { } kbps ? kbps * Efficiency.GetValueOrDefault(Codec, 1.0) : null;

    /// <summary>Whether it's lossless but its highs stop where a lossy encoder's or, for hi-res, a CD's do.</summary>
    public bool Suspect => Lossless && CutoffHz is { } hz && (hz < LossyCutoffHz || SampleRateHz > 48000 && hz < CdCutoffHz);

    /// <summary>
    /// What a suspect file seems really to be, by where its highs stop: lossless at CD quality, or lossy at the level of
    /// the MP3 that stops there (128 kbps at about 16.5 kHz, 160 at 17.5, 192 at 18.5, 256 at 19.5, 320 at 20).
    /// </summary>
    public QualityLevel? Seems => !Suspect ? null : CutoffHz switch
    {
        >= LossyCutoffHz => QualityLevel.Lossless,
        >= 19900 => QualityLevel.High,
        >= 19000 => QualityLevel.Good,
        >= 17100 => QualityLevel.Fair,
        _ => QualityLevel.Low,
    };

    public QualityLevel Level => Codec is null ? QualityLevel.Unknown
        : Seems is { } seems ? seems
        : Lossless ? Codec == "dsd" || BitDepth > 16 || SampleRateHz > 48000 ? QualityLevel.HiRes : QualityLevel.Lossless
        : EquivalentKbps switch
        {
            null => QualityLevel.Unknown,
            >= 300 => QualityLevel.High,
            >= 220 => QualityLevel.Good,
            >= 150 => QualityLevel.Fair,
            _ => QualityLevel.Low,
        };

    /// <summary>As it's usually written: "FLAC 16/44.1", "FLAC 24/96", "MP3 320", "Opus 160".</summary>
    public string Label
    {
        get
        {
            var name = Name(Codec);
            if (Lossless)
                return BitDepth is { } bits && SampleRateHz is { } rate ? $"{name} {bits}/{Kilohertz(rate)}" : name;
            return BitrateKbps is { } kbps ? $"{name} {kbps}" : name;
        }
    }

    /// <summary>What the label doesn't say: how good it is, and a lossy file's sample rate. "Good · 48 kHz".</summary>
    public string Details => Describe(Level) + (SampleRateHz is { } rate && !Lossless ? $" · {Kilohertz(rate)} kHz" : "")
        + (Suspect ? $" · seems {(Seems == QualityLevel.Lossless ? "made from a CD" : "lossy")}: its highs stop at {CutoffKilohertz(CutoffHz!.Value)} kHz" : "");

    public static string Name(string? codec) => codec switch
    {
        null => "Unknown",
        "flac" or "alac" or "mp3" or "aac" or "wav" or "aiff" or "ape" or "dsd" or "pcm" or "wma" or "mpc" => codec.ToUpperInvariant(),
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "wavpack" => "WavPack",
        _ => codec,
    };

    /// <summary>The level's name, for lists and filters.</summary>
    public static string Describe(QualityLevel level) => level switch
    {
        QualityLevel.HiRes => "Hi-res lossless",
        QualityLevel.Lossless => "Lossless",
        QualityLevel.High => "High",
        QualityLevel.Good => "Good",
        QualityLevel.Fair => "Fair",
        QualityLevel.Low => "Low",
        _ => "Unknown",
    };

    /// <summary>A number to sort by, better higher: the level, then within it the bitrate or the depth and rate.</summary>
    public double Rank => (int)Level * 1_000_000d
        + (Lossless ? (BitDepth ?? 0) * 1000d + (SampleRateHz ?? 0) / 1000d : EquivalentKbps ?? 0);

    /// <summary>The worst of several files' qualities, or null if there are none.</summary>
    public static FileQuality? Lowest(IEnumerable<FileQuality> qualities) => qualities
        .MinBy(q => q.Rank);

    /// <summary>Whether several files aren't all of one quality, as their labels say.</summary>
    public static bool Mixed(IEnumerable<FileQuality> qualities) => qualities.Select(q => (q.Label, q.Suspect)).Distinct().Skip(1).Any();

    private static string Kilohertz(int hz) => (hz / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>To a tenth of a kHz, as a cutoff is only that sure: "16.6 kHz".</summary>
    public static string CutoffKilohertz(int hz) => (hz / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>Which qualities a list of songs or albums shows; an album by its worst song.</summary>
public enum QualityFilter
{
    Any,
    HiRes,
    Lossless,
    Lossy,
    BelowGood,
}

public static class QualityFilters
{
    public static readonly IReadOnlyList<(QualityFilter Filter, string Label)> Options =
    [
        (QualityFilter.Any, "Any quality"),
        (QualityFilter.HiRes, "Hi-res lossless"),
        (QualityFilter.Lossless, "Lossless"),
        (QualityFilter.Lossy, "Lossy"),
        (QualityFilter.BelowGood, "Below good"),
    ];

    public static bool Shows(this QualityFilter filter, FileQuality? quality)
    {
        var level = quality?.Level ?? QualityLevel.Unknown;
        return filter switch
        {
            QualityFilter.HiRes => level == QualityLevel.HiRes,
            QualityFilter.Lossless => level >= QualityLevel.Lossless,
            QualityFilter.Lossy => level is >= QualityLevel.Low and <= QualityLevel.High,
            QualityFilter.BelowGood => level is QualityLevel.Low or QualityLevel.Fair,
            _ => true,
        };
    }
}
