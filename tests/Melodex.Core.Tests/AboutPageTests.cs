using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The About page's "Built on" names everything Melodex works with, so it's kept up to date as plugins are added.</summary>
public sealed class AboutPageTests
{
    // How the page names a plugin whose folder doesn't say it as written.
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FanartTv"] = "fanart.tv",
        ["archive"] = "Internet Archive",
        ["ytdlp"] = "yt-dlp",
    };

    [Fact]
    public void Built_on_names_every_plugin()
    {
        var root = TestLibrary.RepositoryRoot;
        var page = File.ReadAllText(Path.Combine(root, "src", "Melodex.Web", "Pages", "About.razor"));
        var plugins = Directory.GetDirectories(Path.Combine(root, "src"), "Melodex.Plugins.*").Select(d => Path.GetFileName(d)["Melodex.Plugins.".Length..])
            .Concat(Directory.GetDirectories(Path.Combine(root, "plugins"), "retriever-*").Select(d => Path.GetFileName(d)["retriever-".Length..]))
            .ToList();
        Assert.NotEmpty(plugins);
        Assert.All(plugins, plugin => Assert.Contains(Named.GetValueOrDefault(plugin, plugin), page, StringComparison.OrdinalIgnoreCase));
    }
}
