using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using CompressIt.Core.Services;
using Microsoft.Win32;

namespace CompressIt.Installer;

public static class InstallerService
{
    private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CompressIt";

    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CompressIt");

    private static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CompressIt.lnk");

    private static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CompressIt.lnk");

    public static bool HasEmbeddedPayload()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceInfo("CompressIt.Payload.zip") is not null;
    }

    public static async Task InstallAsync(
        string installDir,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool registerShellIntegration,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct = default)
    {
        progress.Report((5, "Closing running CompressIt instances..."));
        await StopRunningAppAsync(ct);

        Directory.CreateDirectory(installDir);

        progress.Report((15, "Extracting application files..."));
        await ExtractPayloadAsync(installDir, progress, ct);

        string installedExe = Path.Combine(installDir, "CompressIt.exe");
        if (!File.Exists(installedExe))
        {
            throw new FileNotFoundException("CompressIt.exe was not found in the extracted package.", installedExe);
        }

        progress.Report((85, "Copying uninstaller..."));
        string uninstallExe = Path.Combine(installDir, "Uninstall.exe");
        string? currentSetupExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentSetupExe) &&
            File.Exists(currentSetupExe) &&
            !Path.GetFullPath(currentSetupExe).Equals(Path.GetFullPath(uninstallExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(currentSetupExe, uninstallExe, overwrite: true);
        }

        progress.Report((90, "Creating Windows shortcuts..."));
        if (createStartMenuShortcut)
        {
            CreateShortcut(StartMenuShortcutPath, installedExe, installDir, "CompressIt Video Cutter & Compressor");
        }
        if (createDesktopShortcut)
        {
            CreateShortcut(DesktopShortcutPath, installedExe, installDir, "CompressIt Video Cutter & Compressor");
        }

        progress.Report((94, "Registering Windows Uninstall entry..."));
        var version = UpdateService.GetCurrentVersion();
        RegisterUninstallEntry(installDir, installedExe, uninstallExe, $"{version.Major}.{version.Minor}.{version.Build}");

        if (registerShellIntegration)
        {
            progress.Report((97, "Registering Explorer 'Open in CompressIt'..."));
            ShellIntegrationService.Register(installedExe);
        }

        progress.Report((100, "CompressIt installed successfully!"));
    }

    public static async Task UninstallAsync(
        string? installDir,
        IProgress<(double Percent, string Status)> progress)
    {
        progress.Report((15, "Closing CompressIt..."));
        await StopRunningAppAsync(CancellationToken.None);

        progress.Report((40, "Removing Windows Explorer integration..."));
        ShellIntegrationService.Unregister();

        progress.Report((60, "Removing shortcuts..."));
        TryDeleteFile(StartMenuShortcutPath);
        TryDeleteFile(DesktopShortcutPath);

        progress.Report((80, "Removing Windows Uninstall registration..."));
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false);
        }
        catch { }

        string targetDir = !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir)
            ? installDir
            : DefaultInstallDirectory;

        progress.Report((95, "Scheduling folder cleanup..."));
        if (Directory.Exists(targetDir))
        {
            int pid = Environment.ProcessId;
            string cmd = $"/c \"timeout /t 2 /nobreak >NUL & taskkill /F /PID {pid} >NUL 2>&1 & rmdir /s /q \"{targetDir}\"\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        progress.Report((100, "CompressIt has been uninstalled."));
    }

    private static async Task StopRunningAppAsync(CancellationToken ct)
    {
        foreach (var proc in Process.GetProcessesByName("CompressIt"))
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(1500))
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync(ct);
                    }
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static async Task ExtractPayloadAsync(
        string installDir,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        await using Stream? embeddedStream = asm.GetManifestResourceStream("CompressIt.Payload.zip");

        if (embeddedStream is not null)
        {
            await Task.Run(() => ExtractZipStream(embeddedStream, installDir, progress, ct), ct);
            return;
        }

        // Fallback if run without embedded zip: check adjacent CompressIt-Payload.zip
        string adjacentZip = Path.Combine(AppContext.BaseDirectory, "CompressIt-Payload.zip");
        if (File.Exists(adjacentZip))
        {
            await using var fs = File.OpenRead(adjacentZip);
            await Task.Run(() => ExtractZipStream(fs, installDir, progress, ct), ct);
            return;
        }

        throw new InvalidOperationException("Installer payload is missing. Run scripts/build-release.ps1 to bundle CompressIt-Setup.exe.");
    }

    private static void ExtractZipStream(
        Stream stream,
        string installDir,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        int total = Math.Max(1, archive.Entries.Count);
        int count = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            count++;

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string destPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
            if (!destPath.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);

            if (count % 10 == 0 || count == total)
            {
                double pct = 15.0 + (double)count / total * 68.0;
                progress.Report((pct, $"Installing files ({count}/{total})..."));
            }
        }
    }

    private static void RegisterUninstallEntry(string installDir, string exePath, string uninstallExe, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegKey);
        key.SetValue("DisplayName", "CompressIt");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", UpdateService.AuthorName);
        key.SetValue("URLInfoAbout", UpdateService.AuthorGitHubUrl);
        key.SetValue("HelpLink", UpdateService.ReleasesPageUrl);
        key.SetValue("DisplayIcon", $"\"{exePath}\",0");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{uninstallExe}\" --uninstall --install-dir \"{installDir}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            shortcut.IconLocation = $"{targetExe},0";
            shortcut.Save();
        }
        catch
        {
            // Shortcut creation is best-effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
