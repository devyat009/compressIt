using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public sealed class VideoProcessingService
{
    public async Task<VideoMetadata> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found.", videoPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(videoPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe.");
        string json = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("ffprobe could not read video metadata.");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        long fileSize = new FileInfo(videoPath).Length;
        TimeSpan duration = TimeSpan.Zero;
        long bitrate = 0;

        if (root.TryGetProperty("format", out var formatEl))
        {
            if (formatEl.TryGetProperty("duration", out var durEl) &&
                double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double durSec))
            {
                duration = TimeSpan.FromSeconds(durSec);
            }

            if (formatEl.TryGetProperty("bit_rate", out var brEl) &&
                long.TryParse(brEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long br))
            {
                bitrate = br;
            }
        }

        int width = 0;
        int height = 0;
        double fps = 30.0;
        string videoCodec = "unknown";
        string? audioCodec = null;
        bool hasAudio = false;

        if (root.TryGetProperty("streams", out var streamsEl))
        {
            foreach (var stream in streamsEl.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() : null;
                if (codecType == "video" && width == 0)
                {
                    width = stream.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                    videoCodec = stream.TryGetProperty("codec_name", out var cnEl) ? cnEl.GetString() ?? "unknown" : "unknown";

                    if (stream.TryGetProperty("avg_frame_rate", out var fpsEl))
                    {
                        fps = ParseFraction(fpsEl.GetString(), 30.0);
                    }

                    if (duration == TimeSpan.Zero &&
                        stream.TryGetProperty("duration", out var sdEl) &&
                        double.TryParse(sdEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sDur))
                    {
                        duration = TimeSpan.FromSeconds(sDur);
                    }
                }
                else if (codecType == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    audioCodec = stream.TryGetProperty("codec_name", out var acEl) ? acEl.GetString() : null;
                }
            }
        }

        return new VideoMetadata
        {
            FilePath = videoPath,
            FileName = Path.GetFileName(videoPath),
            FileSizeBytes = fileSize,
            Duration = duration,
            Width = width,
            Height = height,
            Fps = fps,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            HasAudio = hasAudio,
            BitrateBps = bitrate
        };
    }

    public async Task<List<string>> ExtractThumbnailsAsync(
        string ffmpegPath,
        string videoPath,
        TimeSpan duration,
        int count,
        string outputDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        if (duration <= TimeSpan.Zero || count <= 0) return [];

        var results = new string[count];
        using var semaphore = new SemaphoreSlim(4);

        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                double fraction = count == 1 ? 0.5 : (double)i / (count - 1);
                var ts = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds * fraction, 0, Math.Max(0, duration.TotalSeconds - 0.1)));
                var outFile = Path.Combine(outputDirectory, $"thumb_{i:D3}.jpg");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(FormatHelper.FormatFfmpegTimestamp(ts));
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-frames:v");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("scale=-1:64");
                psi.ArgumentList.Add("-q:v");
                psi.ArgumentList.Add("6");
                psi.ArgumentList.Add(outFile);

                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0 && File.Exists(outFile))
                    {
                        results[i] = outFile;
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public async Task<CompressionResult> ExportAsync(
        ExportRequest request,
        string ffmpegPath,
        IProgress<ExportProgress> progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var included = request.Segments.Where(s => s.IsIncluded && s.Duration > TimeSpan.Zero).ToList();
        if (included.Count == 0)
        {
            return new CompressionResult
            {
                Success = false,
                ErrorMessage = "No included segments to export.",
                OriginalSizeBytes = request.SourceVideo.FileSizeBytes
            };
        }

        Directory.CreateDirectory(request.OutputDirectory);
        bool compress = request.Options.EnableCompression;
        string ext = FfmpegCommandBuilder.ResolveExtension(request.Container, request.Options.Codec, request.SourceVideo.FilePath, compress);
        var resolvedEncoder = compress
            ? FfmpegCommandBuilder.ResolveEncoder(request.Options.Codec, request.UseGpu, request.Hardware)
            : new ResolvedEncoder("Lossless (Stream Copy)", false, null);

        var outputFiles = new List<string>();
        var totalDuration = SegmentManager.GetTotalIncludedDuration(included);
        string? losslessTempDir = null;

        try
        {
            if (!compress)
            {
                if (included.Count == 1)
                {
                    string targetPath = GetUniqueFilePath(Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{ext}"));
                    outputFiles.Add(targetPath);

                    var args = FfmpegCommandBuilder.BuildLosslessSingleSegmentArgs(
                        request.SourceVideo,
                        included[0],
                        targetPath,
                        request.Options);

                    await RunFfmpegWithProgressAsync(
                        ffmpegPath,
                        args,
                        included[0].Duration,
                        progress,
                        statusPrefix: "Cutting losslessly (stream copy)...",
                        basePercent: 0,
                        percentSpan: 100,
                        segmentIndex: 1,
                        totalSegments: 1,
                        ct);
                }
                else if (request.MergeSegments)
                {
                    losslessTempDir = Path.Combine(Path.GetTempPath(), "CompressIt_Lossless", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(losslessTempDir);

                    var tempParts = new List<string>(included.Count);
                    double completedSec = 0;
                    double totalSec = Math.Max(0.1, totalDuration.TotalSeconds);

                    for (int i = 0; i < included.Count; i++)
                    {
                        var seg = included[i];
                        string partPath = Path.Combine(losslessTempDir, $"part_{i:D3}{ext}");
                        tempParts.Add(partPath);

                        var segArgs = FfmpegCommandBuilder.BuildLosslessSingleSegmentArgs(
                            request.SourceVideo,
                            seg,
                            partPath,
                            request.Options);

                        double segSpan = seg.Duration.TotalSeconds / totalSec * 80.0;
                        double basePct = completedSec / totalSec * 80.0;

                        await RunFfmpegWithProgressAsync(
                            ffmpegPath,
                            segArgs,
                            seg.Duration,
                            progress,
                            statusPrefix: $"Cutting segment {i + 1} of {included.Count} (lossless)...",
                            basePercent: basePct,
                            percentSpan: segSpan,
                            segmentIndex: i + 1,
                            totalSegments: included.Count,
                            ct);

                        completedSec += seg.Duration.TotalSeconds;
                    }

                    string concatListPath = Path.Combine(losslessTempDir, "concat.txt");
                    var concatLines = tempParts.Select(p => $"file '{p.Replace("'", "'\\''")}'");
                    await File.WriteAllLinesAsync(concatListPath, concatLines, ct);

                    string targetPath = GetUniqueFilePath(Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{ext}"));
                    outputFiles.Add(targetPath);

                    bool hasAudio = request.SourceVideo.HasAudio && request.Options.Audio != AudioMode.Mute;
                    var concatArgs = FfmpegCommandBuilder.BuildLosslessConcatArgs(concatListPath, targetPath, request.Options, hasAudio);

                    await RunFfmpegWithProgressAsync(
                        ffmpegPath,
                        concatArgs,
                        totalDuration,
                        progress,
                        statusPrefix: $"Merging {included.Count} lossless segments...",
                        basePercent: 80,
                        percentSpan: 20,
                        segmentIndex: included.Count,
                        totalSegments: included.Count,
                        ct);
                }
                else
                {
                    double completedDurationSec = 0;
                    double totalDurationSec = Math.Max(0.1, totalDuration.TotalSeconds);

                    for (int i = 0; i < included.Count; i++)
                    {
                        var seg = included[i];
                        string targetPath = GetUniqueFilePath(Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}_part{i + 1}{ext}"));
                        outputFiles.Add(targetPath);

                        var args = FfmpegCommandBuilder.BuildLosslessSingleSegmentArgs(
                            request.SourceVideo,
                            seg,
                            targetPath,
                            request.Options);

                        double segSpan = seg.Duration.TotalSeconds / totalDurationSec * 100.0;
                        double basePct = completedDurationSec / totalDurationSec * 100.0;

                        await RunFfmpegWithProgressAsync(
                            ffmpegPath,
                            args,
                            seg.Duration,
                            progress,
                            statusPrefix: $"Cutting segment {i + 1} of {included.Count} (lossless)...",
                            basePercent: basePct,
                            percentSpan: segSpan,
                            segmentIndex: i + 1,
                            totalSegments: included.Count,
                            ct);

                        completedDurationSec += seg.Duration.TotalSeconds;
                    }
                }
            }
            else if (request.MergeSegments || included.Count == 1)
            {
                string targetPath = GetUniqueFilePath(Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}{ext}"));
                outputFiles.Add(targetPath);

                var args = FfmpegCommandBuilder.BuildMergedSegmentsArgs(
                    request.SourceVideo,
                    included,
                    targetPath,
                    request.Options,
                    resolvedEncoder);

                string label = included.Count > 1
                    ? $"Merging & compressing {included.Count} segments ({resolvedEncoder.EncoderName})..."
                    : $"Compressing video ({resolvedEncoder.EncoderName})...";

                await RunFfmpegWithProgressAsync(
                    ffmpegPath,
                    args,
                    totalDuration,
                    progress,
                    statusPrefix: label,
                    basePercent: 0,
                    percentSpan: 100,
                    segmentIndex: 1,
                    totalSegments: 1,
                    ct);
            }
            else
            {
                double completedDurationSec = 0;
                double totalDurationSec = Math.Max(0.1, totalDuration.TotalSeconds);

                for (int i = 0; i < included.Count; i++)
                {
                    var seg = included[i];
                    string targetPath = GetUniqueFilePath(Path.Combine(request.OutputDirectory, $"{request.OutputBaseName}_part{i + 1}{ext}"));
                    outputFiles.Add(targetPath);

                    var args = FfmpegCommandBuilder.BuildSingleSegmentArgs(
                        request.SourceVideo,
                        seg,
                        targetPath,
                        request.Options,
                        resolvedEncoder);

                    double segSpan = seg.Duration.TotalSeconds / totalDurationSec * 100.0;
                    double basePct = completedDurationSec / totalDurationSec * 100.0;

                    await RunFfmpegWithProgressAsync(
                        ffmpegPath,
                        args,
                        seg.Duration,
                        progress,
                        statusPrefix: $"Compressing segment {i + 1} of {included.Count} ({resolvedEncoder.EncoderName})...",
                        basePercent: basePct,
                        percentSpan: segSpan,
                        segmentIndex: i + 1,
                        totalSegments: included.Count,
                        ct);

                    completedDurationSec += seg.Duration.TotalSeconds;
                }
            }

            sw.Stop();
            long compressedTotal = outputFiles
                .Where(File.Exists)
                .Sum(p => new FileInfo(p).Length);

            return new CompressionResult
            {
                Success = true,
                OriginalSizeBytes = request.SourceVideo.FileSizeBytes,
                CompressedSizeBytes = compressedTotal,
                OutputFiles = outputFiles,
                Elapsed = sw.Elapsed,
                WasMerged = request.MergeSegments || included.Count == 1,
                UsedEncoder = resolvedEncoder.EncoderName
            };
        }
        catch (OperationCanceledException)
        {
            CleanupPartialFiles(outputFiles);
            return new CompressionResult
            {
                Success = false,
                WasCancelled = true,
                ErrorMessage = "Export cancelled by user.",
                OriginalSizeBytes = request.SourceVideo.FileSizeBytes
            };
        }
        catch (Exception ex)
        {
            CleanupPartialFiles(outputFiles);
            return new CompressionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                OriginalSizeBytes = request.SourceVideo.FileSizeBytes
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(losslessTempDir))
            {
                try { Directory.Delete(losslessTempDir, recursive: true); } catch { }
            }
        }
    }

    private static async Task RunFfmpegWithProgressAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        TimeSpan expectedDuration,
        IProgress<ExportProgress> progress,
        string statusPrefix,
        double basePercent,
        double percentSpan,
        int segmentIndex,
        int totalSegments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg.");

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        double expectedUs = Math.Max(1.0, expectedDuration.TotalMicroseconds);
        string currentSpeed = "";
        double latestLocalFraction = 0;

        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key == "speed")
            {
                currentSpeed = val;
            }
            else if (key == "out_time_us" && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
            {
                latestLocalFraction = Math.Clamp(us / expectedUs, 0, 1);
            }
            else if (key == "out_time_ms" && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long msUs))
            {
                // Note: ffmpeg's out_time_ms is actually microseconds in many builds
                latestLocalFraction = Math.Clamp(msUs / expectedUs, 0, 1);
            }
            else if (key == "progress")
            {
                if (val == "end") latestLocalFraction = 1.0;

                double overallPct = Math.Clamp(basePercent + latestLocalFraction * percentSpan, 0, 100);
                string eta = EstimateEta(expectedDuration, latestLocalFraction, currentSpeed);

                progress.Report(new ExportProgress
                {
                    Percent = overallPct,
                    StatusText = statusPrefix,
                    SpeedText = string.IsNullOrWhiteSpace(currentSpeed) ? "" : $"Speed: {currentSpeed}",
                    EtaText = eta,
                    CurrentSegmentIndex = segmentIndex,
                    TotalSegments = totalSegments
                });
            }
        }

        await process.WaitForExitAsync(ct);
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var lastLines = string.Join(Environment.NewLine, stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(6));
            throw new InvalidOperationException($"FFmpeg failed (exit code {process.ExitCode}): {lastLines}");
        }
    }

    private static string EstimateEta(TimeSpan total, double fraction, string speedStr)
    {
        if (fraction <= 0.01 || string.IsNullOrWhiteSpace(speedStr)) return "";
        if (double.TryParse(speedStr.TrimEnd('x', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out double mult) && mult > 0.01)
        {
            double remainingVideoSec = total.TotalSeconds * (1.0 - fraction);
            var eta = TimeSpan.FromSeconds(remainingVideoSec / mult);
            return $"ETA: {FormatHelper.FormatTimecode(eta, includeMilliseconds: false)}";
        }
        return "";
    }

    private static double ParseFraction(string? raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var parts = raw.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
            den > 0)
        {
            return num / den;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && val > 0
            ? val
            : fallback;
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        int n = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            n++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static void CleanupPartialFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
