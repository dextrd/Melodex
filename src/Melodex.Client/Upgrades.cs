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
    /// <summary>Look for upgrades in the whole library once a week.</summary>
    public bool Weekly { get; init; } = true;

    /// <summary>The most upgrades one look through the whole library asks for, from 1 to 500, longest unlooked-for first.</summary>
    public int PerRun { get; init; } = 25;

    public OldFiles OldFiles { get; init; } = OldFiles.Hold;

    /// <summary>How long a replaced file is held, from 1 to 365 days.</summary>
    public int HoldDays { get; init; } = 14;
}

/// <summary>Upgrades' settings and where they stand.</summary>
/// <param name="Warning">Why an upgrade may lose stars, plays and playlists in the connected media server; null when it keeps them.</param>
/// <param name="NextRun">When the weekly look through the library is next, while it's on.</param>
/// <param name="Upgradable">Matched files below their cutoff.</param>
/// <param name="Held">Files replaced by better copies that can still be put back, newest first.</param>
public sealed record UpgradeStatus(UpgradeSettings Settings, string? Warning, DateTime? NextRun, int Upgradable, IReadOnlyList<HeldFileInfo> Held);

/// <summary>A file an upgrade replaced, held outside the music folder until it's deleted.</summary>
/// <param name="Path">Where it was, relative to the music folder.</param>
/// <param name="ReplacedBy">The better copy's path.</param>
/// <param name="Was">Its quality.</param>
/// <param name="Now">The better copy's quality.</param>
public sealed record HeldFileInfo(Guid Id, Guid? TrackId, string Title, string Path, string ReplacedBy, FileQuality Was, FileQuality? Now, DateTime HeldAt,
    DateTime DeleteAt);

/// <summary>What a look for upgrades found.</summary>
/// <param name="Upgradable">Files below their cutoff in what was looked through.</param>
/// <param name="Queued">Upgrades asked for: fewer when some are already being looked for, or there are more than one look asks for.</param>
public sealed record UpgradeSearch(int Upgradable, int Queued);
