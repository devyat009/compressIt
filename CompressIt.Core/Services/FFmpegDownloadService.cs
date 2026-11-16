using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public sealed class FFmpegDownloadService
{
    private static readonly string[] DownloadUrls =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
    ];

    public static string DefaultFfmpegBinDirectory =>
        Path.Combine(AppSettingsService.AppDataDirectory, "ffmpeg", "bin");

    public static (string FfmpegPath, string FfprobePath) ResolvePaths(string? customDirectory = null)
    {
        var dir = !string.IsNullOrWhiteSpace(customDirectory) && Directory.Exists(customDirectory)
            ? customDirectory
            : DefaultFfmpegBinDirectory;

        var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        var ffprobe = Path.Combine(dir, "ffprobe.exe");
        return (ffmpeg, ffprobe);
    }

    public static bool IsInstalled(string? customDirectory = null)
    {
        var (ffmpeg, ffprobe) = ResolvePaths(customDirectory);
        return File.Exists(ffmpeg) && File.Exists(ffprobe);
    }

    public async Task<string?> GetVersionAsync(string ffmpegPath, CancellationToken ct = default)
    {
        if (!File.Exists(ffmpegPath)) return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var line = await process.StandardOutput.ReadLineAsync(ct);
            await process.WaitForExitAsync(ct);
            return line;
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureInstalledAsync(
        IProgress<FfmpegDownloadProgress> progress,
        bool forceRedownload = false,
        CancellationToken ct = default)
    {
        var (ffmpegPath, ffprobePath) = ResolvePaths();
        if (!forceRedownload && File.Exists(ffmpegPath) && File.Exists(ffprobePath))
        {
            progress.Report(new FfmpegDownloadProgress
            {
                Percent = 100,
                Status = "FFmpeg is already installed."
            });
            return;
        }

        Directory.CreateDirectory(DefaultFfmpegBinDirectory);
        var tempZipPath = Path.Combine(AppSettingsService.AppDataDirectory, $"ffmpeg-download-{Guid.NewGuid():N}.zip");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CompressIt/1.0");

            Exception? lastError = null;
            bool downloaded = false;

            foreach (var url in DownloadUrls)
            {
                try
                {
                    progress.Report(new FfmpegDownloadProgress
                    {
                        Percent = 0,
                        Status = $"Connecting to {new Uri(url).Host}..."
                    });

                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength;
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long totalRead = 0;
                        int bytesRead;
                        var lastReport = DateTime.UtcNow;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            totalRead += bytesRead;

                            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 100 || totalRead == totalBytes)
                            {
                                double pct = totalBytes is > 0 ? Math.Clamp((double)totalRead / totalBytes.Value * 85.0, 0, 85) : 0;
                                string sizeInfo = totalBytes.HasValue
                                    ? $"{FormatHelper.FormatFileSize(totalRead)} / {FormatHelper.FormatFileSize(totalBytes.Value)}"
                                    : FormatHelper.FormatFileSize(totalRead);

                                progress.Report(new FfmpegDownloadProgress
                                {
                                    BytesDownloaded = totalRead,
                                    TotalBytes = totalBytes,
                                    Percent = pct,
                                    Status = $"Downloading FFmpeg ({sizeInfo})..."
                                });
                                lastReport = DateTime.UtcNow;
                            }
                        }
                    }

                    downloaded = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            if (!downloaded)
            {
                throw new InvalidOperationException("Failed to download FFmpeg from all mirrors.", lastError);
            }

            ct.ThrowIfCancellationRequested();

            progress.Report(new FfmpegDownloadProgress
            {
                Percent = 88,
                Status = "Extracting ffmpeg.exe and ffprobe.exe..."
            });

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempZipPath);
                bool extractedFfmpeg = false;
                bool extractedFfprobe = false;

                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(ffmpegPath, overwrite: true);
                        extractedFfmpeg = true;
                    }
                    else if (entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(ffprobePath, overwrite: true);
                        extractedFfprobe = true;
                    }

                    if (extractedFfmpeg && extractedFfprobe) break;
                }

                if (!extractedFfmpeg || !extractedFfprobe)
                {
                    throw new InvalidOperationException("Downloaded archive did not contain ffmpeg.exe and ffprobe.exe.");
                }
            }, ct);

            progress.Report(new FfmpegDownloadProgress
            {
                Percent = 96,
                Status = "Verifying FFmpeg installation..."
            });

            var version = await GetVersionAsync(ffmpegPath, ct);
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException("FFmpeg executable failed verification.");
            }

            progress.Report(new FfmpegDownloadProgress
            {
                Percent = 100,
                Status = "FFmpeg ready."
            });
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }
        }
    }
}
