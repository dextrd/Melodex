using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Melodex.Web;

/// <summary>The parts of the app that remember where you were in them.</summary>
public enum NavSection { Library, Search }

/// <summary>
/// Where you were in the Library and in Search, this session. The artist, album and song pages you open from one belong
/// to it. Its link in the top bar goes back to the page you were last on there; from there, back to its own page as you
/// left it; and from that, to its own page afresh.
/// </summary>
public sealed class NavSections : IDisposable
{
    private readonly NavigationManager _nav;
    private readonly SearchMemory _search;
    private readonly Dictionary<NavSection, string> _last = [];
    private readonly Dictionary<NavSection, string> _home = [];

    public NavSections(NavigationManager nav, SearchMemory search)
    {
        _nav = nav;
        _search = search;
        _nav.LocationChanged += Moved;
        Follow(_nav.Uri);
    }

    /// <summary>The section you're in, if any.</summary>
    public NavSection? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Where the section's link goes now.</summary>
    public string Href(NavSection section)
    {
        if (Current != section)
            return _last.GetValueOrDefault(section) ?? Root(section);
        // On its own page already, a fresh search; the Library stays as you set it.
        return IsDeep(Here) || section == NavSection.Library ? _home.GetValueOrDefault(section) ?? Root(section) : Root(section);
    }

    /// <summary>Follows the section's link: on its own page already, that's the page afresh.</summary>
    public void Go(NavSection section)
    {
        var href = Href(section);
        if (Current == section && !IsDeep(Here) && section == NavSection.Search)
            _search.Forget();
        // Back in the section, even on an artist or album page.
        Current = section;
        _nav.NavigateTo(href);
    }

    /// <summary>The Library's Browse tab, as you left it: which view, and so on.</summary>
    public string BrowseHref => _browse ?? "";

    private string? _browse;

    private string Here => _nav.ToBaseRelativePath(_nav.Uri);

    private static string Root(NavSection section) => section == NavSection.Search ? "search" : "";

    private static string Bare(string path) => path.Split('?', '#')[0].TrimEnd('/');

    // The pages that belong to whichever section they were opened from.
    private static bool IsDeep(string path) =>
        Bare(path) is var bare && (bare.StartsWith("artists/") || bare.StartsWith("albums/") || bare.StartsWith("tracks/") || bare.StartsWith("local/")
                                   || bare.StartsWith("playlists/"));

    private static NavSection? SectionOf(string path) => Bare(path) switch
    {
        "" or "playlists" => NavSection.Library,
        "search" => NavSection.Search,
        _ => null,
    };

    private void Moved(object? sender, LocationChangedEventArgs e)
    {
        Follow(e.Location);
        Changed?.Invoke();
    }

    private void Follow(string uri)
    {
        var path = _nav.ToBaseRelativePath(uri);
        if (SectionOf(path) is { } section)
        {
            Current = section;
            _home[section] = _last[section] = path;
            if (Bare(path) == "")
                _browse = path;
        }
        // A playlist is the Library's, however it was opened: its page shows the Library's tabs.
        else if (Bare(path).StartsWith("playlists/"))
        {
            Current = NavSection.Library;
            _last[NavSection.Library] = path;
        }
        else if (IsDeep(path) && Current is { } within)
            _last[within] = path;
        else
            Current = null;
    }

    public void Dispose() => _nav.LocationChanged -= Moved;
}
