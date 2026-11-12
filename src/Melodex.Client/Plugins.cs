using System.Text.Json;
using Melodex.Contracts.Plugins;

namespace Melodex.Client;

// Plugins: index plugins and media-server connectors load into the core; retrieval plugins run in their own
// containers and are added by URL. Every one is configured through a form rendered from its settings schema.

/// <summary>A configured plugin.</summary>
/// <param name="PluginId">The manifest ID, for example <c>melodex.retrieval.ytdlp</c>.</param>
/// <param name="Name">The name you gave it.</param>
/// <param name="Endpoint">For retrieval plugins, the container's URL.</param>
/// <param name="Priority">Lower goes first: the fallback order for retrieval plugins.</param>
/// <param name="Settings">Its settings, with secrets left out.</param>
/// <param name="Manifest">What the plugin says about itself, including its settings schema and capabilities.</param>
public sealed record PluginInfo(
    Guid Id,
    string PluginId,
    PluginType Type,
    string Name,
    string? Endpoint,
    int Priority,
    bool Enabled,
    HealthStatus? Health,
    string? HealthMessage,
    DateTime? HealthCheckedAt,
    JsonElement Settings,
    PluginManifest? Manifest);

/// <summary>A plugin loaded into the core that can be set up. Any plugin that runs on its own is set up by its address instead.</summary>
/// <param name="Problem">Why it can't be set up, if it can't.</param>
public sealed record AvailablePlugin(PluginManifest Manifest, string Source, string? Problem);

/// <summary>Body of <c>POST /plugins</c>.</summary>
/// <param name="PluginId">An in-process plugin from <c>GET /plugins/available</c>; leave out for one at an address.</param>
/// <param name="Endpoint">
/// The address of a plugin that runs on its own, such as a retrieval plugin's container, for example
/// <c>http://retriever-ytdlp:8080</c>. Its manifest says what kind of plugin it is.
/// </param>
/// <param name="Name">Defaults to the plugin's name.</param>
/// <param name="Settings">Defaults to the schema's defaults.</param>
/// <param name="Type">The kind of plugin expected at the endpoint; one of another kind is refused.</param>
public sealed record AddPlugin(string? PluginId = null, string? Endpoint = null, string? Name = null, JsonElement? Settings = null, PluginType? Type = null);

/// <summary>Body of <c>PUT /plugins/{id}</c>. Anything left out stays as it is; a secret left out of the settings keeps its value.</summary>
public sealed record UpdatePlugin(string? Name = null, JsonElement? Settings = null, bool? Enabled = null, string? Endpoint = null);

/// <summary>Body of <c>POST /plugins/{id}/checks/{check}</c>.</summary>
/// <param name="Settings">Settings to check with, as in the settings form before saving; left out, the saved ones.</param>
public sealed record RunPluginCheck(JsonElement? Settings = null);

/// <summary>What a plugin's check found.</summary>
public sealed record PluginCheckResult(bool Passed, string Message);

/// <summary>Body of <c>PUT /plugins/order</c>: the instances of one type, in the order to use them.</summary>
public sealed record PluginOrder(IReadOnlyList<Guid> Ids);
