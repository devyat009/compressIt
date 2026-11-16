using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CompressIt.Core.Models;
using CompressIt.Core.Services;

namespace CompressIt.ViewModels;

public sealed record CodecOption(VideoCodec Codec, string DisplayName, string Description);
public sealed record SpeedOption(EncodingSpeed Speed, string DisplayName);
public sealed record FpsOption(int? Fps, string DisplayName);
public sealed record ContainerOption(ContainerFormat Format, string DisplayName);
public sealed record ResolutionOption(OutputResolution Resolution, string DisplayName);
public sealed record AudioOption(AudioMode Mode, string DisplayName);

public partial class MainViewModel : ObservableObject
{
    private readonly FFmpegDownloadService _ffmpegDownloader = new();
    private readonly HardwareDetectorService _hardwareDetector = new();
    private readonly VideoProcessingService _videoProcessor = new();

    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _thumbCts;
    private string? _currentThumbTempDir;

    public ObservableCollection<CodecOption> CodecOptions { get; } =
    [
        new(VideoCodec.H264, "H.264 (AVC)", "Best compatibility across all devices"),
        new(VideoCodec.H265, "H.265 (HEVC)", "Smaller file sizes at high quality"),
        new(VideoCodec.AV1, "AV1", "Modern next-gen compression"),
        new(VideoCodec.VP9, "VP9", "Web / WebM friendly compression")
    ];

    public ObservableCollection<SpeedOption> SpeedOptions { get; } =
    [
        new(EncodingSpeed.Ultrafast, "Ultrafast"),
        new(EncodingSpeed.Fast, "Fast"),
        new(EncodingSpeed.Balanced, "Balanced"),
        new(EncodingSpeed.Slow, "Slow"),
        new(EncodingSpeed.VerySlow, "Very Slow")
    ];

    public ObservableCollection<FpsOption> FpsOptions { get; } =
    [
        new(null, "Original FPS"),
        new(60, "60 fps"),
        new(30, "30 fps"),
        new(24, "24 fps"),
        new(15, "15 fps")
    ];

    public ObservableCollection<ContainerOption> ContainerOptions { get; } =
    [
        new(ContainerFormat.Auto, "Auto (MP4 / WebM)"),
        new(ContainerFormat.Mp4, "MP4 (.mp4)"),
        new(ContainerFormat.Mkv, "MKV (.mkv)"),
        new(ContainerFormat.Mov, "MOV (.mov)"),
        new(ContainerFormat.Webm, "WebM (.webm)")
    ];

    public ObservableCollection<ResolutionOption> ResolutionOptions { get; } =
    [
        new(OutputResolution.Original, "Original"),
        new(OutputResolution.P1080, "1080p (Full HD)"),
        new(OutputResolution.P720, "720p (HD)"),
        new(OutputResolution.P480, "480p (SD)")
    ];

    public ObservableCollection<AudioOption> AudioOptions { get; } =
    [
        new(AudioMode.Aac192, "AAC 192 kbps"),
        new(AudioMode.Aac128, "AAC 128 kbps"),
        new(AudioMode.Copy, "Copy Original"),
        new(AudioMode.Mute, "Mute (No Audio)")
    ];

    public AppSettings Settings { get; private set; } = AppSettingsService.Load();
    public HardwareCapabilities Hardware { get; private set; } = new();

    [ObservableProperty] private bool _isInitializingFfmpeg = true;
    [ObservableProperty] private double _ffmpegDownloadPercent;
    [ObservableProperty] private string _ffmpegDownloadStatus = "Checking FFmpeg installation...";
    [ObservableProperty] private string? _ffmpegDownloadError;
    [ObservableProperty] private string _ffmpegVersionText = "Not checked";
    [ObservableProperty] private string _ffmpegPathDisplay = FFmpegDownloadService.DefaultFfmpegBinDirectory;

    [ObservableProperty] private bool _useGpuAcceleration = true;
    [ObservableProperty] private bool _shellIntegrationEnabled = true;
    [ObservableProperty] private string _hardwareModeBadge = "GPU (Detecting...)";
    [ObservableProperty] private string _hardwareSummaryText = "Detecting graphics hardware...";

