using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Contracts.Plugins;
using Melodex.Core.Catalogue;
using Melodex.Core.Library;
using Melodex.Core.Plugins;
using Melodex.Plugins.MusicBrainz;

namespace Melodex.Core.Tests;

public class TextMatchTests
{
    [Theory]
    [InlineData("Doin’ It Right", "doin it right")]
    [InlineData("Björk", "bjork")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    [InlineData("  Harder,  Better, Faster!  ", "harder better faster")]
    public void Normalise_folds_case_accents_and_punctuation(string text, string expected) =>
        Assert.Equal(expected, TextMatch.Normalise(text));

    [Theory]
    [InlineData("Glory Box (Album Version)", "glory box")]
    [InlineData("Roads [2014 Remaster]", "roads")]
    [InlineData("One More Time (Live)", "one more time live")]
    [InlineData("Around the World (Radio Edit)", "around the world radio edit")]
    [InlineData("Red Moon - 2020 Remastered Version", "red moon")]
    [InlineData("Self- - 2020 Remastered Version", "self")]
    [InlineData("How Does It Feel - 2016 Remaster", "how does it feel")]
    [InlineData("Requiem - Single", "requiem")]
    [InlineData("Punkrocker - As featured in Superman", "punkrocker")]
    [InlineData("To The Stars - Dragonheart/Soundtrack Version", "to the stars")]
    [InlineData("Holy the Sea (feat. John Gourley and Kris Anaya)", "holy the sea")]
    [InlineData("Sugar Song and Bitter Step (From \"Kekkai Sensen\")", "sugar song and bitter step")]
    [InlineData("Goodnight Sweet Possums - From \"Ice Age The Meltdown\"", "goodnight sweet possums")]
    [InlineData("Firefly - Acoustic", "firefly acoustic")]
    [InlineData("Blue Stone of Atlas - Piano Solo Version", "blue stone of atlas piano solo version")]
    [InlineData("Cicada Days - Live at Knitting Factory, Spokane, WA", "cicada days live at knitting factory spokane wa")]
    [InlineData("Undertale - Fallen Down", "undertale fallen down")]
    public void Titles_lose_qualifiers_that_keep_the_recording_but_keep_ones_that_change_it(string title, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseTitle(title));

    [Theory]
    [InlineData("Cicada Days - Live at Knitting Factory, Spokane, WA", "cicada days")]
    [InlineData("Half-Decade Hangover - Live at City Winery, Atlanta, GA", "half decade hangover")]
    [InlineData("That's Enough, Let's Get You Home - Live in Studio at Backroom Studios, Rockaway NJ", "thats enough lets get you home")]
    public void On_a_live_album_titles_lose_where_they_were_played(string title, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseTitle(title, live: true));

    [Theory]
    [InlineData("Random Access Memories (10th Anniversary Edition)", "random access memories")]
    [InlineData("Dummy [FLAC]", "dummy")]
    [InlineData("Homework (1997)", "homework")]
    [InlineData("Requiem - Single", "requiem")]
    [InlineData("Notes - EP", "notes")]
    public void Album_titles_lose_edition_and_format_tags(string album, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseAlbum(album));

    [Theory]
    [InlineData("Daft Punk feat. Pharrell Williams", "daft punk")]
    [InlineData("Daft Punk ft. Julian Casablancas", "daft punk")]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Bob Marley & The Wailers", "bob marley and the wailers")]
    public void Artists_lose_featured_artists_and_a_leading_the(string artist, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseArtist(artist));

    [Theory]
    [InlineData("Gerard Way; Lydia Night", "Gerard Way & Lydia Night")]
    [InlineData("Illuminine; Dominique Charpentier", "Illuminine")]
    [InlineData("Unknown; La Nef; Sean Dagher", "La Nef, Seán Dagher")]
    public void An_artist_in_a_tag_of_several_fits_a_credit(string tag, string credit) =>
        Assert.Equal(1, TextMatch.ArtistSimilarity(tag, credit), 3);

    [Fact]
    public void Different_artists_stay_apart() =>
        Assert.True(TextMatch.ArtistSimilarity("The Covering Music; Jake Spencer", "Grant Gustin") < 0.5);

    [Theory]
    [InlineData("Iwasaki, Taku", "taku iwasaki")]
    [InlineData("Beatles, The", "beatles")]
    [InlineData("Portishead", "portishead")]
    public void Sort_names_read_as_names(string sortName, string expected) =>
        Assert.Equal(expected, TextMatch.SortNameAsName(sortName));

    [Fact]
    public void Similarity_ignores_word_order()
    {
        Assert.Equal(1, TextMatch.Similarity("box glory", "glory box"));
        Assert.Equal(0, TextMatch.Similarity("", "glory box"));
        Assert.InRange(TextMatch.Similarity("glory box", "glory bok"), 0.85, 0.95);
    }
}

public class FuzzyScoreTests
{
    private static readonly FileTags Tags = new() { Title = "Glory Box", Artist = "Portishead" };

