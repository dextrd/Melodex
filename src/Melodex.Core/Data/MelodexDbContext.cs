using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Melodex.Core.Data;

public partial class MelodexDbContext(DbContextOptions<MelodexDbContext> options) : DbContext(options)
{
    /// <summary>
    /// SQLite options for every context. A list in a query (<c>ids.Contains(x)</c>) goes as one JSON parameter: by
    /// default EF Core makes a parameter per item, and SQLite refuses more than 32,766, which a big library passes.
    /// </summary>
    public static void Sqlite(SqliteDbContextOptionsBuilder sqlite) =>
        sqlite.UseParameterizedCollectionMode(ParameterTranslationMode.Parameter);

    private static readonly TimeSpan WriteTurn = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Brings SQLite's statistics on the tables up to date where they're missing or have grown stale. Without them it can
    /// pick an index that reads a whole table where another would read a few rows, which on a big catalogue takes seconds
    /// a query. Each index is sampled on its own, so writes wait only a moment at a time.
    /// </summary>
    public async Task OptimiseAsync(CancellationToken ct = default)
    {
        var connection = Database.GetDbConnection();
        await Database.OpenConnectionAsync(ct);
        try
        {
            // Asked what it would analyse, across every table (0x10000), rather than letting it (0x01), which would take
            // every table at once.
            var tables = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA optimize=0x10003;";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    if (OptimiseTable().Match(reader.GetString(0)) is { Success: true } match)
                        tables.Add(match.Groups[1].Value);
            }
            foreach (var table in tables)
            {
                var indexes = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table;";
                    command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$table", table));
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        indexes.Add(reader.GetString(0));
                }
                foreach (var name in indexes.Count > 0 ? indexes : [table])
                {
                    // A pause between each, so writes waiting their turn get it: back to back, they'd seldom see it free.
                    await Task.Delay(WriteTurn, ct);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"PRAGMA analysis_limit=400; ANALYZE \"{name.Replace("\"", "\"\"")}\";";
                    await command.ExecuteNonQueryAsync(ct);
                }
            }
        }
        finally
        {
            await Database.CloseConnectionAsync();
        }
        // Each connection reads the statistics when it opens, so the pooled ones are let go to take up the new ones.
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)connection);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^ANALYZE \"main\"\\.\"(.+)\"$")]
    private static partial System.Text.RegularExpressions.Regex OptimiseTable();

    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<EditionTrack> EditionTracks => Set<EditionTrack>();
    public DbSet<ProviderIdMapping> Crosswalk => Set<ProviderIdMapping>();
    public DbSet<LibraryFile> LibraryFiles => Set<LibraryFile>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<ListenNowItem> ListenNowItems => Set<ListenNowItem>();
    public DbSet<PluginInstance> PluginInstances => Set<PluginInstance>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<BackgroundTask> BackgroundTasks => Set<BackgroundTask>();
    public DbSet<ArtworkRecord> Artwork => Set<ArtworkRecord>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();
    public DbSet<PlaylistFolder> PlaylistFolders => Set<PlaylistFolder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Artist>(e =>
        {
            e.HasIndex(a => a.Name);
        });

        modelBuilder.Entity<Album>(e =>
        {
            e.HasOne(a => a.Artist).WithMany(a => a.Albums).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => a.Title);
        });

        modelBuilder.Entity<Edition>(e =>
        {
            e.HasOne(ed => ed.Album).WithMany(a => a.Editions).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Track>(e =>
        {
            e.HasOne(t => t.Artist).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.Title);
            // Every page asks which tracks are ignored, and they're few among the catalogue's many.
            e.HasIndex(t => t.Ignored).HasFilter("\"Ignored\"");
        });

        modelBuilder.Entity<EditionTrack>(e =>
        {
            e.HasKey(et => new { et.EditionId, et.Disc, et.Position });
            e.HasOne(et => et.Edition).WithMany(ed => ed.Tracks).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(et => et.Track).WithMany(t => t.Appearances).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(et => et.TrackId);
        });

        modelBuilder.Entity<ProviderIdMapping>(e =>
        {
            e.ToTable("Crosswalk");
            // A provider ID identifies exactly one entity of a kind; an entity can have many provider IDs.
            // ISRCs and UPCs are the exception, as catalogues sometimes share them between entities.
            e.HasIndex(m => new { m.EntityKind, m.Provider, m.Value })
                .IsUnique()
                .HasFilter("\"Provider\" NOT IN ('isrc', 'upc')")
                .HasDatabaseName("IX_Crosswalk_ProviderId");
            e.HasIndex(m => new { m.Provider, m.Value });
            e.HasIndex(m => new { m.EntityKind, m.EntityId });
        });

        modelBuilder.Entity<LibraryFile>(e =>
        {
            e.HasIndex(f => f.Path).IsUnique();
            e.HasIndex(f => f.TrackId);
            e.HasIndex(f => f.EditionId);
            e.HasIndex(f => f.ArtistId);
            e.HasIndex(f => f.MatchedAt);
            e.HasOne(f => f.Track).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(f => f.Edition).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Request>(e =>
        {
            e.HasIndex(r => new { r.TargetKind, r.TargetId, r.Mode });
            e.HasIndex(r => r.Status);
            e.HasOne<Request>().WithMany().HasForeignKey(r => r.ParentRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasOne(j => j.Request).WithMany(r => r.Jobs).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(j => j.PluginInstance).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(j => j.Status);
        });

        modelBuilder.Entity<ListenNowItem>(e =>
        {
            e.HasOne(l => l.Request).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Track).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.State, l.ExpiresAt });
        });

        modelBuilder.Entity<PluginInstance>(e =>
        {
            e.HasIndex(p => new { p.Type, p.Priority });
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
        });

        modelBuilder.Entity<ArtworkRecord>(e =>
        {
            e.ToTable("Artwork");
            e.HasKey(a => new { a.EntityKind, a.EntityId });
        });

        modelBuilder.Entity<BackgroundTask>(e =>
        {
            e.HasIndex(t => new { t.Status, t.CreatedAt });
        });

        modelBuilder.Entity<Playlist>(e =>
        {
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.UserId);
        });

        modelBuilder.Entity<PlaylistFolder>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => new { f.UserId, f.Path }).IsUnique();
        });

        modelBuilder.Entity<PlaylistEntry>(e =>
        {
            e.HasOne(pe => pe.Playlist).WithMany(p => p.Entries).HasForeignKey(pe => pe.PlaylistId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(pe => new { pe.PlaylistId, pe.Position });
            e.HasIndex(pe => pe.TrackId);
            e.HasIndex(pe => pe.FileId);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.Hash).IsUnique();
            e.HasOne(k => k.User).WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.NormalizedUsername).IsUnique();
        });

        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.HasOne(d => d.Webhook).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.Status, d.NextAttemptAt });
            e.HasIndex(d => new { d.WebhookId, d.CreatedAt });
        });

        // Store enums as their names so the database stays readable and reordering members is safe.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (type.IsEnum)
                property.SetProviderClrType(typeof(string));
        }
    }
}
