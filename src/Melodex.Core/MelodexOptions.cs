namespace Melodex.Core;

/// <summary>Paths the core works with, bound from the <c>Melodex</c> configuration section.</summary>
public class MelodexOptions
{
    public const string Section = "Melodex";

    /// <summary>Config and database: settings, catalogue cache, crosswalk and jobs.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>The real music library. The core only reads it; beets writes it.</summary>
    public string LibraryPath { get; set; } = "music";

    /// <summary>Fresh downloads awaiting verification.</summary>
    public string StagingPath { get; set; } = "staging";

    /// <summary>Temporary listen-now files, outside every media-server library.</summary>
    public string ListenNowPath { get; set; } = "listen-now";

    /// <summary>Where in-process plugin assemblies (index plugins and connectors) are loaded from.</summary>
    public string PluginsPath { get; set; } = "plugins";

    /// <summary>The ffmpeg that decodes songs for the visualiser's bars; the image comes with one, made for that alone.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Clears the admin's password when the core starts, so the next visit sets a new one: for a forgotten password.
    /// Other users, API keys and everything else are kept. Turn it off again afterwards.
    /// </summary>
    public bool ResetLogin { get; set; }
}
