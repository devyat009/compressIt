using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CompressIt.Core.Models;
using CompressIt.Core.Services;

namespace CompressIt.ViewModels;

public partial class BatchVideoItemViewModel : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public long FileSizeBytes { get; }
    public string OriginalSizeText { get; }

    [ObservableProperty] private VideoMetadata? _metadata;
    [ObservableProperty] private string _durationText = "--:--";
    [ObservableProperty] private string _specsText = string.Empty;
    [ObservableProperty] private BitmapImage? _thumbnailImage;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _useCustomSettings;
    [ObservableProperty] private CompressionOptions _customOptions;

    [ObservableProperty] private BatchJobStatus _status = BatchJobStatus.Idle;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private long _compressedSizeBytes;
    [ObservableProperty] private string _outputFilePath = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    public bool IsQueued => Status == BatchJobStatus.Queued;
    public bool IsCompressing => Status == BatchJobStatus.Compressing;
    public bool IsCompleted => Status == BatchJobStatus.Completed;
    public bool IsFailed => Status == BatchJobStatus.Failed;

    public double CardOpacity => IsSelected ? 1.0 : 0.55;
    public Brush CardBorderBrush => IsSelected
        ? new SolidColorBrush(Color.FromArgb(140, 74, 222, 128))
        : new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(CardBorderBrush));
    }

    public string CompressedSizeText => FormatHelper.FormatFileSize(CompressedSizeBytes);

    public string SavedPercentText
    {
        get
        {
            if (FileSizeBytes <= 0 || CompressedSizeBytes <= 0) return "";
            double saved = (1.0 - (double)CompressedSizeBytes / FileSizeBytes) * 100.0;
            return saved >= 0 ? $"-{saved:0.0}%" : $"+{Math.Abs(saved):0.0}%";
        }
    }

    public string SettingsSummaryBadge
    {
        get
        {
            if (!UseCustomSettings) return "Global Preset";
            if (!CustomOptions.EnableCompression) return "Custom: Lossless Cut";
            return $"Custom: {CustomOptions.Codec} • {CustomOptions.QualityPercent}%";
        }
    }

    public BatchVideoItemViewModel(string filePath, CompressionOptions globalSnapshot)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileSizeBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        OriginalSizeText = FormatHelper.FormatFileSize(FileSizeBytes);
        SpecsText = OriginalSizeText;
        _customOptions = globalSnapshot.Clone();
    }

    partial void OnStatusChanged(BatchJobStatus value)
    {
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsCompressing));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnCompressedSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(CompressedSizeText));
        OnPropertyChanged(nameof(SavedPercentText));
    }

    partial void OnUseCustomSettingsChanged(bool value)
    {
        OnPropertyChanged(nameof(SettingsSummaryBadge));
    }

    public void RefreshBadge() => OnPropertyChanged(nameof(SettingsSummaryBadge));

    public void ApplyMetadata(VideoMetadata meta)
    {
        Metadata = meta;
        DurationText = FormatHelper.FormatTimecode(meta.Duration, includeMilliseconds: false);
        SpecsText = $"{meta.Width}×{meta.Height} • {OriginalSizeText}";
    }

    public void LoadThumbnailFromFile(string imagePath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            ThumbnailImage = bmp;
        }
        catch
        {
            // Keep placeholder if thumbnail decoding fails.
        }
    }
}
