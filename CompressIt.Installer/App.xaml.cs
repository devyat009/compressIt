using System.Windows;

namespace CompressIt.Installer;

public partial class App : Application
{
    public static bool IsSilent { get; private set; }
    public static bool ShouldRelaunch { get; private set; }
    public static bool IsUninstall { get; private set; }
    public static string? TargetInstallDir { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length; i++)
        {
            string arg = e.Args[i];
            if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/SILENT", StringComparison.OrdinalIgnoreCase))
            {
                IsSilent = true;
            }
            else if (arg.Equals("--relaunch", StringComparison.OrdinalIgnoreCase))
            {
                ShouldRelaunch = true;
            }
            else if (arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                IsUninstall = true;
            }
            else if (arg.Equals("--install-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                TargetInstallDir = e.Args[++i];
            }
        }

        base.OnStartup(e);
    }
}
