using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;

namespace Melodex.Core.Plugins;

/// <summary>
/// A retrieval plugin instance, reached over the HTTP contract (docs/contracts/retrieval-v1.openapi.yaml). Settings are
/// sent with every call, so the container keeps no setup of its own.
/// </summary>
public sealed class RetrievalClient(HttpClient http, JsonElement settings) : IRetrievalPlugin
{
    public static async Task<PluginManifest> GetManifestAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("v1/manifest", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginManifest>(RetrievalJson.Options, ct))!;
    }

    public Task<PluginManifest> GetManifestAsync(CancellationToken cancellationToken) => GetManifestAsync(http, cancellationToken);

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/health", new HealthRequest(settings), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HealthReport>(RetrievalJson.Options, cancellationToken))!;
    }

    /// <summary>Runs one of the checks the manifest offers.</summary>
    public async Task<CheckResult> RunCheckAsync(string check, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"v1/checks/{Uri.EscapeDataString(check)}", new HealthRequest(settings), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<CheckResult>(RetrievalJson.Options, cancellationToken))!;
    }

    public async Task<IReadOnlyList<Candidate>> SearchAsync(RetrievalTarget target, int limit, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/search", new SearchRequest(settings, target, limit), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<SearchResponse>(RetrievalJson.Options, cancellationToken))!.Candidates;
    }

    public async Task<FetchStatus> StartFetchAsync(string jobId, RetrievalTarget target, Candidate candidate, string stagingDirectory, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/fetch", new FetchRequest(settings, jobId, target, candidate, stagingDirectory), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<FetchStatus>(RetrievalJson.Options, cancellationToken))!;
    }

    public async Task<FetchStatus> GetFetchStatusAsync(string jobId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"v1/fetch/{Uri.EscapeDataString(jobId)}", cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<FetchStatus>(RetrievalJson.Options, cancellationToken))!;
    }

    public async Task CancelFetchAsync(string jobId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"v1/fetch/{Uri.EscapeDataString(jobId)}", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureAsync(response, cancellationToken);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        RetrievalError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<RetrievalError>(RetrievalJson.Options, ct);
        }
        catch (JsonException)
        {
        }
        throw new RetrievalException(error?.Error ?? $"The plugin answered {(int)response.StatusCode} {response.ReasonPhrase}", error?.Code, response.StatusCode);
    }
}

/// <summary>A retrieval plugin said no, with its reason.</summary>
public sealed class RetrievalException(string message, string? code, HttpStatusCode status) : Exception(message)
{
    public string? Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The retrieval plugins the user set up, in fallback order.</summary>
public sealed class RetrievalPlugins(MelodexDbContext db, IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "retrieval";

    /// <summary>A configured instance and its manifest capabilities, as last read from the container.</summary>
    public sealed record Entry(PluginInstance Instance, IRetrievalPlugin Plugin, IReadOnlyList<string> Capabilities);

    public async Task<IReadOnlyList<Entry>> GetChainAsync(CancellationToken ct)
    {
        var instances = await db.PluginInstances.Where(p => p.Type == PluginType.Retrieval && p.Enabled && p.Endpoint != null)
            .OrderBy(p => p.Priority).ThenBy(p => p.CreatedAt).ToListAsync(ct);
        return instances.Select(i => new Entry(i, Create(i), Capabilities(i))).ToList();
    }

    public IRetrievalPlugin Create(PluginInstance instance) =>
        new RetrievalClient(Client(instance.Endpoint!), JsonDocument.Parse(instance.SettingsJson).RootElement.Clone());

    public HttpClient Client(string endpoint)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        return http;
    }

    /// <summary>Capabilities are stored with the instance when it's added or its manifest is refreshed.</summary>
    public static IReadOnlyList<string> Capabilities(PluginInstance instance) =>
        instance.ManifestJson is null ? [] : JsonSerializer.Deserialize<PluginManifest>(instance.ManifestJson, RetrievalJson.Options)?.Capabilities ?? [];
}
