namespace Melodex.Client;

/// <summary>
/// Answer to <c>GET /setup</c>: how far a new install has got with the steps the web app walks the admin through after
/// making their account.
/// </summary>
/// <param name="Done">The steps were finished or skipped, or this install had music before there were steps.</param>
/// <param name="LibraryPath">The music folder, as the core sees it.</param>
/// <param name="LibraryReadable">The core can list the music folder.</param>
/// <param name="AudioFiles">Audio files found in it, counting no further than <see cref="CountLimit"/>.</param>
/// <param name="MediaServer">A media server, such as Navidrome, has been added.</param>
/// <param name="Retrieval">At least one retrieval plugin has been added.</param>
/// <param name="Scanned">The library has been scanned at least once.</param>
public sealed record SetupStatus(bool Done, string LibraryPath, bool LibraryReadable, int AudioFiles, bool MediaServer, bool Retrieval, bool Scanned)
{
    public const int CountLimit = 10_000;
}

/// <summary>Body of <c>POST /setup/reset</c>.</summary>
/// <param name="Confirm">"reset", to show the admin means it.</param>
public sealed record FactoryResetRequest(string? Confirm);

/// <summary>Answer to <c>GET /version</c>.</summary>
/// <param name="Version">The server's version, with the commit it was built from after a <c>+</c> where known.</param>
/// <param name="Contracts">The plugin contract versions it supports, by kind of plugin.</param>
public sealed record ServerVersion(string Version, string ApiVersion, IReadOnlyDictionary<string, string> Contracts);
