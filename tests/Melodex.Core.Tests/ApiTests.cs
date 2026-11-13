using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Melodex.Core.Api;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

public sealed class MelodexFactory : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Melodex:DataPath", _dataPath);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }
}

public class ApiTests(MelodexFactory factory) : IClassFixture<MelodexFactory>
{
    private readonly HttpClient _client = factory.CreateSignedInClient();

    [Fact]
    public async Task Health_reports_healthy_with_a_migrated_database()
    {
        var health = await _client.GetFromJsonAsync<HealthResponse>("/api/v1/health");

        Assert.Equal("healthy", health!.Status);
        Assert.Equal("healthy", health.Checks["database"]);
    }

    [Fact]
    public async Task Version_lists_every_contract()
    {
        var version = await _client.GetFromJsonAsync<VersionResponse>("/api/v1/version");

        Assert.Equal("v1", version!.ApiVersion);
        Assert.Equal(["connector", "index", "retrieval"], version.Contracts.Keys.Order());
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_the_v1_api()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.1", document.RootElement.GetProperty("openapi").GetString());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/health", out _));
    }
}
