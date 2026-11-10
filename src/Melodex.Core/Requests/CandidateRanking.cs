using Melodex.Client;
using Melodex.Contracts.Plugins.Retrieval;

namespace Melodex.Core.Requests;

/// <summary>
/// Which candidates to try first, from every plugin at once: the right song before anything else, then the quality
/// the profile likes best, then the plugin's own confidence, then closeness in length.
/// </summary>
public static class CandidateRanking
{
    /// <summary>
    /// A candidate at least this sure of being the right song counts as it, and its quality decides; less sure ones go
    /// after all of those, by how sure they are. So a better quality never beats a likelier match.
    /// </summary>
    public const double SureMatch = 0.8;

    /// <summary>A candidate's quality as its plugin says it will be.</summary>
    public static FileQuality QualityOf(Candidate c)
    {
        var codec = c.Format?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "m4a" or "mp4" => "aac",
            "ogg" => "vorbis",
            "webm" => "opus",
            "aif" => "aiff",
            "wv" => "wavpack",
            "dsf" or "dff" => "dsd",
            var f => f,
        };
        return new FileQuality(codec, c.BitrateKbps, c.BitDepth, c.SampleRateHz);
    }

    /// <summary>
    /// Candidates in the order to try them, each with the plugin it's from. For keeping, those the profile turns off
    /// are left out; the best allowed comes first. For listen now, all are kept, closest to its target first.
    /// </summary>
    public static IEnumerable<(T Plugin, Candidate Candidate)> Order<T>(IEnumerable<(T Plugin, Candidate Candidate)> candidates,
        QualityProfile profile, bool listenNow, Func<Candidate, double> lengthOff)
    {
        var steps = profile.Ordered().Count;
        var target = QualityStep.Parse(profile.ListenTarget) is { } t ? profile.Position(t) : steps - 1;
        double Quality(Candidate c)
        {
            if (profile.Position(QualityOf(c)) is not { } at)
                return 2 * steps;
            // Listen now: at or under the target, nearest first; then over it, smallest first.
            return !listenNow ? at : at >= target ? at - target : steps + (target - at);
        }

        return candidates
            .Where(x => listenNow || profile.Allows(QualityOf(x.Candidate)))
            .OrderBy(x => (x.Candidate.Score ?? SureMatch) >= SureMatch ? 0 : 1)
            .ThenByDescending(x => (x.Candidate.Score ?? SureMatch) >= SureMatch ? 0 : x.Candidate.Score ?? 0)
            .ThenBy(x => Quality(x.Candidate))
            .ThenByDescending(x => x.Candidate.Score ?? SureMatch)
            .ThenBy(x => lengthOff(x.Candidate));
    }
}
