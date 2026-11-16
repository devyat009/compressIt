using System.Diagnostics;
using System.Management;
using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public sealed class HardwareDetectorService
{
    private static readonly string[] NvidiaEncoders = ["h264_nvenc", "hevc_nvenc", "av1_nvenc"];
    private static readonly string[] AmdEncoders = ["h264_amf", "hevc_amf", "av1_amf"];
    private static readonly string[] IntelEncoders = ["h264_qsv", "hevc_qsv", "av1_qsv"];
    private static readonly string[] CpuEncoders = ["libx264", "libx265", "libsvtav1", "libaom-av1", "libvpx-vp9"];

    public static List<string> QuerySystemGpus()
    {
        var physical = new List<string>();
        var virtualGpus = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                {
                    virtualGpus.Add(name);
                }
                else
                {
                    physical.Add(name);
                }
            }
        }
        catch
        {
            // Ignore WMI failures on restricted systems.
        }

        physical.AddRange(virtualGpus);
        return physical;
    }

    public static HardwareVendor InferPrimaryVendor(IEnumerable<string> gpuNames)
    {
        foreach (var name in gpuNames)
        {
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
            {
                return HardwareVendor.Nvidia;
            }

            if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                return HardwareVendor.Amd;
            }

            if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return HardwareVendor.Intel;
            }
        }

        return HardwareVendor.None;
    }

    public async Task<HardwareCapabilities> DetectAsync(string ffmpegPath, CancellationToken ct = default)
    {
        var gpus = await Task.Run(QuerySystemGpus, ct);
        var vendor = InferPrimaryVendor(gpus);
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(ffmpegPath))
        {
            return new HardwareCapabilities
            {
                GpuNames = gpus,
                DetectedVendor = vendor,
                AvailableEncoders = available
            };
        }

        var listedEncoders = await GetListedEncodersAsync(ffmpegPath, ct);

        foreach (var cpu in CpuEncoders)
        {
            if (listedEncoders.Contains(cpu))
            {
                available.Add(cpu);
            }
        }

        // Order hardware tests by detected vendor first, then check remaining vendors as fallback.
        var candidateGroups = vendor switch
        {
            HardwareVendor.Nvidia => new[] { NvidiaEncoders, AmdEncoders, IntelEncoders },
            HardwareVendor.Amd => new[] { AmdEncoders, NvidiaEncoders, IntelEncoders },
            HardwareVendor.Intel => new[] { IntelEncoders, NvidiaEncoders, AmdEncoders },
            _ => new[] { NvidiaEncoders, AmdEncoders, IntelEncoders }
        };

        foreach (var group in candidateGroups)
        {
            var present = group.Where(listedEncoders.Contains).ToArray();
            if (present.Length == 0) continue;

            // Test the encoders in parallel within the vendor group to verify driver/hardware support quickly.
            var testTasks = present.Select(async enc =>
            {
                bool ok = await ProbeEncoderWorksAsync(ffmpegPath, enc, ct);
                return (enc, ok);
            });

            var results = await Task.WhenAll(testTasks);
            foreach (var (enc, ok) in results)
            {
                if (ok) available.Add(enc);
            }
        }

        // Refine vendor if WMI missed it or preferred GPU vendor actually succeeded.
        if (vendor == HardwareVendor.None)
        {
            if (available.Any(e => e.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))) vendor = HardwareVendor.Nvidia;
            else if (available.Any(e => e.EndsWith("_amf", StringComparison.OrdinalIgnoreCase))) vendor = HardwareVendor.Amd;
            else if (available.Any(e => e.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))) vendor = HardwareVendor.Intel;
        }

        return new HardwareCapabilities
        {
            GpuNames = gpus,
            DetectedVendor = vendor,
            AvailableEncoders = available
        };
    }

    private static async Task<HashSet<string>> GetListedEncodersAsync(string ffmpegPath, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                // Format: " V..... libx264  ..."
                if (trimmed.Length < 8 || trimmed[0] != 'V') continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    result.Add(parts[1]);
                }
            }
        }
        catch
        {
            // Return empty set on failure.
        }

        return result;
    }

    private static async Task<bool> ProbeEncoderWorksAsync(string ffmpegPath, string encoder, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:d=0.04 -frames:v 1 -c:v {encoder} -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
