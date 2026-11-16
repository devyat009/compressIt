namespace CompressIt.Core.Models;

public enum VideoCodec
{
    H264,
    H265,
    AV1,
    VP9
}

public enum EncodingSpeed
{
    Ultrafast,
    Fast,
    Balanced,
    Slow,
    VerySlow
}

public enum HardwareVendor
{
    None,
    Nvidia,
    Amd,
    Intel
}

public enum AudioMode
{
    Copy,
    Aac128,
    Aac192,
    Mute
}

public enum OutputResolution
{
    Original,
    P1080,
    P720,
    P480
}

public enum ContainerFormat
{
    Auto,
    Mp4,
    Mkv,
    Mov,
    Webm
}

public sealed class CompressionOptions
{
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public EncodingSpeed Speed { get; set; } = EncodingSpeed.Balanced;
    public int? TargetFps { get; set; } = null; // null = Original
    public int QualityPercent { get; set; } = 85; // 1..100
    public AudioMode Audio { get; set; } = AudioMode.Aac192;
    public OutputResolution Resolution { get; set; } = OutputResolution.Original;
    public ContainerFormat Container { get; set; } = ContainerFormat.Auto;

    public CompressionOptions Clone() => new()
    {
        Codec = Codec,
        Speed = Speed,
        TargetFps = TargetFps,
        QualityPercent = QualityPercent,
        Audio = Audio,
        Resolution = Resolution,
        Container = Container
    };
}

public sealed class VideoSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Index { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool IsIncluded { get; set; } = true;

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

public sealed class VideoMetadata
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string VideoCodec { get; init; } = "unknown";
    public string? AudioCodec { get; init; }
    public bool HasAudio { get; init; }
    public long BitrateBps { get; init; }
}

public sealed class HardwareCapabilities
{
    public List<string> GpuNames { get; init; } = [];
    public HardwareVendor DetectedVendor { get; init; } = HardwareVendor.None;
    public HashSet<string> AvailableEncoders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SupportsGpuEncoding =>
        AvailableEncoders.Any(e =>
            e.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ||
            e.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ||
            e.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase));

    public string PrimaryGpuDisplayName =>
        GpuNames.FirstOrDefault() ?? "No dedicated GPU detected";
}

public sealed class ExportRequest
{
    public required VideoMetadata SourceVideo { get; init; }
    public required string OutputDirectory { get; init; }
    public required string OutputBaseName { get; init; }
    public ContainerFormat Container { get; init; } = ContainerFormat.Auto;
    public bool MergeSegments { get; init; } = true;
    public required IReadOnlyList<VideoSegment> Segments { get; init; }
    public required CompressionOptions Options { get; init; }
    public bool UseGpu { get; init; } = true;
    public required HardwareCapabilities Hardware { get; init; }
}

public sealed class ExportProgress
{
    public double Percent { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string SpeedText { get; init; } = string.Empty;
    public string EtaText { get; init; } = string.Empty;
    public int CurrentSegmentIndex { get; init; }
    public int TotalSegments { get; init; }
}

public sealed class CompressionResult
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public string? ErrorMessage { get; init; }
    public long OriginalSizeBytes { get; init; }
    public long CompressedSizeBytes { get; init; }
    public List<string> OutputFiles { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
    public bool WasMerged { get; init; }
    public string UsedEncoder { get; init; } = string.Empty;

    public double SavedPercent =>
        OriginalSizeBytes <= 0
            ? 0
            : (1.0 - (double)CompressedSizeBytes / OriginalSizeBytes) * 100.0;
}

public sealed class FfmpegDownloadProgress
{
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double Percent { get; init; }
    public string Status { get; init; } = string.Empty;
}
