using System.Reflection;
using Microsoft.Win32;

namespace CompressIt.Core.Services;

public static class ShellIntegrationService
{
    public static readonly string[] VideoExtensions =
    [
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv",
        ".m4v", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp"
    ];

    private const string ProgId = "CompressIt.Video";
    private const string AppExeName = "CompressIt.exe";

    public static string? ResolveExecutablePath()
    {
        var procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) &&
            Path.GetFileName(procPath).Equals(AppExeName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(procPath))
        {
            return procPath;
        }

        var entryLocation = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryLocation))
        {
            var candidate = Path.ChangeExtension(entryLocation, ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\SystemFileAssociations\video\shell\CompressIt\command");
            var val = key?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool Register(string? exePath = null)
    {
        exePath ??= ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        string command = $"\"{exePath}\" \"%1\"";
        string icon = $"\"{exePath}\",0";

        // 1. ProgID for Open With
        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progKey.SetValue(null, "CompressIt Video");
            progKey.SetValue("FriendlyTypeName", "CompressIt Video");
            using var iconKey = progKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, icon);
            using var cmdKey = progKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(null, command);
        }

        // 2. Applications registration for Windows "Open with" dialog
        using (var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{AppExeName}"))
        {
            appKey.SetValue("FriendlyAppName", "CompressIt");
            using var iconKey = appKey.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, icon);
            using var cmdKey = appKey.CreateSubKey(@"shell\open\command");
            cmdKey.SetValue(null, command);

            using var supportedKey = appKey.CreateSubKey("SupportedTypes");
            foreach (var ext in VideoExtensions)
            {
                supportedKey.SetValue(ext, "");
            }
        }

        // 3. SystemFileAssociations\video context menu ("Open in CompressIt")
        using (var videoShellKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\video\shell\CompressIt"))
        {
            videoShellKey.SetValue(null, "Open in CompressIt");
            videoShellKey.SetValue("MUIVerb", "Open in CompressIt");
            videoShellKey.SetValue("Icon", icon);
            using var cmdKey = videoShellKey.CreateSubKey("command");
            cmdKey.SetValue(null, command);
        }

        // 4. Per-extension OpenWithProgids + SystemFileAssociations fallback
        foreach (var ext in VideoExtensions)
        {
            using (var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
            {
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var extShell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\CompressIt"))
            {
                extShell.SetValue(null, "Open in CompressIt");
                extShell.SetValue("MUIVerb", "Open in CompressIt");
                extShell.SetValue("Icon", icon);
                using var cmdKey = extShell.CreateSubKey("command");
                cmdKey.SetValue(null, command);
            }
        }

        return true;
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{AppExeName}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\video\shell\CompressIt", throwOnMissingSubKey: false);

            foreach (var ext in VideoExtensions)
            {
                using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\CompressIt", throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
