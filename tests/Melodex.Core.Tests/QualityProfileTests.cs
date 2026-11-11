using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Retrieval;
using Melodex.Core.Data;
using Melodex.Core.Requests;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Quality profiles: the qualities wanted, in order, and what's good enough (#18).</summary>
public sealed class QualityProfileTests
{
    private static readonly FileQuality HiResFlac = new("flac", null, 24, 96000);
    private static readonly FileQuality CdFlac = new("flac", null, 16, 44100);
    private static readonly FileQuality Mp3_320 = new("mp3", 320, null, 44100);
    private static readonly FileQuality Opus160 = new("opus", 160, null, 48000);
    private static readonly FileQuality Mp3_128 = new("mp3", 128, null, 44100);

    [Fact]
    public void The_default_wants_lossless_and_good_lossy_and_is_content_with_cd_flac()
    {
        var profile = new QualityProfile();
        Assert.Equal(QualityStep.All.Count, profile.Ordered().Count);
        Assert.True(profile.Allows(HiResFlac) && profile.Allows(Opus160));
        Assert.False(profile.Allows(Mp3_128));
        Assert.True(profile.Allows(null));
        Assert.True(profile.Meets(HiResFlac) && profile.Meets(CdFlac));
        Assert.False(profile.Meets(Mp3_320));
        Assert.True(profile.Meets(Mp3_320, new QualityStep(CodecFamily.Mp3, QualityLevel.High).Key));
    }

    [Fact]
    public void Steps_a_saved_profile_doesnt_name_go_in_after_their_default_neighbour()
    {
        // Saved before ALAC was a step of its own, with MP3 high moved to the top and FLAC hi-res turned off.
        var mp3High = new QualityStep(CodecFamily.Mp3, QualityLevel.High);
        var saved = QualityStep.All.Where(s => s.Codec != CodecFamily.Alac && s != mp3High)
            .Select(s => new ProfileStep(s.Key, s != new QualityStep(CodecFamily.Flac, QualityLevel.HiRes))).Prepend(new ProfileStep(mp3High.Key, true))
            .Append(new ProfileStep("made-up", true)).ToList();
        var ordered = new QualityProfile { Steps = saved }.Ordered().Select(s => s.Step).ToList();

        Assert.Equal(QualityStep.All.Count, ordered.Count);
        Assert.Equal(mp3High, ordered[0]);
        Assert.Equal([new(CodecFamily.Flac, QualityLevel.HiRes), new(CodecFamily.Alac, QualityLevel.HiRes), new(CodecFamily.Uncompressed, QualityLevel.HiRes)], ordered.Skip(1).Take(3));
        var cd = ordered.IndexOf(new QualityStep(CodecFamily.Flac, QualityLevel.Lossless));
        Assert.Equal(new QualityStep(CodecFamily.Alac, QualityLevel.Lossless), ordered[cd + 1]);
        Assert.False(new QualityProfile { Steps = saved }.Ordered()[1].Allowed);
    }

    [Fact]
    public void A_likelier_match_beats_a_better_quality_and_among_sure_ones_quality_decides()
    {
        var candidates = new[]
        {
            Of("sure mp3", "mp3", 320, 0.95),
            Of("sure flac", "flac", null, 0.85),
            Of("unsure hi-res", "flac", null, 0.7, bits: 24),
            Of("sure low", "mp3", 128, 0.99),
            Of("sure unknown", null, null, 0.9),
        };
        var order = CandidateRanking.Order(candidates.Select(c => (c.Title, c)), new QualityProfile(), false, _ => 0).Select(x => x.Plugin);
        // MP3 128 is turned off; one of unknown quality is tried after the known ones, to be read once it's here.
        Assert.Equal(["sure flac", "sure mp3", "sure unknown", "unsure hi-res"], order);
    }

    [Fact]
    public void Listen_now_takes_the_best_at_or_under_its_target_or_else_the_smallest_over_it()
    {
        var profile = new QualityProfile { ListenTarget = new QualityStep(CodecFamily.Opus, QualityLevel.Good).Key };
        var order = CandidateRanking.Order(new[]
        {
            Of("flac", "flac", null, 0.9), Of("mp3 320", "mp3", 320, 0.9), Of("opus 160", "opus", 160, 0.9), Of("mp3 128", "mp3", 128, 0.9),
        }.Select(c => (c.Title, c)), profile, true, _ => 0).Select(x => x.Plugin);
        Assert.Equal(["opus 160", "mp3 128", "mp3 320", "flac"], order);
    }

    [Theory]
    [InlineData(44100, 16570, QualityLevel.Low)]
    [InlineData(44100, 17400, QualityLevel.Fair)]
    [InlineData(44100, 18700, QualityLevel.Fair)]
    [InlineData(44100, 19400, QualityLevel.Good)]
    [InlineData(44100, 20150, QualityLevel.High)]
    [InlineData(96000, 22050, QualityLevel.Lossless)]
    public void Lossless_cut_off_low_is_suspect_and_counts_as_what_it_seems(int rate, int cutoff, QualityLevel seems)
    {
        var quality = new FileQuality("flac", null, rate > 48000 ? 24 : 16, rate, cutoff);
        Assert.True(quality.Suspect);
        Assert.Equal(seems, quality.Level);
        Assert.Equal(seems == QualityLevel.Lossless ? new QualityStep(CodecFamily.Flac, QualityLevel.Lossless) : new QualityStep(CodecFamily.Mp3, seems),
            QualityStep.Of(quality));
        Assert.False(new QualityProfile().Meets(quality) && seems < QualityLevel.Lossless);
    }

