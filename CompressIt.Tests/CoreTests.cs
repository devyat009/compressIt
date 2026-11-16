using CompressIt.Core.Models;
using CompressIt.Core.Services;
using Xunit;

namespace CompressIt.Tests;

public class CoreTests
{
    [Fact]
    public void FormatFileSize_FormatsMbAndGbCorrectly()
    {
        Assert.Equal("500.0 KB", FormatHelper.FormatFileSize(500 * 1024L));
        Assert.Equal("148.5 MB", FormatHelper.FormatFileSize((long)(148.5 * 1024 * 1024)));
        Assert.Equal("2.50 GB", FormatHelper.FormatFileSize((long)(2.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void GetQualityTier_MapsLowMediumHigh()
    {
        Assert.Equal("Low", FormatHelper.GetQualityTier(15));
        Assert.Equal("Low", FormatHelper.GetQualityTier(39));
        Assert.Equal("Medium", FormatHelper.GetQualityTier(40));
        Assert.Equal("Medium", FormatHelper.GetQualityTier(74));
        Assert.Equal("High", FormatHelper.GetQualityTier(75));
        Assert.Equal("High", FormatHelper.GetQualityTier(93));
    }

    [Fact]
    public void SegmentManager_SplitAndMerge_PreservesTimelineContinuity()
    {
        var initial = SegmentManager.CreateInitial(TimeSpan.FromSeconds(60));
        Assert.Single(initial);

        var split1 = SegmentManager.SplitAt(initial, TimeSpan.FromSeconds(20));
        Assert.Equal(2, split1.Count);
        Assert.Equal(TimeSpan.Zero, split1[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(20), split1[0].End);
        Assert.Equal(TimeSpan.FromSeconds(20), split1[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(60), split1[1].End);

        var split2 = SegmentManager.SplitAt(split1, TimeSpan.FromSeconds(45));
        Assert.Equal(3, split2.Count);
        Assert.Equal(1, split2[0].Index);
        Assert.Equal(2, split2[1].Index);
        Assert.Equal(3, split2[2].Index);

        var toggled = SegmentManager.ToggleInclude(split2, split2[1].Id);
        Assert.False(toggled[1].IsIncluded);
        Assert.Equal(TimeSpan.FromSeconds(35), SegmentManager.GetTotalIncludedDuration(toggled));

        var merged = SegmentManager.MergeWithNext(split2, split2[0].Id);
        Assert.Equal(2, merged.Count);
        Assert.Equal(TimeSpan.FromSeconds(45), merged[0].End);
    }

    [Fact]
    public void FfmpegCommandBuilder_PrefersGpuForMatchingVendor_AndFallsBackToCpuWhenDisabled()
    {
        var hw = new HardwareCapabilities
        {
            GpuNames = ["AMD Radeon RX 9060 XT"],
            DetectedVendor = HardwareVendor.Amd,
            AvailableEncoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "h264_amf", "hevc_amf", "av1_amf", "libx264", "libx265", "libsvtav1", "libvpx-vp9"
            }
        };

        var gpuH264 = FfmpegCommandBuilder.ResolveEncoder(VideoCodec.H264, useGpu: true, hw);
        Assert.True(gpuH264.IsGpu);
        Assert.Equal("h264_amf", gpuH264.EncoderName);

        var gpuAv1 = FfmpegCommandBuilder.ResolveEncoder(VideoCodec.AV1, useGpu: true, hw);
        Assert.True(gpuAv1.IsGpu);
        Assert.Equal("av1_amf", gpuAv1.EncoderName);

        var cpuH264 = FfmpegCommandBuilder.ResolveEncoder(VideoCodec.H264, useGpu: false, hw);
        Assert.False(cpuH264.IsGpu);
        Assert.Equal("libx264", cpuH264.EncoderName);

        var vp9Fallback = FfmpegCommandBuilder.ResolveEncoder(VideoCodec.VP9, useGpu: true, hw);
        Assert.False(vp9Fallback.IsGpu);
        Assert.Equal("libvpx-vp9", vp9Fallback.EncoderName);
        Assert.NotNull(vp9Fallback.Note);
    }

    [Fact]
    public void FfmpegCommandBuilder_MergedSegments_BuildsConcatFilterComplex()
    {
        var meta = new VideoMetadata
        {
            FilePath = @"C:\videos\demo.mp4",
            FileName = "demo.mp4",
            FileSizeBytes = 100_000_000,
            Duration = TimeSpan.FromSeconds(60),
            Width = 1920,
            Height = 1080,
            Fps = 30,
            HasAudio = true
        };
        var segments = new List<VideoSegment>
        {
            new() { Index = 1, Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(10), IsIncluded = true },
            new() { Index = 2, Start = TimeSpan.FromSeconds(25), End = TimeSpan.FromSeconds(40), IsIncluded = true }
        };
        var opts = new CompressionOptions
        {
            Codec = VideoCodec.H264,
            Speed = EncodingSpeed.Balanced,
            TargetFps = 30,
            QualityPercent = 93,
            Audio = AudioMode.Copy
        };
        var encoder = new ResolvedEncoder("h264_amf", true, null);

        var args = FfmpegCommandBuilder.BuildMergedSegmentsArgs(meta, segments, @"C:\videos\demo_compressed.mp4", opts, encoder);
        string joined = string.Join(" ", args);

        Assert.Contains("-filter_complex", args);
        Assert.Contains("concat=n=2:v=1:a=1", joined);
        Assert.Contains("fps=30", joined);
        Assert.Contains("h264_amf", args);
        // Copy audio cannot be used with filter_complex atrim/concat, so builder switches to aac
        Assert.Contains("aac", args);
    }
}
