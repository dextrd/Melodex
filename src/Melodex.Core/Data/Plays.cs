using Melodex.Client;

namespace Melodex.Core.Data;

/// <summary>A song someone played (#2), as it was then, so history keeps it after the file or album goes.</summary>
public class Play
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>The song's track ID, or a file of your own's file ID.</summary>
    public Guid TrackId { get; set; }

    public Guid? FileId { get; set; }
    public required string Title { get; set; }
    public required string ArtistCredit { get; set; }
    public Guid? ArtistId { get; set; }
    public Guid? AlbumId { get; set; }
    public string? AlbumTitle { get; set; }
    public string? CoverUrl { get; set; }
    public double? DurationSeconds { get; set; }

    /// <summary>MusicBrainz IDs where the file has them, which ListenBrainz matches plays by.</summary>
    public string? RecordingMbid { get; set; }

    public string? ReleaseMbid { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
    public PlaySourceKind? SourceKind { get; set; }
    public Guid? SourceId { get; set; }
    public string? SourceName { get; set; }

    /// <summary><c>web</c>, or a Subsonic player's name.</summary>
    public string Client { get; set; } = "web";

    /// <summary>Announced to webhooks and the event stream.</summary>
    public bool Announced { get; set; }
}

/// <summary>
/// A song someone skipped within its first half minute, as a radio played it (#4): no radio plays it them again, and
/// songs by the same artist come up less, for <see cref="Listening.RadioService.SkipsKept"/>.
/// </summary>
public class RadioSkip
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid TrackId { get; set; }
    public Guid? ArtistId { get; set; }
    public DateTime SkippedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Someone's account on ListenBrainz or Last.fm, which their plays are sent to.</summary>
public class ScrobbleAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public ScrobbleService Service { get; set; }
    public required string Username { get; set; }

    /// <summary>The ListenBrainz user token or Last.fm session key, encrypted.</summary>
    public required string Secret { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSentAt { get; set; }
    public string? Error { get; set; }

    /// <summary>The service turned the link down: nothing's sent until it's linked again.</summary>
    public bool Refused { get; set; }
}

/// <summary>A play still to send to one linked account; gone once it's sent.</summary>
public class ScrobbleSend
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PlayId { get; set; }
    public Play Play { get; set; } = null!;
    public Guid AccountId { get; set; }
    public ScrobbleAccount Account { get; set; } = null!;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
}