    [Fact]
    public void A_cd_cut_off_where_a_cd_is_and_lossy_files_are_never_suspect()
    {
        Assert.False(new FileQuality("flac", null, 16, 44100, 21500).Suspect);
        Assert.Equal(QualityLevel.Lossless, new FileQuality("flac", null, 16, 44100, 21500).Level);
        Assert.False(new FileQuality("flac", null, 24, 96000, 40000).Suspect);
        Assert.False(new FileQuality("mp3", 320, null, 44100, 16000).Suspect);
        Assert.True(FileQuality.Mixed([new FileQuality("flac", null, 16, 44100), new FileQuality("flac", null, 16, 44100, 16000)]));
    }

    [Fact]
    public void Of_candidates_otherwise_alike_one_vouched_for_comes_first()
    {
        var plain = Of("plain", "flac", null, 0.95);
        var vouched = Of("vouched", "flac", null, 0.9) with { Vouched = true };
        var order = CandidateRanking.Order(new[] { plain, vouched, Of("mp3", "mp3", 320, 0.99) with { Vouched = true } }.Select(c => (c.Title, c)),
            new QualityProfile(), false, _ => 0).Select(x => x.Plugin);
        Assert.Equal(["vouched", "plain", "mp3"], order);
    }

    [Theory]
    [InlineData("m4a", "aac")]
    [InlineData("ogg", "vorbis")]
    [InlineData("FLAC", "flac")]
    public void A_candidates_format_is_read_as_its_codec(string format, string codec) =>
        Assert.Equal(codec, CandidateRanking.QualityOf(Of("x", format, 256, 0.9)).Codec);

    [Fact]
    public async Task The_narrowest_setting_wins_and_says_where_it_comes_from()
    {
        var root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Melodex:DataPath", Path.Combine(root, "data")).UseSetting("Melodex:LibraryPath", Path.Combine(root, "music")));
        var api = new MelodexClient(factory.CreateSignedInClient());
        Guid artistId, albumId, trackId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var artist = new Artist { Name = "Massive Attack" };
            var album = new Album { Title = "Mezzanine", Artist = artist, ArtistCredit = "Massive Attack" };
            var track = new Track { Title = "Teardrop", Artist = artist, ArtistCredit = "Massive Attack" };
            db.AddRange(artist, album, track);
            await db.SaveChangesAsync();
            (artistId, albumId, trackId) = (artist.Id, album.Id, track.Id);
        }

        var flacCd = new QualityStep(CodecFamily.Flac, QualityLevel.Lossless).Key;
        var mp3High = new QualityStep(CodecFamily.Mp3, QualityLevel.High).Key;
        var song = (await api.GetQualitySettingAsync("tracks", trackId, albumId))!;
        Assert.Equal((QualityOverrideKind.Inherit, QualityOverrideKind.Cutoff, flacCd, "the main profile"),
            (song.Kind, song.Applies.Kind, song.Applies.Cutoff, song.Applies.From));

        await api.SaveQualityOverrideAsync("artists", artistId, new QualityOverride(QualityOverrideKind.Cutoff, mp3High));
        song = (await api.GetQualitySettingAsync("tracks", trackId, albumId))!;
        Assert.Equal((mp3High, "Massive Attack"), (song.Applies.Cutoff, song.Applies.From));

        await api.SaveQualityOverrideAsync("albums", albumId, new QualityOverride(QualityOverrideKind.NeverUpgrade));
        song = (await api.GetQualitySettingAsync("tracks", trackId, albumId))!;
        Assert.Equal((QualityOverrideKind.NeverUpgrade, "the album Mezzanine"), (song.Applies.Kind, song.Applies.From));

        await api.SaveQualityOverrideAsync("tracks", trackId, new QualityOverride(QualityOverrideKind.Cutoff, flacCd));
        song = (await api.GetQualitySettingAsync("tracks", trackId, albumId))!;
        Assert.Equal((QualityOverrideKind.Cutoff, flacCd, "this song"), (song.Kind, song.Applies.Cutoff, song.Applies.From));
        Assert.Equal((QualityOverrideKind.NeverUpgrade, "the album Mezzanine"), (song.Inherited.Kind, song.Inherited.From));

        await Assert.ThrowsAsync<MelodexApiException>(() => api.SaveQualityOverrideAsync("tracks", trackId, new QualityOverride(QualityOverrideKind.Cutoff, "made-up")));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_profile_must_want_its_cutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Melodex:DataPath", Path.Combine(root, "data")).UseSetting("Melodex:LibraryPath", Path.Combine(root, "music")));
        var api = new MelodexClient(factory.CreateSignedInClient());
        var profile = await api.GetQualityProfileAsync();
        var off = profile with { Steps = profile.Ordered().Select(s => new ProfileStep(s.Step.Key, s.Step.Key != profile.Cutoff)).ToList() };
        await Assert.ThrowsAsync<MelodexApiException>(() => api.SaveQualityProfileAsync(off));

        var reordered = profile with { Steps = profile.Ordered().Reverse().Select(s => new ProfileStep(s.Step.Key, true)).ToList(), Cutoff = profile.ListenTarget };
        await api.SaveQualityProfileAsync(reordered);
        var saved = await api.GetQualityProfileAsync();
        Assert.Equal(reordered.Ordered(), saved.Ordered());
        Assert.Equal(profile.ListenTarget, saved.Cutoff);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static Candidate Of(string title, string? format, int? kbps, double score, int? bits = null) =>
        new(title, RetrievalKind.Track, title, null, null, null, "test", null, format, kbps, score, bits);
}
