using System.Globalization;
using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public sealed record ResolvedEncoder(string EncoderName, bool IsGpu, string? Note);

public static class FfmpegCommandBuilder
{
    public static ResolvedEncoder ResolveEncoder(VideoCodec codec, bool useGpu, HardwareCapabilities hardware)
    {
        if (useGpu && hardware.SupportsGpuEncoding)
        {
            var candidates = GetGpuCandidates(codec, hardware.DetectedVendor);
            foreach (var candidate in candidates)
            {
                if (hardware.AvailableEncoders.Contains(candidate))
                {
                    return new ResolvedEncoder(candidate, true, null);
                }
            }
        }

        string cpu = codec switch
        {
            VideoCodec.H264 => "libx264",
            VideoCodec.H265 => "libx265",
            VideoCodec.AV1 => hardware.AvailableEncoders.Contains("libsvtav1") ? "libsvtav1" : "libaom-av1",
            VideoCodec.VP9 => "libvpx-vp9",
            _ => "libx264"
        };

        string? note = useGpu
            ? $"{codec} is not supported by detected GPU encoders; using CPU ({cpu})."
            : null;

        return new ResolvedEncoder(cpu, false, note);
    }

    private static string[] GetGpuCandidates(VideoCodec codec, HardwareVendor preferredVendor)
    {
        string suffixOrder = preferredVendor switch
        {
            HardwareVendor.Nvidia => "nvenc,amf,qsv",
            HardwareVendor.Amd => "amf,nvenc,qsv",
            HardwareVendor.Intel => "qsv,nvenc,amf",
            _ => "nvenc,amf,qsv"
        };

        string prefix = codec switch
        {
            VideoCodec.H264 => "h264",
            VideoCodec.H265 => "hevc",
            VideoCodec.AV1 => "av1",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(prefix)) return [];

        return suffixOrder.Split(',').Select(s => $"{prefix}_{s}").ToArray();
    }

    public static string ResolveExtension(ContainerFormat container, VideoCodec codec, string sourcePath)
    {
        return container switch
        {
            ContainerFormat.Mp4 => ".mp4",
            ContainerFormat.Mkv => ".mkv",
            ContainerFormat.Mov => ".mov",
            ContainerFormat.Webm => ".webm",
            ContainerFormat.Auto => codec == VideoCodec.VP9 ? ".webm" : ".mp4",
            _ => Path.GetExtension(sourcePath) is { Length: > 1 } ext ? ext : ".mp4"
        };
    }

    public static int MapQualityToQpOrCrf(VideoCodec codec, int qualityPercent)
    {
        int q = Math.Clamp(qualityPercent, 1, 100);
        // 100% -> minQ (highest quality), 1% -> maxQ (smallest file)
        var (minQ, maxQ) = codec switch
        {
            VideoCodec.H264 => (14, 42),
            VideoCodec.H265 => (16, 44),
            VideoCodec.AV1 => (18, 52),
            VideoCodec.VP9 => (18, 54),
            _ => (14, 42)
        };

        double t = (100 - q) / 99.0;
        return (int)Math.Round(minQ + t * (maxQ - minQ));
    }