    [ObservableProperty] private VideoMetadata? _currentVideo;
    [ObservableProperty] private bool _hasVideo;
    [ObservableProperty] private TimeSpan _videoDuration = TimeSpan.Zero;
    [ObservableProperty] private TimeSpan _playheadPosition = TimeSpan.Zero;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _volume = 85;
    [ObservableProperty] private bool _isMuted;

    [ObservableProperty] private IReadOnlyList<VideoSegment> _segments = Array.Empty<VideoSegment>();
    [ObservableProperty] private Guid? _selectedSegmentId;
    [ObservableProperty] private IReadOnlyList<string> _thumbnailPaths = Array.Empty<string>();

    [ObservableProperty] private CodecOption _selectedCodecOption;
    [ObservableProperty] private SpeedOption _selectedSpeedOption;
    [ObservableProperty] private FpsOption _selectedFpsOption;
    [ObservableProperty] private int _qualityPercent = 85;
    [ObservableProperty] private ContainerOption _selectedContainerOption;
    [ObservableProperty] private ResolutionOption _selectedResolutionOption;
    [ObservableProperty] private AudioOption _selectedAudioOption;

    [ObservableProperty] private bool _isDrawerExpanded = true;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isExportConfigOpen;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isResultOpen;

    [ObservableProperty] private bool _mergeSegments = true;
    [ObservableProperty] private string _exportOutputDirectory = string.Empty;
    [ObservableProperty] private string _exportFileName = string.Empty;
    [ObservableProperty] private double _exportProgressPercent;
    [ObservableProperty] private string _exportStatusText = "Preparing...";
    [ObservableProperty] private string _exportSpeedText = string.Empty;
    [ObservableProperty] private string _exportEtaText = string.Empty;

    [ObservableProperty] private CompressionResult? _lastResult;
    [ObservableProperty] private string? _errorBannerText;

    public bool HasErrorBanner
    {
        get => !string.IsNullOrWhiteSpace(ErrorBannerText);
        set
        {
            if (!value) ErrorBannerText = null;
        }
    }

