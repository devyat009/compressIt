using System.Diagnostics;
using CompressIt.Core.Models;
using CompressIt.Core.Services;
using Xunit;

namespace CompressIt.Tests;

public class IntegrationSmokeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EndToEnd_DownloadFfmpeg_DetectGpu_CutAndCompressMergedAndSeparate()
    {
        var downloader = new FFmpegDownloadService();
        var dlProgress = new Progress<FfmpegDownloadProgress>();
        await downloader.EnsureInstalledAsync(dlProgress);

        var (ffmpegPath, ffprobePath) = FFmpegDownloadService.ResolvePaths();
        Assert.True(File.Exists(ffmpegPath));
        Assert.True(File.Exists(ffprobePath));

        var detector = new HardwareDetectorService();
        var hw = await detector.DetectAsync(ffmpegPath);
        Assert.NotEmpty(hw.GpuNames);
        Assert.NotEmpty(hw.AvailableEncoders);

        var tempDir = Path.Combine(Path.GetTempPath(), "CompressIt_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Generate a 4-second test video with audio
            var inputPath = Path.Combine(tempDir, "sample_input.mp4");
            var genPsi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=640x360:rate=30:duration=4 -f lavfi -i sine=frequency=440:duration=4 -c:v libx264 -pix_fmt yuv420p -c:a aac \"{inputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var genProc = Process.Start(genPsi)!)
            {
                await genProc.WaitForExitAsync();
                Assert.Equal(0, genProc.ExitCode);
            }

            var processor = new VideoProcessingService();
            var meta = await processor.ProbeAsync(ffprobePath, inputPath);
            Assert.Equal(640, meta.Width);
            Assert.Equal(360, meta.Height);
            Assert.True(meta.HasAudio);
            Assert.True(meta.Duration.TotalSeconds >= 3.5);

            // Extract thumbnails
            var thumbsDir = Path.Combine(tempDir, "thumbs");
            var thumbs = await processor.ExtractThumbnailsAsync(ffmpegPath, inputPath, meta.Duration, 4, thumbsDir);
            Assert.Equal(4, thumbs.Count);

            // Cut into 3 segments, exclude middle segment
            var segs = SegmentManager.CreateInitial(meta.Duration);
            segs = SegmentManager.SplitAt(segs, TimeSpan.FromSeconds(1.2));
            segs = SegmentManager.SplitAt(segs, TimeSpan.FromSeconds(2.6));
            segs = SegmentManager.ToggleInclude(segs, segs[1].Id);
            Assert.Equal(2, segs.Count(s => s.IsIncluded));

            // 1) Export MERGED with GPU default (or fallback) at 93% quality
            var mergedOutDir = Path.Combine(tempDir, "out_merged");
            var exportProgress = new Progress<ExportProgress>();
            var mergedReq = new ExportRequest
            {
                SourceVideo = meta,
                OutputDirectory = mergedOutDir,
                OutputBaseName = "sample_merged",
                MergeSegments = true,
                Segments = segs,
                Options = new CompressionOptions
                {
                    Codec = VideoCodec.H264,
                    Speed = EncodingSpeed.Balanced,
                    TargetFps = 30,
                    QualityPercent = 93,
                    Audio = AudioMode.Aac192
                },
                UseGpu = true,
                Hardware = hw
            };

            var mergedResult = await processor.ExportAsync(mergedReq, ffmpegPath, exportProgress);
            Assert.True(mergedResult.Success, mergedResult.ErrorMessage);
            Assert.Single(mergedResult.OutputFiles);
            Assert.True(File.Exists(mergedResult.OutputFiles[0]));

            if (hw.AvailableEncoders.Contains("av1_amf"))
            {
                var av1Req = new ExportRequest
                {
                    SourceVideo = meta,
                    OutputDirectory = mergedOutDir,
                    OutputBaseName = "sample_av1_gpu",
                    MergeSegments = true,
                    Segments = segs,
                    Options = new CompressionOptions
                    {
                        Codec = VideoCodec.AV1,
                        Speed = EncodingSpeed.Balanced,
                        QualityPercent = 93,
                        Audio = AudioMode.Aac192
                    },
                    UseGpu = true,
                    Hardware = hw
                };
                var av1Result = await processor.ExportAsync(av1Req, ffmpegPath, exportProgress);
                Assert.True(av1Result.Success, av1Result.ErrorMessage);
                Assert.Equal("av1_amf", av1Result.UsedEncoder);
            }

            var mergedMeta = await processor.ProbeAsync(ffprobePath, mergedResult.OutputFiles[0]);
            Assert.InRange(mergedMeta.Duration.TotalSeconds, 2.0, 3.2);

            // 2) Export SEPARATE with CPU
            var sepOutDir = Path.Combine(tempDir, "out_separate");
            var sepReq = new ExportRequest
            {
                SourceVideo = meta,
                OutputDirectory = sepOutDir,
                OutputBaseName = "sample_sep",
                MergeSegments = false,
                Segments = segs,
                Options = new CompressionOptions
                {
                    Codec = VideoCodec.H265,
                    Speed = EncodingSpeed.Fast,
                    QualityPercent = 75,
                    Audio = AudioMode.Aac128
                },
                UseGpu = false,
                Hardware = hw
            };

            var sepResult = await processor.ExportAsync(sepReq, ffmpegPath, exportProgress);
            Assert.True(sepResult.Success, sepResult.ErrorMessage);
            Assert.Equal(2, sepResult.OutputFiles.Count);
            Assert.All(sepResult.OutputFiles, f => Assert.True(File.Exists(f)));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
