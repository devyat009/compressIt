using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CompressIt.Core.Services;

public sealed class UpdateInfo
{
    public Version CurrentVersion { get; init; } = new(1, 0, 0);
    public Version LatestVersion { get; init; } = new(1, 0, 0);
    public bool HasReleases { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public string ReleaseTitle { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string ReleasePageUrl { get; init; } = "https://github.com/devyat009/compressIt/releases";
    public string? AssetDownloadUrl { get; init; }
    public string? AssetFileName { get; init; }
    public long? AssetSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class UpdateDownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class UpdateService
{
    public const string RepoOwner = "devyat009";
    public const string RepoName = "compressIt";
    public const string AuthorName = "Devyat009";
    public const string AuthorGitHubUrl = "https://github.com/devyat009/";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";

    public static Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(infoVer, out var parsedInfo))
        {
            return NormalizeVersion(parsedInfo);
        }

        var ver = asm.GetName().Version;
        return ver is not null ? NormalizeVersion(ver) : new Version(1, 0, 0);
    }

    public static string FormatVersion(Version v) =>
        v.Build >= 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}.0";

    public static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string cleaned = raw.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[1..];
        }

        int plusIdx = cleaned.IndexOf('+');
        if (plusIdx >= 0) cleaned = cleaned[..plusIdx];

        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0) cleaned = cleaned[..dashIdx];

        if (Version.TryParse(cleaned, out var parsed))
        {
            version = NormalizeVersion(parsed);
            return true;
        }

        return false;
    }

    private static Version NormalizeVersion(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static UpdateInfo ParseReleaseJson(string json, Version currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        string releaseTitle = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? tagName ?? "New Release") : (tagName ?? "New Release");
        string releaseNotes = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? string.Empty) : string.Empty;
        string htmlUrl = root.TryGetProperty("html_url", out var htmlEl) ? (htmlEl.GetString() ?? ReleasesPageUrl) : ReleasesPageUrl;

        if (!TryParseVersion(tagName, out var latestVersion))
        {
            latestVersion = currentVersion;
        }

        string? exeUrl = null;
        string? exeName = null;
        long? exeSize = null;

        string? zipUrl = null;
        string? zipName = null;
        long? zipSize = null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                string? fileName = asset.TryGetProperty("name", out var fnEl) ? fnEl.GetString() : null;
                string? dlUrl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() : null;
                long? size = asset.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out long s) ? s : null;

                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(dlUrl)) continue;

                if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exeUrl is null)
                {
                    exeUrl = dlUrl;
                    exeName = fileName;
                    exeSize = size;
                }
                else if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zipUrl is null)
                {
                    zipUrl = dlUrl;
                    zipName = fileName;
                    zipSize = size;
                }
            }
        }

        var chosenUrl = exeUrl ?? zipUrl;
        var chosenName = exeName ?? zipName;
        var chosenSize = exeUrl is not null ? exeSize : zipSize;

        bool isNewer = latestVersion > NormalizeVersion(currentVersion);

        return new UpdateInfo
        {
            CurrentVersion = NormalizeVersion(currentVersion),
            LatestVersion = latestVersion,
            HasReleases = true,
            IsUpdateAvailable = isNewer,
            ReleaseTitle = releaseTitle,
            ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? "Bug fixes and improvements." : releaseNotes.Trim(),
            ReleasePageUrl = htmlUrl,
            AssetDownloadUrl = chosenUrl,
            AssetFileName = chosenName,
            AssetSizeBytes = chosenSize
        };
    }

    public async Task<UpdateInfo> CheckForUpdatesAsync(Version? currentVersion = null, CancellationToken ct = default)
    {
        var current = NormalizeVersion(currentVersion ?? GetCurrentVersion());

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"CompressIt/{current.Major}.{current.Minor}.{current.Build}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(LatestReleaseApiUrl, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateInfo
                {
                    CurrentVersion = current,
                    LatestVersion = current,
                    HasReleases = false,
                    IsUpdateAvailable = false,
                    ReleasePageUrl = ReleasesPageUrl
                };
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return ParseReleaseJson(json, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateInfo
            {
                CurrentVersion = current,
                LatestVersion = current,
                HasReleases = false,
                IsUpdateAvailable = false,
                ReleasePageUrl = ReleasesPageUrl,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> DownloadUpdateAssetAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.AssetDownloadUrl) || string.IsNullOrWhiteSpace(update.AssetFileName))
        {
            throw new InvalidOperationException("No downloadable update package was attached to the GitHub release.");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "CompressIt_Updates");
        Directory.CreateDirectory(tempDir);
        string targetPath = Path.Combine(tempDir, update.AssetFileName);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CompressIt-Updater/1.0");

        progress.Report(new UpdateDownloadProgress
        {
            Percent = 0,
            Status = $"Connecting to download {update.AssetFileName}..."
        });

        using var response = await http.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength ?? update.AssetSizeBytes;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var lastReport = DateTime.UtcNow;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 80 || totalRead == totalBytes)
                {
                    double pct = totalBytes is > 0 ? Math.Clamp((double)totalRead / totalBytes.Value * 100.0, 0, 100) : 0;
                    string sizeText = totalBytes.HasValue
                        ? $"{FormatHelper.FormatFileSize(totalRead)} / {FormatHelper.FormatFileSize(totalBytes.Value)}"
                        : FormatHelper.FormatFileSize(totalRead);

                    progress.Report(new UpdateDownloadProgress
                    {
                        BytesDownloaded = totalRead,
                        TotalBytes = totalBytes,
                        Percent = pct,
                        Status = $"Downloading {update.AssetFileName} ({sizeText})..."
                    });
                    lastReport = DateTime.UtcNow;
                }
            }
        }

        progress.Report(new UpdateDownloadProgress
        {
            Percent = 100,
            Status = "Download complete. Applying update..."
        });

        return targetPath;
    }

    public static void LaunchDownloadedUpdate(string downloadedFilePath, string currentAppDirectory)
    {
        if (downloadedFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo
            {
                FileName = downloadedFilePath,
                UseShellExecute = true
            };
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--relaunch");
            psi.ArgumentList.Add("--install-dir");
            psi.ArgumentList.Add(currentAppDirectory);
            Process.Start(psi);
            return;
        }

        if (downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string stageDir = Path.Combine(Path.GetTempPath(), "CompressIt_Updates", $"stage_{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(downloadedFilePath, stageDir, overwriteFiles: true);

            // Handle archives that wrap everything in a single subfolder
            var topDirs = Directory.GetDirectories(stageDir);
            var topFiles = Directory.GetFiles(stageDir);
            string sourceDir = (topDirs.Length == 1 && topFiles.Length == 0) ? topDirs[0] : stageDir;

            int pid = Environment.ProcessId;
            string exePath = Path.Combine(currentAppDirectory, "CompressIt.exe");
            string scriptPath = Path.Combine(Path.GetTempPath(), "CompressIt_Updates", "apply_update.cmd");

            string cmdContent = $"""
@echo off
:waitloop
tasklist /FI "PID eq {pid}" 2>NUL | find /I "{pid}" >NUL
if "%ERRORLEVEL%"=="0" (
    timeout /t 1 /nobreak >NUL
    goto waitloop
)
xcopy "{sourceDir}\*" "{currentAppDirectory}\" /E /Y /I /Q >NUL
start "" "{exePath}"
rmdir /s /q "{stageDir}"
del "%~f0"
""";
            File.WriteAllText(scriptPath, cmdContent);

            bool needsAdmin = currentAppDirectory.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                StringComparison.OrdinalIgnoreCase);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = needsAdmin,
                Verb = needsAdmin ? "runas" : string.Empty
            });
            return;
        }

        Process.Start(new ProcessStartInfo(downloadedFilePath) { UseShellExecute = true });
    }
}