    partial void OnErrorBannerTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorBanner));

    public bool IsAnyOverlayOpen =>
        IsInitializingFfmpeg || IsSettingsOpen || IsExportConfigOpen || IsExporting || IsResultOpen;

    public bool IsVideoPlayerVisible => HasVideo && !IsAnyOverlayOpen;

    public bool SeparateSegments
    {
        get => !MergeSegments;
        set
        {
            if (value) MergeSegments = false;
        }
    }

    public bool UseCpuEncoding
    {
        get => !UseGpuAcceleration;
        set
        {
            if (value) UseGpuAcceleration = false;
        }
    }

    public string QualityPercentLabel => $"{QualityPercent}%";
    public string QualityTierLabel => FormatHelper.GetQualityTier(QualityPercent);

    public Brush QualityTierBrush => QualityTierLabel switch
    {
        "Low" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        "Medium" => new SolidColorBrush(Color.FromRgb(56, 189, 248)),
        _ => new SolidColorBrush(Color.FromRgb(74, 222, 128))
    };

    public string ActiveEncoderHint
    {
        get
        {
            var resolved = FfmpegCommandBuilder.ResolveEncoder(
                SelectedCodecOption.Codec,
                UseGpuAcceleration,
                Hardware);

            if (!string.IsNullOrEmpty(resolved.Note)) return resolved.Note;
            return resolved.IsGpu
                ? $"GPU Accelerated: {resolved.EncoderName}"
                : $"CPU Software: {resolved.EncoderName}";
        }
    }

    public string SourceVideoSpecsText => CurrentVideo is null
        ? "No video loaded"
        : $"{CurrentVideo.Width}×{CurrentVideo.Height} • {CurrentVideo.Fps:0.##} fps • {FormatHelper.FormatFileSize(CurrentVideo.FileSizeBytes)}";

    public string TimecodeDisplay =>
        $"{FormatHelper.FormatTimecode(PlayheadPosition)} / {FormatHelper.FormatTimecode(VideoDuration)}";

    public bool CanSplitAtPlayhead => SegmentManager.CanSplitAt(Segments, PlayheadPosition);

    public string SegmentsSummaryText
    {
        get
        {
            if (Segments.Count == 0) return "No segments";
            int kept = Segments.Count(s => s.IsIncluded);
            var keptDuration = SegmentManager.GetTotalIncludedDuration(Segments);
            return $"{kept} of {Segments.Count} segment{(Segments.Count == 1 ? "" : "s")} kept • {FormatHelper.FormatTimecode(keptDuration, includeMilliseconds: false)}";
        }
    }

    public string SelectedSegmentActionText
    {
        get
        {
            var seg = Segments.FirstOrDefault(s => s.Id == SelectedSegmentId);
            if (seg is null) return "Keep / Cut";
            return seg.IsIncluded ? $"Exclude #{seg.Index}" : $"Keep #{seg.Index}";
        }
    }

    public bool CanMergeWithNext
    {
        get
        {
            if (SelectedSegmentId is null || Segments.Count < 2) return false;
            int idx = -1;
            for (int i = 0; i < Segments.Count; i++)
            {
                if (Segments[i].Id == SelectedSegmentId) { idx = i; break; }
            }
            return idx >= 0 && idx < Segments.Count - 1;
        }
    }

    public bool HasMultipleIncludedSegments => Segments.Count(s => s.IsIncluded) > 1;

    public IEnumerable<string> IncludedSegmentDescriptions =>
        Segments.Where(s => s.IsIncluded)
                .Select((s, i) => $"Part {i + 1} (Segment #{s.Index}): {FormatHelper.FormatTimecode(s.Start)} → {FormatHelper.FormatTimecode(s.End)} ({FormatHelper.FormatTimecode(s.Duration, false)})");

    public string ExportSummaryText
    {
        get
        {
            var resolved = FfmpegCommandBuilder.ResolveEncoder(SelectedCodecOption.Codec, UseGpuAcceleration, Hardware);
            string fpsLabel = SelectedFpsOption.Fps is int fps ? $"{fps} fps" : "Original FPS";
            string hwMode = resolved.IsGpu ? "GPU" : "CPU";
            return $"{SelectedCodecOption.DisplayName} ({hwMode}: {resolved.EncoderName}) • {QualityPercent}% ({QualityTierLabel}) • {SelectedSpeedOption.DisplayName} • {fpsLabel}";
        }
    }

    public string ResultOriginalSizeText => LastResult is null ? "0 MB" : FormatHelper.FormatFileSize(LastResult.OriginalSizeBytes);
    public string ResultCompressedSizeText => LastResult is null ? "0 MB" : FormatHelper.FormatFileSize(LastResult.CompressedSizeBytes);

    public string ResultSavedBadgeText
    {
        get
        {
            if (LastResult is null) return "";
            double saved = LastResult.SavedPercent;
            return saved >= 0
                ? $"-{saved:0.1}% smaller"
                : $"+{Math.Abs(saved):0.1}% larger";
        }
    }

    public string ResultDetailText
    {
        get
        {
            if (LastResult is null) return "";
            string mergeNote = LastResult.OutputFiles.Count > 1
                ? $"{LastResult.OutputFiles.Count} separate files"
                : (LastResult.WasMerged ? "Single output file" : "1 file");
            return $"{mergeNote} • Encoder: {LastResult.UsedEncoder} • Completed in {LastResult.Elapsed.TotalSeconds:0.1}s";
        }
    }

    public event Action<string>? VideoReadyToLoadInPlayer;
    public event Action<TimeSpan>? PlayerSeekRequested;
    public event Action? PlayerTogglePlayPauseRequested;
    public event Action? PlayerPauseRequested;

    public MainViewModel()
    {
        _selectedCodecOption = CodecOptions.First(c => c.Codec == Settings.DefaultCodec);
        _selectedSpeedOption = SpeedOptions.First(s => s.Speed == Settings.DefaultSpeed);
        _selectedFpsOption = FpsOptions[0];
        _qualityPercent = Math.Clamp(Settings.DefaultQualityPercent, 1, 100);
        _selectedContainerOption = ContainerOptions[0];
        _selectedResolutionOption = ResolutionOptions[0];
        _selectedAudioOption = AudioOptions[0];
        _useGpuAcceleration = Settings.UseGpuAcceleration;
        _shellIntegrationEnabled = Settings.ShellIntegrationEnabled;
    }

    partial void OnIsInitializingFfmpegChanged(bool value) => OnPropertyChangedAllOverlayStates();
    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChangedAllOverlayStates();
    partial void OnIsExportConfigOpenChanged(bool value) => OnPropertyChangedAllOverlayStates();
    partial void OnIsExportingChanged(bool value) => OnPropertyChangedAllOverlayStates();
    partial void OnIsResultOpenChanged(bool value) => OnPropertyChangedAllOverlayStates();
    partial void OnHasVideoChanged(bool value) => OnPropertyChanged(nameof(IsVideoPlayerVisible));

    partial void OnMergeSegmentsChanged(bool value) => OnPropertyChanged(nameof(SeparateSegments));

    partial void OnUseGpuAccelerationChanged(bool value)
    {
        Settings.UseGpuAcceleration = value;
        AppSettingsService.Save(Settings);
        OnPropertyChanged(nameof(UseCpuEncoding));
        UpdateHardwareLabels();
        OnPropertyChanged(nameof(ActiveEncoderHint));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnShellIntegrationEnabledChanged(bool value)
    {
        Settings.ShellIntegrationEnabled = value;
        AppSettingsService.Save(Settings);
        if (value)
        {
            ShellIntegrationService.Register();
        }
        else
        {
            ShellIntegrationService.Unregister();
        }
    }

    partial void OnQualityPercentChanged(int value)
    {
        Settings.DefaultQualityPercent = value;
        AppSettingsService.Save(Settings);
        OnPropertyChanged(nameof(QualityPercentLabel));
        OnPropertyChanged(nameof(QualityTierLabel));
        OnPropertyChanged(nameof(QualityTierBrush));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnSelectedCodecOptionChanged(CodecOption value)
    {
        Settings.DefaultCodec = value.Codec;
        AppSettingsService.Save(Settings);
        OnPropertyChanged(nameof(ActiveEncoderHint));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnSelectedSpeedOptionChanged(SpeedOption value)
    {
        Settings.DefaultSpeed = value.Speed;
        AppSettingsService.Save(Settings);
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnSelectedFpsOptionChanged(FpsOption value) => OnPropertyChanged(nameof(ExportSummaryText));

    partial void OnPlayheadPositionChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(TimecodeDisplay));
        OnPropertyChanged(nameof(CanSplitAtPlayhead));
    }

    partial void OnVideoDurationChanged(TimeSpan value) => OnPropertyChanged(nameof(TimecodeDisplay));

    partial void OnSegmentsChanged(IReadOnlyList<VideoSegment> value)
    {
        OnPropertyChanged(nameof(SegmentsSummaryText));
        OnPropertyChanged(nameof(CanSplitAtPlayhead));
        OnPropertyChanged(nameof(SelectedSegmentActionText));
        OnPropertyChanged(nameof(CanMergeWithNext));
        OnPropertyChanged(nameof(HasMultipleIncludedSegments));
        OnPropertyChanged(nameof(IncludedSegmentDescriptions));
    }

    partial void OnSelectedSegmentIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(SelectedSegmentActionText));
        OnPropertyChanged(nameof(CanMergeWithNext));
    }

    private void OnPropertyChangedAllOverlayStates()
    {
        OnPropertyChanged(nameof(IsAnyOverlayOpen));
        OnPropertyChanged(nameof(IsVideoPlayerVisible));
    }

    public async Task InitializeAsync(string? startupVideoPath)
    {
        // Auto-register Windows Explorer "Open in CompressIt" on startup if enabled.
        if (Settings.ShellIntegrationEnabled)
        {
            ShellIntegrationService.Register();
        }

        await EnsureFfmpegAndDetectHardwareAsync(forceRedownload: false);

        if (!IsInitializingFfmpeg && !string.IsNullOrWhiteSpace(startupVideoPath) && File.Exists(startupVideoPath))
        {
            await LoadVideoAsync(startupVideoPath);
        }
    }

    [RelayCommand]
    private async Task RetryFfmpegDownloadAsync()
    {
        await EnsureFfmpegAndDetectHardwareAsync(forceRedownload: false);
    }

    [RelayCommand]
    private async Task RedownloadFfmpegAsync()
    {
        IsSettingsOpen = false;
        await EnsureFfmpegAndDetectHardwareAsync(forceRedownload: true);
    }

    public async Task SetCustomFfmpegFolderAsync(string folderPath)
    {
        if (!FFmpegDownloadService.IsInstalled(folderPath))
        {
            ErrorBannerText = "Selected folder does not contain both ffmpeg.exe and ffprobe.exe.";
            return;
        }

        Settings.CustomFfmpegDirectory = folderPath;
        AppSettingsService.Save(Settings);
        await EnsureFfmpegAndDetectHardwareAsync(forceRedownload: false);
    }

    private async Task EnsureFfmpegAndDetectHardwareAsync(bool forceRedownload)
    {
        IsInitializingFfmpeg = true;
        FfmpegDownloadError = null;

        try
        {
            var (ffmpegPath, ffprobePath) = FFmpegDownloadService.ResolvePaths(Settings.CustomFfmpegDirectory);

            if (forceRedownload || !File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            {
                var progress = new Progress<FfmpegDownloadProgress>(p =>
                {
                    FfmpegDownloadPercent = p.Percent;
                    FfmpegDownloadStatus = p.Status;
                });

                await _ffmpegDownloader.EnsureInstalledAsync(progress, forceRedownload);
                Settings.CustomFfmpegDirectory = null;
                AppSettingsService.Save(Settings);
                (ffmpegPath, _) = FFmpegDownloadService.ResolvePaths();
            }

            FfmpegPathDisplay = Path.GetDirectoryName(ffmpegPath) ?? ffmpegPath;
            FfmpegDownloadPercent = 98;
            FfmpegDownloadStatus = "Detecting GPU hardware encoders...";

            var ver = await _ffmpegDownloader.GetVersionAsync(ffmpegPath);
            FfmpegVersionText = ver ?? "Installed";

            Hardware = await _hardwareDetector.DetectAsync(ffmpegPath);
            UpdateHardwareLabels();
            OnPropertyChanged(nameof(ActiveEncoderHint));
            OnPropertyChanged(nameof(ExportSummaryText));

            FfmpegDownloadPercent = 100;
            IsInitializingFfmpeg = false;
        }
        catch (Exception ex)
        {
            FfmpegDownloadError = ex.Message;
            FfmpegDownloadStatus = "FFmpeg setup failed.";
        }
    }

    private void UpdateHardwareLabels()
    {
        if (UseGpuAcceleration && Hardware.SupportsGpuEncoding)
        {
            string vendorLabel = Hardware.DetectedVendor switch
            {
                HardwareVendor.Amd => "AMD AMF",
                HardwareVendor.Nvidia => "NVIDIA NVENC",
                HardwareVendor.Intel => "Intel QSV",
                _ => "GPU"
            };
            HardwareModeBadge = $"GPU: {vendorLabel}";
            var gpuEncs = Hardware.AvailableEncoders.Where(e =>
                e.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ||
                e.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ||
                e.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase));
            HardwareSummaryText = $"{Hardware.PrimaryGpuDisplayName}\nVerified GPU encoders: {string.Join(", ", gpuEncs)}";
        }
        else if (UseGpuAcceleration && !Hardware.SupportsGpuEncoding)
        {
            HardwareModeBadge = "CPU (No GPU encoder)";
            HardwareSummaryText = $"{Hardware.PrimaryGpuDisplayName}\nNo compatible hardware encoder verified; falling back to CPU.";
        }
        else
        {
            HardwareModeBadge = "CPU: Software";
            HardwareSummaryText = $"CPU mode active (libx264 / libx265 / libsvtav1 / libvpx-vp9).\nDetected GPU: {Hardware.PrimaryGpuDisplayName}";
        }
    }

    public async Task LoadVideoAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            ErrorBannerText = $"File not found: {filePath}";
            return;
        }

        ErrorBannerText = null;
        IsResultOpen = false;
        IsExportConfigOpen = false;

        var (ffmpegPath, ffprobePath) = FFmpegDownloadService.ResolvePaths(Settings.CustomFfmpegDirectory);
        if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
        {
            await EnsureFfmpegAndDetectHardwareAsync(forceRedownload: false);
            if (IsInitializingFfmpeg) return;
            (ffmpegPath, ffprobePath) = FFmpegDownloadService.ResolvePaths(Settings.CustomFfmpegDirectory);
        }

        try
        {
            var meta = await _videoProcessor.ProbeAsync(ffprobePath, filePath);
            CurrentVideo = meta;
            HasVideo = true;
            VideoDuration = meta.Duration;
            PlayheadPosition = TimeSpan.Zero;

            var initialSegments = SegmentManager.CreateInitial(meta.Duration);
            Segments = initialSegments;
            SelectedSegmentId = initialSegments.FirstOrDefault()?.Id;

            ExportOutputDirectory = Path.GetDirectoryName(meta.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            ExportFileName = $"{Path.GetFileNameWithoutExtension(meta.FileName)}_compressed";

            OnPropertyChanged(nameof(SourceVideoSpecsText));
            VideoReadyToLoadInPlayer?.Invoke(filePath);

            _ = ExtractThumbnailsInBackgroundAsync(ffmpegPath, meta);
        }
        catch (Exception ex)
        {
            ErrorBannerText = $"Could not open video: {ex.Message}";
        }
    }

    private async Task ExtractThumbnailsInBackgroundAsync(string ffmpegPath, VideoMetadata meta)
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        CleanupThumbnailTempDir();
        _currentThumbTempDir = Path.Combine(Path.GetTempPath(), "CompressIt_Thumbs", Guid.NewGuid().ToString("N"));
        ThumbnailPaths = Array.Empty<string>();

        try
        {
            var thumbs = await _videoProcessor.ExtractThumbnailsAsync(ffmpegPath, meta.FilePath, meta.Duration, 12, _currentThumbTempDir, ct);
            if (!ct.IsCancellationRequested)
            {
                ThumbnailPaths = thumbs;
            }
        }
        catch
        {
            // Thumbnails are decorative; ignore failures.
        }
    }

    public void CleanupThumbnailTempDir()
    {
        if (string.IsNullOrEmpty(_currentThumbTempDir)) return;
        try
        {
            if (Directory.Exists(_currentThumbTempDir))
            {
                Directory.Delete(_currentThumbTempDir, recursive: true);
            }
        }
        catch { }
        _currentThumbTempDir = null;
    }

    [RelayCommand]
    private void TogglePlayPause() => PlayerTogglePlayPauseRequested?.Invoke();

    [RelayCommand]
    private void StepBackward()
    {
        if (!HasVideo) return;
        var target = PlayheadPosition - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        PlayheadPosition = target;
        PlayerSeekRequested?.Invoke(target);
    }

    [RelayCommand]
    private void StepForward()
    {
        if (!HasVideo) return;
        var target = PlayheadPosition + TimeSpan.FromSeconds(1);
        if (target > VideoDuration) target = VideoDuration;
        PlayheadPosition = target;
        PlayerSeekRequested?.Invoke(target);
    }

    [RelayCommand]
    private void CutAtPlayhead()
    {
        if (!HasVideo || !CanSplitAtPlayhead) return;

        var updated = SegmentManager.SplitAt(Segments, PlayheadPosition);
        Segments = updated;

        var hit = updated.FirstOrDefault(s => PlayheadPosition >= s.Start && PlayheadPosition <= s.End) ?? updated.LastOrDefault();
        SelectedSegmentId = hit?.Id;
    }

    [RelayCommand]
    private void ToggleSelectedSegmentInclude()
    {
        if (SelectedSegmentId is null) return;
        Segments = SegmentManager.ToggleInclude(Segments, SelectedSegmentId.Value);
    }

    public void ToggleSegmentInclude(Guid segmentId)
    {
        Segments = SegmentManager.ToggleInclude(Segments, segmentId);
        SelectedSegmentId = segmentId;
    }

    [RelayCommand]
    private void MergeSelectedWithNext()
    {
        if (SelectedSegmentId is null || !CanMergeWithNext) return;
        Segments = SegmentManager.MergeWithNext(Segments, SelectedSegmentId.Value);
    }

    [RelayCommand]
    private void ResetCuts()
    {
        if (CurrentVideo is null) return;
        var initial = SegmentManager.CreateInitial(CurrentVideo.Duration);
        Segments = initial;
        SelectedSegmentId = initial.FirstOrDefault()?.Id;
    }

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerExpanded = !IsDrawerExpanded;

    [RelayCommand]
    private void OpenSettings()
    {
        PlayerPauseRequested?.Invoke();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void OpenExportConfig()
    {
        if (CurrentVideo is null) return;

        if (!Segments.Any(s => s.IsIncluded))
        {
            ErrorBannerText = "Please keep at least one segment before exporting.";
            return;
        }

        ErrorBannerText = null;
        PlayerPauseRequested?.Invoke();
        OnPropertyChanged(nameof(HasMultipleIncludedSegments));
        OnPropertyChanged(nameof(IncludedSegmentDescriptions));
        OnPropertyChanged(nameof(ExportSummaryText));
        IsExportConfigOpen = true;
    }

    [RelayCommand]
    private void CloseExportConfig() => IsExportConfigOpen = false;

    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (CurrentVideo is null) return;

        var (ffmpegPath, _) = FFmpegDownloadService.ResolvePaths(Settings.CustomFfmpegDirectory);
        if (!File.Exists(ffmpegPath))
        {
            ErrorBannerText = "FFmpeg executable is missing.";
            return;
        }

        IsExportConfigOpen = false;
        IsExporting = true;
        ExportProgressPercent = 0;
        ExportStatusText = "Starting FFmpeg...";
        ExportSpeedText = "";
        ExportEtaText = "";

        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();

        var options = new CompressionOptions
        {
            Codec = SelectedCodecOption.Codec,
            Speed = SelectedSpeedOption.Speed,
            TargetFps = SelectedFpsOption.Fps,
            QualityPercent = QualityPercent,
            Container = SelectedContainerOption.Format,
            Resolution = SelectedResolutionOption.Resolution,
            Audio = SelectedAudioOption.Mode
        };

        var request = new ExportRequest
        {
            SourceVideo = CurrentVideo,
            OutputDirectory = string.IsNullOrWhiteSpace(ExportOutputDirectory)
                ? (Path.GetDirectoryName(CurrentVideo.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
                : ExportOutputDirectory,
            OutputBaseName = string.IsNullOrWhiteSpace(ExportFileName)
                ? $"{Path.GetFileNameWithoutExtension(CurrentVideo.FileName)}_compressed"
                : ExportFileName.Trim(),
            Container = SelectedContainerOption.Format,
            MergeSegments = MergeSegments,
            Segments = Segments,
            Options = options,
            UseGpu = UseGpuAcceleration,
            Hardware = Hardware
        };

        var progress = new Progress<ExportProgress>(p =>
        {
            ExportProgressPercent = p.Percent;
            ExportStatusText = p.StatusText;
            ExportSpeedText = p.SpeedText;
            ExportEtaText = p.EtaText;
        });

        var result = await _videoProcessor.ExportAsync(request, ffmpegPath, progress, _exportCts.Token);
        IsExporting = false;

        if (result.WasCancelled)
        {
            return;
        }

        if (!result.Success)
        {
            ErrorBannerText = result.ErrorMessage ?? "Compression failed.";
            return;
        }

        LastResult = result;
        OnPropertyChanged(nameof(ResultOriginalSizeText));
        OnPropertyChanged(nameof(ResultCompressedSizeText));
        OnPropertyChanged(nameof(ResultSavedBadgeText));
        OnPropertyChanged(nameof(ResultDetailText));
        IsResultOpen = true;
    }

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    [RelayCommand]
    private void CloseResult() => IsResultOpen = false;

    [RelayCommand]
    private void OpenFirstOutputFile()
    {
        var file = LastResult?.OutputFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowOutputInExplorer()
    {
        var file = LastResult?.OutputFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void DismissError() => ErrorBannerText = null;
}
