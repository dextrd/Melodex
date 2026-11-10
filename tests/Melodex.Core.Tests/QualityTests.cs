using Melodex.Client;

namespace Melodex.Core.Tests;

/// <summary>The scale files' qualities are ranked on, and how they're written (#17).</summary>
public sealed class QualityTests
{
    [Theory]
    [InlineData("flac", null, 16, 44100, QualityLevel.Lossless, "FLAC 16/44.1")]
    [InlineData("flac", null, 24, 96000, QualityLevel.HiRes, "FLAC 24/96")]
    [InlineData("alac", null, 16, 88200, QualityLevel.HiRes, "ALAC 16/88.2")]
    [InlineData("mp3", 320, null, 44100, QualityLevel.High, "MP3 320")]
    [InlineData("mp3", 245, null, 44100, QualityLevel.Good, "MP3 245")]
    [InlineData("mp3", 192, null, 44100, QualityLevel.Fair, "MP3 192")]
    [InlineData("mp3", 128, null, 44100, QualityLevel.Low, "MP3 128")]
    [InlineData("opus", 160, null, 48000, QualityLevel.Good, "Opus 160")]
    [InlineData("aac", 256, null, 44100, QualityLevel.High, "AAC 256")]
    [InlineData("vorbis", 96, null, 44100, QualityLevel.Low, "Vorbis 96")]
    [InlineData(null, null, null, null, QualityLevel.Unknown, "Unknown")]
    public void Files_are_ranked_and_written_as_they_usually_are(string? codec, int? kbps, int? bits, int? rate, QualityLevel level, string label)
    {
        var quality = new FileQuality(codec, kbps, bits, rate);
        Assert.Equal(level, quality.Level);
        Assert.Equal(label, quality.Label);
    }

    [Fact]
    public void An_album_is_as_good_as_its_worst_song()
    {
        var songs = new[] { new FileQuality("flac", null, 24, 96000), new FileQuality("mp3", 320, null, 44100), new FileQuality("flac", null, 16, 44100) };
        Assert.Equal("MP3 320", FileQuality.Lowest(songs)!.Label);
        Assert.True(FileQuality.Mixed(songs));
        Assert.False(FileQuality.Mixed([songs[2], songs[2] with { }]));
        Assert.Null(FileQuality.Lowest([]));
    }

    [Fact]
    public void Better_files_rank_higher_within_a_level_too()
    {
        Assert.True(new FileQuality("flac", null, 24, 192000).Rank > new FileQuality("flac", null, 24, 96000).Rank);
        Assert.True(new FileQuality("mp3", 320, null, null).Rank > new FileQuality("aac", 128, null, null).Rank);
        Assert.True(new FileQuality("flac", null, 16, 44100).Rank > new FileQuality("mp3", 320, null, null).Rank);
    }

    [Theory]
    [InlineData(QualityFilter.Lossless, "flac", 16, true)]
    [InlineData(QualityFilter.Lossless, "mp3", null, false)]
    [InlineData(QualityFilter.HiRes, "flac", 16, false)]
    [InlineData(QualityFilter.Lossy, "mp3", null, true)]
    [InlineData(QualityFilter.Lossy, null, null, false)]
    [InlineData(QualityFilter.BelowGood, "mp3", null, true)]
    [InlineData(QualityFilter.Any, null, null, true)]
    public void The_library_filters_by_quality(QualityFilter filter, string? codec, int? bits, bool shown) =>
        Assert.Equal(shown, filter.Shows(codec is null ? null : new FileQuality(codec, codec == "mp3" ? 128 : null, bits, 44100)));
}
