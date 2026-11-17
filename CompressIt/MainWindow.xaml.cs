using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CompressIt.Core.Services;
using CompressIt.ViewModels;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace CompressIt;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private Task? _libVlcInitTask;
    private bool _updatingPositionFromPlayer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _vm.VideoReadyToLoadInPlayer += LoadVideoInPlayer;
        _vm.PlayerSeekRequested += SeekPlayer;
        _vm.PlayerTogglePlayPauseRequested += TogglePlayPause;
        _vm.PlayerPauseRequested += PausePlayer;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Volume) && _mediaPlayer is not null)
            {
                _mediaPlayer.Volume = _vm.Volume;
            }
        };

        VideoTimeline.SeekRequested += ts => SeekPlayer(ts);
        VideoTimeline.SegmentSelected += id => _vm.SelectedSegmentId = id;
        VideoTimeline.SegmentIncludeToggled += id => _vm.ToggleSegmentInclude(id);
        VideoTimeline.SegmentBoundaryAdjustRequested += (id, isStart, ts) => _vm.AdjustSegmentBoundary(id, isStart, ts);

        App.ExternalFileRequested += OnExternalFileRequested;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _libVlcInitTask = InitializeLibVlcAsync();
        await _vm.InitializeAsync(App.InitialFileToOpen);
    }

    private async Task InitializeLibVlcAsync()
    {
        try
        {
            var vlc = await Task.Run(() => new LibVLC("--no-video-title-show", "--no-plugins-cache"));
            var player = new MediaPlayer(vlc)
            {
                Volume = _vm.Volume
            };

            player.TimeChanged += (_, args) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _updatingPositionFromPlayer = true;
                    _vm.PlayheadPosition = TimeSpan.FromMilliseconds(Math.Max(0, args.Time));
                    _updatingPositionFromPlayer = false;
                });
            };

            player.Playing += (_, _) => Dispatcher.BeginInvoke(() => _vm.IsPlaying = true);
            player.Paused += (_, _) => Dispatcher.BeginInvoke(() => _vm.IsPlaying = false);
            player.Stopped += (_, _) => Dispatcher.BeginInvoke(() => _vm.IsPlaying = false);
            player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _vm.IsPlaying = false);

            _libVlc = vlc;
            _mediaPlayer = player;
            MediaVideoView.MediaPlayer = _mediaPlayer;
        }
        catch (Exception ex)
        {
            _vm.ErrorBannerText = $"Video preview engine initialization warning: {ex.Message}";
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        App.ExternalFileRequested -= OnExternalFileRequested;
        _vm.CleanupThumbnailTempDir();

        _mediaPlayer?.Stop();
        _currentMedia?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }

    private async void OnExternalFileRequested(string filePath)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        await _vm.LoadVideoAsync(filePath);
    }

    private async void LoadVideoInPlayer(string filePath)
    {
        if (_libVlcInitTask is not null)
        {
            await _libVlcInitTask;
        }

        if (_libVlc is null || _mediaPlayer is null)
        {
            _vm.IsLoadingVideo = false;
            return;
        }

        _mediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = new Media(_libVlc, new Uri(filePath));
        _mediaPlayer.Media = _currentMedia;
        _mediaPlayer.Mute = _vm.IsMuted;
        _mediaPlayer.Volume = _vm.Volume;

        var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnVout(object? s, MediaPlayerVoutEventArgs e)
        {
            if (e.Count > 0) firstFrameTcs.TrySetResult(true);
        }

        void OnEncounteredError(object? s, EventArgs e) => firstFrameTcs.TrySetResult(false);

        _mediaPlayer.Vout += OnVout;
        _mediaPlayer.EncounteredError += OnEncounteredError;

        try
        {
            _mediaPlayer.Play();
            await Task.WhenAny(firstFrameTcs.Task, Task.Delay(3000));
            // Give LibVLC a brief beat to render the first video frame before pausing & revealing
            await Task.Delay(180);

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.SetPause(true);
                _mediaPlayer.Time = 0;
                _vm.PlayheadPosition = TimeSpan.Zero;
                _vm.IsPlaying = false;
            }
        }
        finally
        {
            _mediaPlayer.Vout -= OnVout;
            _mediaPlayer.EncounteredError -= OnEncounteredError;
            _vm.IsLoadingVideo = false;
        }
    }

    private void SeekPlayer(TimeSpan target)
    {
        if (_mediaPlayer is null || _currentMedia is null || _updatingPositionFromPlayer) return;

        long targetMs = (long)Math.Clamp(target.TotalMilliseconds, 0, _vm.VideoDuration.TotalMilliseconds);
        if (_mediaPlayer.State == VLCState.Ended || _mediaPlayer.State == VLCState.Stopped)
        {
            _mediaPlayer.Play();
            _mediaPlayer.SetPause(true);
        }
        _mediaPlayer.Time = targetMs;
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer is null || _currentMedia is null) return;

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
        }
        else
        {
            if (_mediaPlayer.State == VLCState.Ended)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }
    }

    private void PausePlayer()
    {
        if (_mediaPlayer is not null && _mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
        }
    }

    private async void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Video File",
            Filter = "Video Files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.flv;*.ts;*.mts;*.mpg;*.mpeg;*.3gp|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            await _vm.LoadVideoAsync(dialog.FileName);
        }
    }

    private void ToggleMuteButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsMuted = !_vm.IsMuted;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Mute = _vm.IsMuted;
        }
    }

    private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Output Folder",
            InitialDirectory = Directory.Exists(_vm.ExportOutputDirectory)
                ? _vm.ExportOutputDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog() == true)
        {
            _vm.ExportOutputDirectory = dialog.FolderName;
        }
    }

    private async void BrowseFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder containing ffmpeg.exe and ffprobe.exe"
        };

        if (dialog.ShowDialog() == true)
        {
            await _vm.SetCustomFfmpegFolderAsync(dialog.FolderName);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is { Length: > 0 } && IsSupportedVideo(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && IsSupportedVideo(files[0]))
        {
            await _vm.LoadVideoAsync(files[0]);
        }
    }

    private static bool IsSupportedVideo(string path)
    {
        var ext = Path.GetExtension(path);
        return ShellIntegrationService.VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox || _vm.IsAnyOverlayOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C:
            case Key.S:
                if (_vm.CanSplitAtPlayhead)
                {
                    _vm.CutAtPlayheadCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Left:
                _vm.StepBackwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                _vm.StepForwardCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
