namespace Melodex.Core.Data;

/// <summary>Someone who signs in to Melodex. The first, made at setup, is the admin, who may do everything.</summary>
public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Username { get; set; }

    /// <summary>The username in lower case, so two users can't differ only by case.</summary>
    public required string NormalizedUsername { get; set; }

    /// <summary>PBKDF2 with SHA-256: <c>pbkdf2-sha256$iterations$salt$hash</c>. Empty for the admin after a login reset.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>What they may do, comma-separated. The admin may do everything whatever this says.</summary>
    public string Permissions { get; set; } = "";

    /// <summary>The admin: made at setup, never removed, and changed only by themself.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Changes with the password, which signs out their other sessions.</summary>
    public string Stamp { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSignInAt { get; set; }

    /// <summary>How the web app looks to them, as JSON: see <c>Appearance</c>. Empty for the default.</summary>
    public string? Appearance { get; set; }

    /// <summary>Their own logins on the media servers, for syncing their playlists: one for each server, kept when another takes over.</summary>
    public List<MediaServerLogin> MediaServerLogins { get; set; } = [];

    /// <summary>Why their playlists last failed to sync, if they did.</summary>
    public string? PlaylistSyncError { get; set; }

    public DateTime? PlaylistsSyncedAt { get; set; }
}

/// <summary>Someone's login on one media server, for syncing their playlists with it.</summary>
public class MediaServerLogin
{
    public Guid UserId { get; set; }

    /// <summary>The media-server plugin it's for. Removing the plugin removes its logins.</summary>
    public Guid PluginInstanceId { get; set; }
    public PluginInstance PluginInstance { get; set; } = null!;

    public required string Username { get; set; }

    /// <summary>The password, encrypted with the data protection keys: it has to be sent, so it can't be hashed.</summary>
    public required string Password { get; set; }
}

/// <summary>
/// A key an app uses to call the API. Only a hash of the key is kept: it's shown once, when it's made, and can't be
/// recovered, only revoked and replaced.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the key is for, usually the app's name. Requests made with it are credited to this.</summary>
    public required string Name { get; set; }

    /// <summary>The start of the key, to tell keys apart in a list without showing them.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the whole key, in hex. Keys are long and random, so a plain hash is enough.</summary>
    public required string Hash { get; set; }

    /// <summary>What it may do, comma-separated: any of its owner's permissions, or admin for all of them.</summary>
    public required string Scopes { get; set; }

    /// <summary>Who made it. It can never do more than they may, and goes when they do.</summary>
    public Guid? UserId { get; set; }

    public User? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Roughly when it was last used; updated at most once a minute.</summary>
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>An address Melodex tells, with a signed POST, when something it cares about happens.</summary>
public class Webhook
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required string Url { get; set; }

    /// <summary>Signs each delivery, so the receiver can tell it came from this Melodex.</summary>
    public required string Secret { get; set; }

    /// <summary>The events it gets, comma-separated; empty for all of them.</summary>
    public string Events { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum WebhookDeliveryStatus
{
    /// <summary>Not sent yet, or sent and failed with tries left.</summary>
    Pending,
    Delivered,

    /// <summary>Every try failed.</summary>
    Failed,
}

/// <summary>One event sent, or to be sent, to one webhook. Kept in the database so deliveries survive a restart.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WebhookId { get; set; }
    public Webhook Webhook { get; set; } = null!;

    public required string Event { get; set; }

    /// <summary>The body, exactly as sent, so retries send the same bytes and the same signature check works.</summary>
    public required string PayloadJson { get; set; }

    public WebhookDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>The HTTP status of the last try, if it got one.</summary>
    public int? ResponseStatus { get; set; }

    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
