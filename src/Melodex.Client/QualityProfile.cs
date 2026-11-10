namespace Melodex.Client;

/// <summary>Codecs as the profile tells them apart: the common ones by name, the rest as other lossless or lossy.</summary>
public enum CodecFamily
{
    Flac,
    Alac,

    /// <summary>WAV, AIFF and raw PCM.</summary>
    Uncompressed,

    /// <summary>APE, WavPack and DSD.</summary>
    OtherLossless,
    Opus,
    Aac,
    Vorbis,
    Mp3,

    /// <summary>WMA, Musepack and anything else lossy.</summary>
    OtherLossy,
}

/// <summary>
/// One row of a quality profile: a codec at a level, such as "FLAC hi-res" or "Opus high". Lossless codecs come in
/// hi-res and CD; lossy ones in high, good, fair and low (see <see cref="QualityLevel"/>).
/// </summary>
public readonly record struct QualityStep(CodecFamily Codec, QualityLevel Level)
{
    public bool Lossless => Codec is CodecFamily.Flac or CodecFamily.Alac or CodecFamily.Uncompressed or CodecFamily.OtherLossless;

    /// <summary>How it's stored: "flac-hires", "opus-good".</summary>
    public string Key => $"{Codec.ToString().ToLowerInvariant()}-{Level.ToString().ToLowerInvariant()}";

    /// <summary>"FLAC hi-res", "FLAC CD", "Opus high", "Other lossy low".</summary>
    public string Name => $"{CodecName(Codec)} {LevelName(Level)}";

    public static string CodecName(CodecFamily codec) => codec switch
    {
        CodecFamily.Flac => "FLAC",
        CodecFamily.Alac => "ALAC",
        CodecFamily.Uncompressed => "WAV/AIFF",
        CodecFamily.OtherLossless => "Other lossless",
        CodecFamily.Opus => "Opus",
        CodecFamily.Aac => "AAC",
        CodecFamily.Vorbis => "Vorbis",
        CodecFamily.Mp3 => "MP3",
        _ => "Other lossy",
    };

    public static string LevelName(QualityLevel level) => level switch
    {
        QualityLevel.HiRes => "hi-res",
        QualityLevel.Lossless => "CD",
        _ => level.ToString().ToLowerInvariant(),
    };

    /// <summary>Every step there is, in the default order: best first, and within a level the codecs that do most with it.</summary>
    public static IReadOnlyList<QualityStep> All { get; } =
    [
        .. new[] { QualityLevel.HiRes, QualityLevel.Lossless }.SelectMany(level =>
            new[] { CodecFamily.Flac, CodecFamily.Alac, CodecFamily.Uncompressed, CodecFamily.OtherLossless }.Select(c => new QualityStep(c, level))),
        .. new[] { QualityLevel.High, QualityLevel.Good, QualityLevel.Fair, QualityLevel.Low }.SelectMany(level =>
            new[] { CodecFamily.Opus, CodecFamily.Aac, CodecFamily.Vorbis, CodecFamily.Mp3, CodecFamily.OtherLossy }.Select(c => new QualityStep(c, level))),
    ];

    public static QualityStep? Parse(string? key) => All.Cast<QualityStep?>().FirstOrDefault(s => s!.Value.Key == key);

    /// <summary>The step a file's quality is, or null when too little is known of it (no codec, or lossy with no bitrate).</summary>
    public static QualityStep? Of(FileQuality? quality)
    {
        if (quality?.Codec is not { } codec || quality.Level == QualityLevel.Unknown)
            return null;
        var family = codec switch
        {
            "flac" => CodecFamily.Flac,
            "alac" => CodecFamily.Alac,
            "wav" or "aiff" or "pcm" => CodecFamily.Uncompressed,
            "ape" or "wavpack" or "dsd" => CodecFamily.OtherLossless,
            "opus" => CodecFamily.Opus,
            "aac" => CodecFamily.Aac,
            "vorbis" => CodecFamily.Vorbis,
            "mp3" => CodecFamily.Mp3,
            _ => quality.Lossless ? CodecFamily.OtherLossless : CodecFamily.OtherLossy,
        };
        return new QualityStep(family, quality.Level);
    }
}

/// <summary>One step of a profile in its place: whether it's ever wanted.</summary>
public sealed record ProfileStep(string Key, bool Allowed);

