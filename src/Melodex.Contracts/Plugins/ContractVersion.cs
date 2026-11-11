namespace Melodex.Contracts.Plugins;

/// <summary>
/// A plugin contract version. Minor versions only add optional members, so a core that supports
/// <c>1.3</c> accepts plugins built for <c>1.0</c> to <c>1.3</c>; a new major version is a breaking change.
/// </summary>
public readonly record struct ContractVersion(int Major, int Minor)
{
    /// <summary>Whether a core supporting this version can load a plugin built for <paramref name="target"/>.</summary>
    public bool Supports(ContractVersion target) => target.Major == Major && target.Minor <= Minor;

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>Parses a <c>major.minor</c> string.</summary>
    public static ContractVersion Parse(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            throw new FormatException($"'{value}' is not a contract version of the form major.minor.");
        return new ContractVersion(major, minor);
    }
}

/// <summary>The contract versions this build of the contracts package defines.</summary>
public static class ContractVersions
{
    /// <summary>Index plugins: what exists.</summary>
    public static readonly ContractVersion Index = new(1, 0);

    /// <summary>Retrieval plugins: how music is fetched (HTTP contract).</summary>
    public static readonly ContractVersion Retrieval = new(1, 0);

    /// <summary>Media-server connectors: where music ends up.</summary>
    public static readonly ContractVersion Connector = new(1, 3);

    /// <summary>Artwork plugins: pictures of artists and albums.</summary>
    public static readonly ContractVersion Artwork = new(1, 0);

    /// <summary>Lyrics plugins: the words of songs.</summary>
    public static readonly ContractVersion Lyrics = new(1, 1);

    /// <summary>The version the core supports for a given plugin type.</summary>
    public static ContractVersion For(PluginType type) => type switch
    {
        PluginType.Index => Index,
        PluginType.Retrieval => Retrieval,
        PluginType.Connector => Connector,
        PluginType.Artwork => Artwork,
        PluginType.Lyrics => Lyrics,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
