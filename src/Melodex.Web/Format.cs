using Melodex.Client;
using Melodex.Contracts.Catalogue;

namespace Melodex.Web;

public static class Format
{
    public static string Duration(double? seconds) =>
        seconds is { } s ? TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";

    /// <summary>A list's items with their places in it, for a <c>Virtualize</c>, which draws only some of them.</summary>
    public static List<(T Item, int Index)> Indexed<T>(IReadOnlyList<T> items) => items.Select((item, i) => (item, i)).ToList();

    /// <summary>Widens a list's # column to fit its longest number.</summary>
    public static string NumberColumn(int count) => $"--digits: {Math.Max(2, count.ToString().Length)}";

    public static string Year(string? date) => date is { Length: >= 4 } ? date[..4] : "";

    public static string TypeName(AlbumType type) => type switch
    {
        AlbumType.Album => "Albums",
        AlbumType.Ep => "EPs",
        AlbumType.Single => "Singles",
        AlbumType.Broadcast => "Broadcasts",
        _ => "Other",
    };

    public static string TypeLabel(AlbumType type) => type switch
    {
        AlbumType.Ep => "EP",
        _ => type.ToString(),
    };

    public static string Secondary(IReadOnlyList<AlbumSecondaryType> types) =>
        string.Join(", ", types.Select(t => t switch
        {
            AlbumSecondaryType.DjMix => "DJ mix",
            AlbumSecondaryType.AudioDrama => "Audio drama",
            AlbumSecondaryType.FieldRecording => "Field recording",
            AlbumSecondaryType.Spokenword => "Spoken word",
            _ => t.ToString(),
        }));

    public static string Tracks(AlbumSummary album) =>
        album.TotalTracks is { } total ? $"{album.OwnedTracks ?? 0} of {total}" : "";

    /// <summary>A request's state in a word or two, with progress for requests made of parts.</summary>
    public static string RequestLabel(RequestInfo r) => r.State switch
    {
        _ when r.Paused && r.Active => "Paused",
        Client.RequestState.Queued => "Queued",
        Client.RequestState.InProgress when r.Parts is { } p => $"Fetching {p.InLibrary} of {p.Total}",
        Client.RequestState.InProgress => "In progress",
        Client.RequestState.Searching => "Searching",
        Client.RequestState.Fetching => "Fetching",
        Client.RequestState.Verifying => "Checking",
        Client.RequestState.Importing or Client.RequestState.Notifying when r.Upgrade => "Upgrading",
        Client.RequestState.Importing or Client.RequestState.Notifying => "Filing",
        // An upgrade that found nothing better leaves the song as it was, in the library.
        Client.RequestState.InLibrary when r.Upgrade => r.Detail?.StartsWith("Upgraded") == true ? "Upgraded" : "Unchanged",
        Client.RequestState.InLibrary => "In library",
        Client.RequestState.Playable => "Ready to play",
        Client.RequestState.NeedsReview => "Needs review",
        Client.RequestState.Failed => "Failed",
        _ => "Cancelled",
    };

    /// <summary>The badge colour for a request, grey while paused.</summary>
    public static string RequestBadge(RequestInfo r) => r.Paused && r.Active ? "neutral" : RequestBadge(r.State);

    /// <summary>The badge colour for a request state.</summary>
    public static string RequestBadge(Client.RequestState state) => state switch
    {
        Client.RequestState.InLibrary => "owned",
        Client.RequestState.Playable => "covered",
        Client.RequestState.NeedsReview => "partial",
        Client.RequestState.Failed => "missing",
        Client.RequestState.Cancelled => "neutral",
        Client.RequestState.Queued => "active waiting",
        _ => "active",
    };

    public static string TaskLabel(TaskInfo task) => task.State switch
    {
        TaskState.Queued when task.Paused => "Paused",
        TaskState.Queued => "Waiting",
        TaskState.Running => "Running",
        TaskState.Succeeded => "Done",
        TaskState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    public static string TaskBadge(TaskInfo task) => task.State switch
    {
        TaskState.Queued when task.Paused => "neutral",
        TaskState.Succeeded => "owned",
        TaskState.Failed => "missing",
        TaskState.Cancelled => "neutral",
        TaskState.Queued => "active waiting",
        _ => "active",
    };

    /// <summary>How long ago, roughly: "just now", "5 min ago", "3 h ago", or the date.</summary>
    public static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : utc.ToLocalTime().ToString("d MMM");
    }

    /// <summary>How soon, roughly: "in 5 min", "in 3 h", or the date.</summary>
    public static string Until(DateTime utc)
    {
        var left = utc - DateTime.UtcNow;
        return left.TotalMinutes < 1 ? "any minute"
            : left.TotalHours < 1 ? $"in {(int)left.TotalMinutes} min"
            : left.TotalDays < 1 ? $"in {(int)left.TotalHours} h {left.Minutes} min"
            : $"on {utc.ToLocalTime():d MMM}";
    }

    /// <summary>A number of minutes as people say it: "45 minutes", "1 hour", "2 days".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };

    /// <summary>Where an album's page is: the catalogue's, or for music of your own, its local page.</summary>
    public static string AlbumHref(AlbumSummary album) => $"{(album.Local ? "local/albums" : "albums")}/{album.Id}";

    public static string? AlbumHref(LibraryTrack track) => track.AlbumId is { } album ? $"{(track.Local ? "local/albums" : "albums")}/{album}" : null;

    public static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    public static string Minutes(int minutes) =>
        minutes % 1440 == 0 ? Plural(minutes / 1440, "day")
        : minutes % 60 == 0 ? Plural(minutes / 60, "hour")
        : Plural(minutes, "minute");

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
