using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

/// <summary>Excluding, deferring and hiding files, and putting them back to be matched.</summary>
public static class SetAsides
{
    public static SetAside Of(LibraryFile file) =>
        file.HiddenAt is not null ? SetAside.Hidden
        : file.ExcludedAt is not null ? SetAside.Excluded
        : file.DeferredAt is not null ? SetAside.Deferred
        : SetAside.None;

    /// <summary>Whether a file's own tags are enough for the library to show it: a title and an artist.</summary>
    public static bool Usable(FileTags tags) => !string.IsNullOrWhiteSpace(tags.Title) && !string.IsNullOrWhiteSpace(tags.Artist ?? tags.AlbumArtist);

    /// <summary>
    /// Sets a file aside, or with <see cref="SetAside.None"/> puts it back on the review list, to be matched again at the
    /// next scan. Excluding or hiding a file drops any match it had; deferring keeps its suggestion until the next scan
    /// tries again.
    /// </summary>
    public static void Apply(LibraryFile file, SetAside to)
    {
        if (Of(file) == to)
            return;
        var now = DateTime.UtcNow;
        file.ExcludedAt = to == SetAside.Excluded ? now : null;
        file.DeferredAt = to == SetAside.Deferred ? now : null;
        file.HiddenAt = to == SetAside.Hidden ? now : null;
        file.PutBackAt = to == SetAside.None ? now : null;
        switch (to)
        {
            case SetAside.None:
                // On the review list until the next scan matches it again; a file never matched yet is simply matched then.
                file.MatchedAt = file.MatchedAt is null ? null : now;
                break;
            case SetAside.Deferred:
                file.MatchedAt ??= now;
                break;
            default:
                file.TrackId = file.EditionId = file.SuggestedTrackId = file.SuggestedEditionId = null;
                file.ArtistId = null;
                file.MatchMethod = MatchMethod.Unmatched;
                file.MatchConfidence = null;
                file.ReviewReason = null;
                file.MatchedAt ??= now;
                break;
        }
    }
}
