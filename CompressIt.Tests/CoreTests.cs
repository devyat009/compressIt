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
    public void SegmentManager_AdjustBoundaries_CreatesGapAndClampsAtNeighborOrEdge()
    {
        var total = TimeSpan.FromSeconds(60);
        var segs = SegmentManager.SplitAt(SegmentManager.CreateInitial(total), TimeSpan.FromSeconds(30));

        // Trim start of first segment with "<" handle -> [5s, 30s]
        segs = SegmentManager.AdjustSegmentStart(segs, segs[0].Id, TimeSpan.FromSeconds(5), total);
        Assert.Equal(TimeSpan.FromSeconds(5), segs[0].Start);

        // Trim end of segment 1 inward with ">" -> [5s, 22s], leaving gray cut-out gap [22s, 30s] before segment 2
        segs = SegmentManager.AdjustSegmentEnd(segs, segs[0].Id, TimeSpan.FromSeconds(22), total);
        Assert.Equal(TimeSpan.FromSeconds(22), segs[0].End);
        Assert.Equal(TimeSpan.FromSeconds(30), segs[1].Start);

        // Expanding segment 2's "<" leftward stops cleanly at segment 1's end (22s), never overlapping
        segs = SegmentManager.AdjustSegmentStart(segs, segs[1].Id, TimeSpan.FromSeconds(10), total);
        Assert.Equal(TimeSpan.FromSeconds(22), segs[1].Start);

        // Trim end of last segment with ">" handle -> [22s, 52s]
        segs = SegmentManager.AdjustSegmentEnd(segs, segs[1].Id, TimeSpan.FromSeconds(52), total);
        Assert.Equal(TimeSpan.FromSeconds(52), segs[1].End);
    }

    [Fact]
    public void UpdateService_ParsesGitHubReleaseJsonAndDetectsNewerVersion()
    {
        string sampleJson = """
        {
          "tag_name": "v1.2.0",
          "name": "CompressIt v1.2.0",
          "body": "- Faster AMD AMF encoding\n- Timeline zoom improvements",
          "html_url": "https://github.com/devyat009/compressIt/releases/tag/v1.2.0",
          "assets": [
            {
              "name": "CompressIt-win-x64-v1.2.0.zip",
              "size": 70000000,
              "browser_download_url": "https://github.com/devyat009/compressIt/releases/download/v1.2.0/CompressIt-win-x64-v1.2.0.zip"
            },
            {
              "name": "CompressIt-Setup-v1.2.0.exe",
              "size": 75000000,
              "browser_download_url": "https://github.com/devyat009/compressIt/releases/download/v1.2.0/CompressIt-Setup-v1.2.0.exe"
            }
          ]
        }
        """;

        var update = UpdateService.ParseReleaseJson(sampleJson, new Version(1, 0, 0));
        Assert.True(update.HasReleases);
        Assert.True(update.IsUpdateAvailable);
        Assert.Equal(new Version(1, 2, 0), update.LatestVersion);
        Assert.Equal("CompressIt-Setup-v1.2.0.exe", update.AssetFileName);
        Assert.Contains("Faster AMD AMF encoding", update.ReleaseNotes);

        var sameVersion = UpdateService.ParseReleaseJson(sampleJson, new Version(1, 2, 0));
        Assert.False(sameVersion.IsUpdateAvailable);
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