    public static List<string> BuildEncoderArgs(ResolvedEncoder encoder, CompressionOptions options)
    {
        var args = new List<string> { "-c:v", encoder.EncoderName };
        int qVal = MapQualityToQpOrCrf(options.Codec, options.QualityPercent);

        if (encoder.EncoderName.EndsWith("_amf", StringComparison.OrdinalIgnoreCase))
        {
            string amfQuality = options.Speed switch
            {
                EncodingSpeed.Ultrafast or EncodingSpeed.Fast => "speed",
                EncodingSpeed.Balanced => "balanced",
                EncodingSpeed.Slow => "quality",
                EncodingSpeed.VerySlow => "high_quality",
                _ => "balanced"
            };
            int qvbrLevel = Math.Clamp(qVal, 1, 51);
            args.AddRange(["-quality", amfQuality, "-rc", "qvbr", "-qvbr_quality_level", qvbrLevel.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (encoder.EncoderName.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            string nvPreset = options.Speed switch
            {
                EncodingSpeed.Ultrafast => "p1",
                EncodingSpeed.Fast => "p3",
                EncodingSpeed.Balanced => "p4",
                EncodingSpeed.Slow => "p6",
                EncodingSpeed.VerySlow => "p7",
                _ => "p4"
            };
            args.AddRange(["-preset", nvPreset, "-rc", "vbr", "-cq", qVal.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        }
        else if (encoder.EncoderName.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
        {
            string qsvPreset = options.Speed switch
            {
                EncodingSpeed.Ultrafast => "veryfast",
                EncodingSpeed.Fast => "faster",
                EncodingSpeed.Balanced => "medium",
                EncodingSpeed.Slow => "slow",
                EncodingSpeed.VerySlow => "veryslow",
                _ => "medium"
            };
            args.AddRange(["-preset", qsvPreset, "-global_quality", qVal.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (encoder.EncoderName.Equals("libsvtav1", StringComparison.OrdinalIgnoreCase))
        {
            string svtPreset = options.Speed switch
            {
                EncodingSpeed.Ultrafast => "10",
                EncodingSpeed.Fast => "8",
                EncodingSpeed.Balanced => "6",
                EncodingSpeed.Slow => "4",
                EncodingSpeed.VerySlow => "3",
                _ => "6"
            };
            args.AddRange(["-preset", svtPreset, "-crf", qVal.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (encoder.EncoderName.Equals("libvpx-vp9", StringComparison.OrdinalIgnoreCase))
        {
            string cpuUsed = options.Speed switch
            {
                EncodingSpeed.Ultrafast => "5",
                EncodingSpeed.Fast => "4",
                EncodingSpeed.Balanced => "2",
                EncodingSpeed.Slow => "1",
                EncodingSpeed.VerySlow => "0",
                _ => "2"
            };
            args.AddRange(["-cpu-used", cpuUsed, "-crf", qVal.ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-row-mt", "1"]);
        }
        else
        {
            string cpuPreset = options.Speed switch
            {
                EncodingSpeed.Ultrafast => "ultrafast",
                EncodingSpeed.Fast => "fast",
                EncodingSpeed.Balanced => "medium",
                EncodingSpeed.Slow => "slow",
                EncodingSpeed.VerySlow => "veryslow",
                _ => "medium"
            };
            args.AddRange(["-preset", cpuPreset, "-crf", qVal.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-pix_fmt", "yuv420p"]);
        return args;
    }

    public static List<string> BuildAudioArgs(AudioMode mode, string outputExt, bool hasAudio, bool requiresFilterReencode)
    {
        if (!hasAudio || mode == AudioMode.Mute)
        {
            return ["-an"];
        }

        bool isWebm = outputExt.Equals(".webm", StringComparison.OrdinalIgnoreCase);

        if (mode == AudioMode.Copy && !requiresFilterReencode && !isWebm)
        {
            return ["-c:a", "copy"];
        }

        if (isWebm)
        {
            string bitrate = mode == AudioMode.Aac128 ? "128k" : "160k";
            return ["-c:a", "libopus", "-b:a", bitrate];
        }

        string aacBitrate = mode == AudioMode.Aac128 ? "128k" : "192k";
        return ["-c:a", "aac", "-b:a", aacBitrate];
    }

    public static List<string> BuildVideoFilterChain(CompressionOptions options)
    {
        var filters = new List<string>();

        if (options.TargetFps is int fps and > 0)
        {
            filters.Add($"fps={fps}");
        }

        switch (options.Resolution)
        {
            case OutputResolution.P1080:
                filters.Add("scale=-2:min(1080\\,ih)");
                break;
            case OutputResolution.P720:
                filters.Add("scale=-2:min(720\\,ih)");
                break;
            case OutputResolution.P480:
                filters.Add("scale=-2:min(480\\,ih)");
                break;
            case OutputResolution.Original:
            default:
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
                break;
        }

        return filters;
    }

    public static List<string> BuildSingleSegmentArgs(
        VideoMetadata source,
        VideoSegment segment,
        string outputPath,
        CompressionOptions options,
        ResolvedEncoder encoder)
    {
        var args = new List<string> { "-hide_banner", "-y", "-progress", "pipe:1", "-nostats" };

        bool isFullVideo = segment.Start <= TimeSpan.FromMilliseconds(50) &&
                           segment.End >= source.Duration - TimeSpan.FromMilliseconds(50);

        if (!isFullVideo && segment.Start > TimeSpan.Zero)
        {
            args.AddRange(["-ss", FormatHelper.FormatFfmpegTimestamp(segment.Start)]);
        }

        args.AddRange(["-i", source.FilePath]);

        if (!isFullVideo)
        {
            args.AddRange(["-t", FormatHelper.FormatFfmpegTimestamp(segment.Duration)]);
        }

        var vf = BuildVideoFilterChain(options);
        if (vf.Count > 0)
        {
            args.AddRange(["-vf", string.Join(",", vf)]);
        }

        args.AddRange(BuildEncoderArgs(encoder, options));

        var ext = Path.GetExtension(outputPath);
        args.AddRange(BuildAudioArgs(options.Audio, ext, source.HasAudio, requiresFilterReencode: false));

        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.Add(outputPath);
        return args;
    }

    public static List<string> BuildMergedSegmentsArgs(
        VideoMetadata source,
        IReadOnlyList<VideoSegment> segments,
        string outputPath,
        CompressionOptions options,
        ResolvedEncoder encoder)
    {
        if (segments.Count == 1)
        {
            return BuildSingleSegmentArgs(source, segments[0], outputPath, options, encoder);
        }

        var args = new List<string> { "-hide_banner", "-y", "-progress", "pipe:1", "-nostats", "-i", source.FilePath };

        bool includeAudio = source.HasAudio && options.Audio != AudioMode.Mute;
        var filterParts = new List<string>();
        var concatInputs = new List<string>();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            string start = seg.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            string end = seg.End.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            filterParts.Add($"[0:v]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}]");
            if (includeAudio)
            {
                filterParts.Add($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[a{i}]");
                concatInputs.Add($"[v{i}][a{i}]");
            }
            else
            {
                concatInputs.Add($"[v{i}]");
            }
        }

        string postVf = string.Join(",", BuildVideoFilterChain(options));
        if (includeAudio)
        {
            filterParts.Add($"{string.Concat(concatInputs)}concat=n={segments.Count}:v=1:a=1[cv][outa]");
            filterParts.Add($"[cv]{postVf}[outv]");
        }
        else
        {
            filterParts.Add($"{string.Concat(concatInputs)}concat=n={segments.Count}:v=1:a=0[cv]");
            filterParts.Add($"[cv]{postVf}[outv]");
        }

        args.AddRange(["-filter_complex", string.Join(";", filterParts)]);
        args.AddRange(["-map", "[outv]"]);
        if (includeAudio)
        {
            args.AddRange(["-map", "[outa]"]);
        }

        args.AddRange(BuildEncoderArgs(encoder, options));

        var ext = Path.GetExtension(outputPath);
        args.AddRange(BuildAudioArgs(options.Audio, ext, source.HasAudio, requiresFilterReencode: true));

        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.Add(outputPath);
        return args;
    }
}
