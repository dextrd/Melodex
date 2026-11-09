using Melodex.Client;

namespace Melodex.Core.Listening;

/// <summary>Media types for audio files, so players can check they can play a file before trying.</summary>
public static class MediaTypes
{
    /// <summary>The usual codec for a file extension, when nothing better is known.</summary>
    public static string? CodecFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".opus" => "opus",
        ".ogg" or ".oga" => "vorbis",
        ".mp3" => "mp3",
        ".flac" => "flac",
        ".m4a" or ".mp4" or ".aac" => "aac",
        ".webm" => "opus",
        ".wav" => "pcm",
        ".wma" => "wma",
        ".aif" or ".aiff" => "aiff",
        ".ape" => "ape",
        ".wv" => "wavpack",
        ".mpc" => "mpc",
        ".dsf" => "dsd",
        _ => null,
    };

    /// <summary>The container, from the extension: <c>ogg</c>, <c>mp4</c>, <c>flac</c> and so on.</summary>
    public static string? ContainerFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".opus" or ".ogg" or ".oga" => "ogg",
        ".m4a" or ".mp4" or ".aac" => "mp4",
        ".mp3" => "mp3",
        ".flac" => "flac",
        ".webm" => "webm",
        ".wav" => "wav",
        ".wma" => "asf",
        _ => null,
    };

    /// <summary>
    /// The media type to serve a file as, with the codec when the container can hold several, such as
    /// <c>audio/ogg; codecs=opus</c>. Browsers answer <c>canPlayType</c> for exactly this string.
    /// </summary>
    public static string For(string path, string? codec = null)
    {
        codec ??= CodecFor(path);
        return ContainerFor(path) switch
        {
            "ogg" => codec is null ? "audio/ogg" : $"audio/ogg; codecs={codec}",
            "webm" => codec is null ? "audio/webm" : $"audio/webm; codecs={codec}",
            "mp4" => codec switch
            {
                "alac" => "audio/mp4; codecs=alac",
                "aac" or null => "audio/mp4; codecs=mp4a.40.2",
                _ => $"audio/mp4; codecs={codec}",
            },
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "asf" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }

    /// <summary>The media type without parameters, for the <c>Content-Type</c> header.</summary>
    public static string Bare(string mediaType) => mediaType.Split(';')[0];

    /// <summary>The codec and bitrate as the file says, for when the plugin didn't.</summary>
    public static (string? Codec, int? BitrateKbps) Probe(string fullPath)
    {
        try
        {
            using var file = TagLib.File.Create(fullPath);
            var quality = QualityOf(file, fullPath);
            return (quality.Codec, quality.BitrateKbps);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A file's encoding as it says: the codec, from TagLib's description of it or else the extension; the bitrate; and
    /// for lossless files, the bit depth and sample rate.
    /// </summary>
    public static FileQuality QualityOf(TagLib.File file, string path)
    {
        var properties = file.Properties;
        var description = properties?.Codecs.OfType<TagLib.IAudioCodec>().FirstOrDefault()?.Description ?? "";
        bool Says(string name) => description.Contains(name, StringComparison.OrdinalIgnoreCase);
        var codec = Says("ALAC") ? "alac"
            : Says("Opus") ? "opus"
            : Says("Vorbis") ? "vorbis"
            : Says("FLAC") ? "flac"
            : Says("Layer 3") ? "mp3"
            : Says("AAC") ? "aac"
            : Says("WavPack") ? "wavpack"
            : Says("Monkey") ? "ape"
            : Says("Musepack") ? "mpc"
            : CodecFor(path);
        // Some files don't say (Opus, a VBR MP3 without its header): their average, from their size and length.
        int? kbps = properties?.AudioBitrate is > 0 and var said ? said
            : properties?.Duration.TotalSeconds is > 0 and var seconds && new FileInfo(path) is { Exists: true } info
            && (int)Math.Round(info.Length * 8 / seconds / 1000) is > 0 and var average ? average
            : null;
        var quality = new FileQuality(codec, kbps, null, properties?.AudioSampleRate is > 0 and var rate ? rate : null);
        // Lossy files have no depth of their own, whatever their decoder works at.
        return quality.Lossless && properties?.BitsPerSample is > 0 and var bits ? quality with { BitDepth = bits } : quality;
    }
}
