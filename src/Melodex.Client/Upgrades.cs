using System.Text.Json.Serialization;

namespace Melodex.Client;

/// <summary>What happens to a file once a better copy has replaced it.</summary>
public enum OldFiles
{
    /// <summary>Held outside the music folder for <see cref="UpgradeSettings.HoldDays"/>, so it can be put back, then deleted.</summary>
    Hold,

    /// <summary>Deleted straight away.</summary>
    Delete,
}

/// <summary>
/// How owned music below its cutoff is upgraded: replaced by a better copy of the same song, tagged as the old file was,
/// at its path with the new file's extension.
/// </summary>
public sealed record UpgradeSettings
{
    /// <summary>Look through the whole library for upgrades on a schedule: on <see cref="Days"/>, between <see cref="From"/> and <see cref="Until"/>.</summary>
    public bool Scheduled { get; init; } = true;

    /// <summary>The days a look starts on: one for weekly, all seven for daily.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [DayOfWeek.Monday];

    /// <summary>When on those days upgrades may start being asked for.</summary>
    public TimeOnly From { get; init; } = new(1, 0);

    /// <summary>When no more are: it's the next day when it's earlier than <see cref="From"/>, as 22:00 to 04:00 is.</summary>
    public TimeOnly Until { get; init; } = new(6, 0);

    /// <summary>The time zone the days and times are in, as the browser that saved them names it (IANA); UTC if it's unknown.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>
    /// The most upgrades one look asks for, from 1 to 500, longest unlooked-for first; null for as many as there are
    /// time for in the window. A look through the whole library by hand asks for this many too, or 500 at most.
    /// </summary>
    // Written even when null, which would otherwise read back as the default.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? PerRun { get; init; } = 25;

    public OldFiles OldFiles { get; init; } = OldFiles.Hold;

    /// <summary>How long a replaced file is held, from 1 to 365 days.</summary>
    public int HoldDays { get; init; } = 14;
}

/// <summary>Upgrades' settings and where they stand.</summary>
/// <param name="Warning">Why an upgrade may lose stars, plays and playlists in the connected media server; null when it keeps them.</param>
/// <param name="NextRun">When the scheduled look through the library next starts, while it's on.</param>
/// <param name="Running">Whether it's looking now, in its window.</param>
/// <param name="Upgradable">Matched files below their cutoff.</param>
/// <param name="Held">Files replaced by better copies that can still be put back, newest first.</param>
public sealed record UpgradeStatus(UpgradeSettings Settings, string? Warning, DateTime? NextRun, int Upgradable, IReadOnlyList<HeldFileInfo> Held,
    bool Running = false);

/// <summary>A file an upgrade replaced, held outside the music folder until it's deleted.</summary>
/// <param name="Path">Where it was, relative to the music folder.</param>
/// <param name="ReplacedBy">The better copy's path.</param>
/// <param name="Was">Its quality.</param>
/// <param name="Now">The better copy's quality.</param>
public sealed record HeldFileInfo(Guid Id, Guid? TrackId, string Title, string Path, string ReplacedBy, FileQuality Was, FileQuality? Now, DateTime HeldAt,
    DateTime DeleteAt, string? CoverUrl = null, double? DurationSeconds = null);

/// <summary>What a look for upgrades found.</summary>
/// <param name="Upgradable">Files below their cutoff in what was looked through.</param>
/// <param name="Queued">Upgrades asked for: fewer when some are already being looked for, or there are more than one look asks for.</param>
public sealed record UpgradeSearch(int Upgradable, int Queued);
