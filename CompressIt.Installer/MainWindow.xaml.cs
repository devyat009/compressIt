using System.Diagnostics;
using System.IO;
using System.Windows;
using CompressIt.Core.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace CompressIt.Installer;

public partial class MainWindow : FluentWindow
{
    private bool _isCompleted;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var ver = UpdateService.GetCurrentVersion();
        VersionSubtitle.Text = $"{UpdateService.FormatVersion(ver)} • Video Cutter & GPU Compressor";
        InstallPathBox.Text = string.IsNullOrWhiteSpace(App.TargetInstallDir)
            ? InstallerService.DefaultInstallDirectory
            : App.TargetInstallDir;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.IsUninstall)
        {
            HeaderTitle.Text = "Uninstall CompressIt";
            OptionsPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Ready to remove CompressIt from your PC.";
            PrimaryBtnText.Text = "Uninstall";

            if (App.IsSilent)
            {
                await RunUninstallAsync();
            }
            return;
        }

        if (App.IsSilent)
        {
            HeaderTitle.Text = "Updating CompressIt";
            await RunInstallAsync(autoLaunchOnComplete: App.ShouldRelaunch);
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (_isCompleted)
        {
            if (!App.IsUninstall)
            {
                LaunchInstalledApp();
            }
            Close();
            return;
        }

        if (App.IsUninstall)
        {
            await RunUninstallAsync();
        }
        else
        {
            await RunInstallAsync(autoLaunchOnComplete: false);
        }
    }

    private async Task RunInstallAsync(bool autoLaunchOnComplete)
    {
        _isBusy = true;
        OptionsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        PrimaryBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        string targetDir = InstallPathBox.Text.Trim();
        var progress = new Progress<(double Percent, string Status)>(p =>
        {
            InstallProgressBar.Value = p.Percent;
            PercentText.Text = $"{p.Percent:0}%";
            StatusText.Text = p.Status;
        });

        try
        {
            await InstallerService.InstallAsync(
                targetDir,
                createDesktopShortcut: DesktopCheck.IsChecked == true && !App.IsSilent,
                createStartMenuShortcut: StartMenuCheck.IsChecked == true,
                registerShellIntegration: ShellIntegrationCheck.IsChecked == true,
                progress);

            _isCompleted = true;
            PrimaryBtnText.Text = "Launch CompressIt";
            PrimaryBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;

            if (autoLaunchOnComplete)
            {
                LaunchInstalledApp();
                Close();
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            PrimaryBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RunUninstallAsync()
    {
        _isBusy = true;
        PrimaryBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;

        var progress = new Progress<(double Percent, string Status)>(p =>
        {
            InstallProgressBar.Value = p.Percent;
            PercentText.Text = $"{p.Percent:0}%";
            StatusText.Text = p.Status;
        });

        await InstallerService.UninstallAsync(App.TargetInstallDir, progress);
        _isCompleted = true;
        PrimaryBtnText.Text = "Close";
        PrimaryBtn.IsEnabled = true;
        _isBusy = false;

        if (App.IsSilent)
        {
            Close();
        }
    }

    private void LaunchInstalledApp()
    {
        string exe = Path.Combine(InstallPathBox.Text.Trim(), "CompressIt.exe");
        if (File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose Install Folder",
            InitialDirectory = Directory.Exists(InstallPathBox.Text)
                ? InstallPathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog() == true)
        {
            InstallPathBox.Text = Path.Combine(dlg.FolderName, "CompressIt");
        }
    }

    private void GitHubAuthor_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(UpdateService.AuthorGitHubUrl) { UseShellExecute = true });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
