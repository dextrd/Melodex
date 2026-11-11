using System.Text.Json;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

public static class LibrarySettingsExtensions
{
    /// <summary>How the missing view treats this album when it isn't owned, by the strictest of its kinds.</summary>
    public static ReleaseShowing ShowingOf(this LibrarySettings settings, Album album) =>
        ReleaseKinds.Of(album.Type, album.SecondaryTypes, album.HasOfficialRelease).Select(k => settings.ShowingOf(album.ArtistId, k)).DefaultIfEmpty().Max();
}

public sealed class SettingsService(MelodexDbContext db)
{
    private const string LibraryKey = "library";
    private const string FetchKey = "fetch";
    private const string LogKey = "log";
    private const string ArtistShowingKey = "artistShowing";
    private const string QualityKey = "quality";
    private const string UpgradesKey = "upgrades";
    private const string UpgradeRunKey = "upgrades.lastRun";

    /// <summary>The library's settings, with the artists that have their own choices for the missing view.</summary>
    public async Task<LibrarySettings> GetLibraryAsync(CancellationToken ct)
    {
        var artists = await GetAsync<Dictionary<Guid, Dictionary<ReleaseKind, ReleaseShowing>>>(ArtistShowingKey, ct);
        return await GetAsync<LibrarySettings>(LibraryKey, ct) with
        {
            ArtistShowing = artists.ToDictionary(a => a.Key, a => (IReadOnlyDictionary<ReleaseKind, ReleaseShowing>)a.Value),
        };
    }

    /// <summary>An artist's own choices for the missing view, every kind written out; null to follow the library's again.</summary>
    public async Task SaveArtistShowingAsync(Guid artistId, IReadOnlyDictionary<ReleaseKind, ReleaseShowing>? showing, CancellationToken ct)
    {
        var artists = await GetAsync<Dictionary<Guid, Dictionary<ReleaseKind, ReleaseShowing>>>(ArtistShowingKey, ct);
        if (showing is null)
            artists.Remove(artistId);
        else
            artists[artistId] = Enum.GetValues<ReleaseKind>().ToDictionary(k => k, k => showing.TryGetValue(k, out var s) ? s : ReleaseKinds.Default(k));
        await SaveAsync(ArtistShowingKey, artists, ct);
    }

    public Task SaveLibraryAsync(LibrarySettings value, CancellationToken ct) => SaveAsync(LibraryKey, value, ct);

    public Task<FetchSettings> GetFetchAsync(CancellationToken ct) => GetAsync<FetchSettings>(FetchKey, ct);

    public Task SaveFetchAsync(FetchSettings value, CancellationToken ct) => SaveAsync(FetchKey, value, ct);

    public Task<QualityProfile> GetQualityAsync(CancellationToken ct) => GetAsync<QualityProfile>(QualityKey, ct);

    /// <summary>Saves the profile with every step in it, in order.</summary>
    public Task SaveQualityAsync(QualityProfile value, CancellationToken ct) =>
        SaveAsync(QualityKey, value with { Steps = value.Ordered().Select(s => new ProfileStep(s.Step.Key, s.Allowed)).ToList() }, ct);

    public Task<UpgradeSettings> GetUpgradesAsync(CancellationToken ct) => GetAsync<UpgradeSettings>(UpgradesKey, ct);

    public Task SaveUpgradesAsync(UpgradeSettings value, CancellationToken ct) => SaveAsync(UpgradesKey, value, ct);

    /// <summary>When the weekly look for upgrades last ran, or was first seen on.</summary>
    public async Task<DateTime?> GetUpgradeRunAsync(CancellationToken ct) => (await GetAsync<UpgradeRun>(UpgradeRunKey, ct)).At;

    public Task SaveUpgradeRunAsync(DateTime at, CancellationToken ct) => SaveAsync(UpgradeRunKey, new UpgradeRun { At = at }, ct);

    private sealed record UpgradeRun
    {
        public DateTime? At { get; init; }
    }

    public Task<LogSettings> GetLogAsync(CancellationToken ct) => GetAsync<LogSettings>(LogKey, ct);

    public Task SaveLogAsync(LogSettings value, CancellationToken ct) => SaveAsync(LogKey, value, ct);

    private async Task<T> GetAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.Settings.FindAsync([key], ct);
        return row is null ? new T() : JsonSerializer.Deserialize<T>(row.ValueJson, MelodexJson.Options) ?? new T();
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, MelodexJson.Options);
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        else
            row.ValueJson = json;
        await db.SaveChangesAsync(ct);
    }
}