/// <summary>
/// Which qualities are wanted, as Sonarr's profiles: every <see cref="QualityStep"/> in order of preference, any turned
/// off as never wanted, and a cutoff, the step that's good enough. New downloads take the best allowed quality of
/// the right song; listen-now takes the closest to its own, smaller, target.
/// </summary>
public sealed record QualityProfile
{
    /// <summary>The steps in order, best first. Steps missing from it (a newer Melodex's) go where the default has them.</summary>
    public IReadOnlyList<ProfileStep> Steps { get; init; } = Default;

    /// <summary>The step that's good enough: music at it or above isn't upgraded.</summary>
    public string Cutoff { get; init; } = new QualityStep(CodecFamily.Flac, QualityLevel.Lossless).Key;

    /// <summary>
    /// What listen-now aims for: it takes the best quality at this step or below it, or failing that the smallest
    /// above, so a song starts soon. It isn't kept, and fair or low quality is fine to listen to once.
    /// </summary>
    public string ListenTarget { get; init; } = new QualityStep(CodecFamily.Opus, QualityLevel.Fair).Key;

    /// <summary>The default: everything in its usual order, fair and low lossy turned off.</summary>
    public static IReadOnlyList<ProfileStep> Default { get; } =
        QualityStep.All.Select(s => new ProfileStep(s.Key, s.Level >= QualityLevel.Good)).ToList();

    /// <summary>The steps in order, every one of them: unknown keys dropped, missing ones put in after their default neighbour.</summary>
    public IReadOnlyList<(QualityStep Step, bool Allowed)> Ordered()
    {
        var steps = Steps.Select(s => (Step: QualityStep.Parse(s.Key), s.Allowed))
            .Where(s => s.Step is not null).Select(s => (Step: s.Step!.Value, s.Allowed))
            .DistinctBy(s => s.Step).ToList();
        foreach (var (step, i) in QualityStep.All.Select((s, i) => (s, i)))
        {
            if (steps.Any(s => s.Step == step))
                continue;
            var after = QualityStep.All.Take(i).Reverse().Select(p => steps.FindIndex(s => s.Step == p)).FirstOrDefault(x => x >= 0, -1);
            steps.Insert(after + 1, (step, step.Level >= QualityLevel.Good));
        }
        return steps;
    }

    /// <summary>Where a quality stands, best 0; null when it's unknown, so it goes after every known one.</summary>
    public int? Position(FileQuality? quality) => QualityStep.Of(quality) is { } step ? Position(step) : null;

    public int Position(QualityStep step) => Ordered().ToList().FindIndex(s => s.Step == step);

    /// <summary>Whether a quality may be fetched: an unknown one may, to be read once it's here.</summary>
    public bool Allows(FileQuality? quality) => QualityStep.Of(quality) is not { } step || Ordered().First(s => s.Step == step).Allowed;

    /// <summary>Whether a quality is good enough, by this cutoff (or another's, such as an album's own).</summary>
    public bool Meets(FileQuality? quality, string? cutoff = null) =>
        Position(quality) is { } at && QualityStep.Parse(cutoff ?? Cutoff) is { } step && at <= Position(step);
}

/// <summary>An artist's, album's or track's own quality setting, over the main profile's.</summary>
public enum QualityOverrideKind
{
    /// <summary>As the album, artist or main profile has it.</summary>
    Inherit,

    /// <summary>Its own cutoff.</summary>
    Cutoff,

    /// <summary>Never upgraded, whatever its quality.</summary>
    NeverUpgrade,
}

/// <summary>A quality setting as it applies to something, and where it comes from.</summary>
/// <param name="Kind">Its own setting: inherit, a cutoff, or never upgrade.</param>
/// <param name="Cutoff">Its own cutoff's step key, when it has one.</param>
/// <param name="Effective">What applies: <see cref="QualityOverrideKind.Cutoff"/> or <see cref="QualityOverrideKind.NeverUpgrade"/>.</param>
/// <param name="EffectiveCutoff">The cutoff that applies, unless it's never upgraded.</param>
/// <param name="From">Where what applies is set: "this track", "the album Mezzanine", "Massive Attack", "the main profile".</param>
public sealed record QualitySetting(QualityOverrideKind Kind, string? Cutoff, QualityOverrideKind Effective, string? EffectiveCutoff, string From);

/// <summary>Body of saving something's own quality setting.</summary>
public sealed record QualityOverride(QualityOverrideKind Kind, string? Cutoff = null);