    private static double Score(double fileSeconds, double? listingSeconds, string title = "Glory Box", string artist = "Portishead") =>
        LibraryMatcher.Fuzzy(Tags, TimeSpan.FromSeconds(fileSeconds), title, title, artist,
            listingSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void Same_title_artist_and_length_is_a_sure_match() => Assert.Equal(1, Score(306, 305), 3);

    [Fact]
    public void Lengths_a_few_seconds_apart_still_match() => Assert.True(Score(306, 309) >= 0.99);

    [Fact]
    public void A_length_over_20_seconds_out_never_matches() => Assert.True(Score(381, 306) <= 0.6);

    [Fact]
    public void An_unknown_length_stays_under_the_default_threshold() => Assert.True(Score(306, null) < 0.85);

    [Fact]
    public void A_different_title_never_matches_even_with_the_right_length() =>
        Assert.True(Score(306, 306, title: "Knockin’ On Glory Box (Portishead vs. Guns N’ Roses)") <= 0.5);

    [Fact]
    public void A_featured_artist_credit_doesnt_count_against_a_match() =>
        Assert.Equal(1, Score(306, 306, artist: "Portishead feat. Someone"), 3);

    [Fact]
    public void The_same_song_by_another_artist_never_matches() =>
        Assert.True(LibraryMatcher.Fuzzy(new FileTags { Title = "Runnin' Home To You", Artist = "The Covering Music; Jake Spencer" },
            TimeSpan.FromSeconds(161), "Runnin’ Home to You", "Runnin’ Home to You", "Grant Gustin", TimeSpan.FromSeconds(166)) <= 0.5);

    [Fact]
    public void The_artist_the_tags_resolved_to_counts_whatever_script_its_credit_is_in() =>
        Assert.True(LibraryMatcher.Fuzzy(new FileTags { Title = "Il mare eterno nella mia anima", Artist = "Taku Iwasaki" },
            TimeSpan.FromSeconds(189), "Il mare eterno nella mia anima", "Il mare eterno nella mia anima", "岩崎琢", TimeSpan.FromSeconds(194), sameArtist: true) >= 0.85);

    [Fact]
    public void A_streaming_remaster_tag_matches_the_original_title() =>
        Assert.Equal(1, LibraryMatcher.Fuzzy(new FileTags { Title = "Red Moon - 2020 Remastered Version", Artist = "Will Wood and the Tapeworms" },
            TimeSpan.FromSeconds(248), "Red Moon", "Red Moon", "Will Wood and the Tapeworms", TimeSpan.FromSeconds(248)), 3);

    [Fact]
    public void A_venue_matches_on_a_live_album_only()
    {
        var tags = new FileTags { Title = "Cicada Days - Live at Knitting Factory, Spokane, WA", Artist = "Will Wood" };
        double Live(bool live) => LibraryMatcher.Fuzzy(tags, TimeSpan.FromSeconds(277), "Cicada Days", "Cicada Days", "Will Wood", TimeSpan.FromSeconds(277), liveAlbum: live);
        Assert.Equal(1, Live(true), 3);
        Assert.True(Live(false) < 0.85);
    }
}

public class PluginCatalogTests
{
    private static PluginCatalog Catalog(string pluginsPath) =>
        new(Options.Create(new MelodexOptions { PluginsPath = pluginsPath }), NullLogger<PluginCatalog>.Instance);

    [Fact]
    public void The_built_in_musicbrainz_index_is_found_and_usable()
    {
        var plugin = Catalog(Path.Combine(Path.GetTempPath(), "melodex-no-plugins")).Find(MusicBrainzIndexFactory.PluginId);

        Assert.NotNull(plugin);
        Assert.Equal("built-in", plugin.Source);
        Assert.Equal(PluginType.Index, plugin.Manifest.Type);
        Assert.Null(plugin.Rejection);
        Assert.NotNull(plugin.Factory);
    }

    [Fact]
    public void Files_in_the_plugins_folder_that_arent_plugins_are_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "junk"));
        File.WriteAllText(Path.Combine(root, "junk", "notes.dll"), "not an assembly");
        try
        {
            Assert.Equal(
            [
                Melodex.Plugins.MusicBrainz.CoverArtArchiveFactory.PluginId, MusicBrainzIndexFactory.PluginId,
                Melodex.Plugins.Navidrome.NavidromeArtworkFactory.PluginId, Melodex.Plugins.Navidrome.NavidromeConnectorFactory.PluginId,
                Melodex.Plugins.FanartTv.FanartTvFactory.PluginId, Melodex.Plugins.Wikimedia.WikimediaCommonsFactory.PluginId,
                Melodex.Plugins.Deezer.DeezerFactory.PluginId, Melodex.Plugins.Lrclib.LrclibFactory.PluginId,
            ], Catalog(root).All.Select(p => p.Manifest.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
