using Melodex.Contracts.Catalogue;

namespace Melodex.Core.Data;

// Catalogue entities, filled from index plugins. Every one has an internal ID; provider IDs (MusicBrainz,
// ISRC, UPC, Deezer...) live only in the crosswalk, so no plugin's ID scheme leaks into the rest of the model.

/// <summary>Which kind of catalogue entity a crosswalk row or request points at.</summary>
public enum EntityKind
{
    Artist,
    Album,
    Edition,
    Track,
}

public class Artist
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? SortName { get; set; }
    public string? Disambiguation { get; set; }
    public string? Country { get; set; }
    public DateTime RefreshedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the discography was last fetched from the index plugin; null if never.</summary>
    public DateTime? AlbumsRefreshedAt { get; set; }

    /// <summary>Whether any library file is credited to this artist, so it appears on the Library page.</summary>
    public bool InLibrary { get; set; }

    /// <summary>Its own quality setting over the profile's: null to inherit, "never" never to upgrade, or a cutoff's step key.</summary>
    public string? QualityOverride { get; set; }

    public List<Album> Albums { get; set; } = [];
}

/// <summary>An album across all its editions (a MusicBrainz release group).</summary>
public class Album
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
    public required string Title { get; set; }
    public required string ArtistCredit { get; set; }
    public AlbumType Type { get; set; }
    public List<AlbumSecondaryType> SecondaryTypes { get; set; } = [];

    /// <summary><c>yyyy</c>, <c>yyyy-MM</c> or <c>yyyy-MM-dd</c>.</summary>
    public string? FirstReleaseDate { get; set; }

    /// <summary>Whether any edition is an official release; null when unknown. Bootleg-only albums are hidden by default.</summary>
    public bool? HasOfficialRelease { get; set; }

    /// <summary>The user never wants this album listed as missing.</summary>
    public bool Ignored { get; set; }

    /// <summary>Its own quality setting over the profile's: null to inherit, "never" never to upgrade, or a cutoff's step key.</summary>
    public string? QualityOverride { get; set; }

    public DateTime RefreshedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the editions and track listings were last fetched; null if never.</summary>
    public DateTime? EditionsRefreshedAt { get; set; }

    public List<Edition> Editions { get; set; } = [];
}

/// <summary>One specific release of an album (a MusicBrainz release).</summary>
public class Edition
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AlbumId { get; set; }
    public Album Album { get; set; } = null!;
    public required string Title { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Country { get; set; }
    public string? Status { get; set; }
    public string? Disambiguation { get; set; }

    /// <summary>Media formats, for example <c>CD, CD</c> or <c>Digital Media</c>.</summary>
    public string? Formats { get; set; }

    public int TrackCount { get; set; }

    public List<EditionTrack> Tracks { get; set; } = [];
}

/// <summary>A recording, which can appear on several editions.</summary>
public class Track
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ArtistId { get; set; }
    public Artist Artist { get; set; } = null!;
    public required string Title { get; set; }
    public required string ArtistCredit { get; set; }
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// The user doesn't want this song: it stops counting towards every album it's on, as do recordings sharing its ISRC.
    /// </summary>
    public bool Ignored { get; set; }

    /// <summary>Its own quality setting over the profile's: null to inherit, "never" never to upgrade, or a cutoff's step key.</summary>
    public string? QualityOverride { get; set; }

    public List<EditionTrack> Appearances { get; set; } = [];
}

/// <summary>A track's place on an edition.</summary>
public class EditionTrack
{
    public Guid EditionId { get; set; }
    public Edition Edition { get; set; } = null!;
    public int Disc { get; set; }
    public int Position { get; set; }
    public Guid TrackId { get; set; }
    public Track Track { get; set; } = null!;

    /// <summary>Printed track number, for example <c>A1</c>.</summary>
    public required string Number { get; set; }

    /// <summary>Title on this edition, which may differ from the recording's.</summary>
    public required string Title { get; set; }

    public TimeSpan? Duration { get; set; }
}

/// <summary>
/// The crosswalk: one row per provider ID an internal entity is known by. ISRCs and UPCs are stored here too,
/// under the <c>isrc</c> and <c>upc</c> providers, as the provider-neutral glue between catalogues. Unlike provider
/// IDs, they aren't unique: real catalogues sometimes give two recordings the same ISRC.
/// </summary>
public class ProviderIdMapping
{
    public long Id { get; set; }
    public EntityKind EntityKind { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>Scheme name, see <see cref="Providers"/>.</summary>
    public required string Provider { get; set; }

    public required string Value { get; set; }

    /// <summary>The plugin instance that supplied the mapping, or null if it came from file tags or beets.</summary>
    public Guid? SourcePluginInstanceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
