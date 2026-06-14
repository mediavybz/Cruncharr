using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cruncharr.API.Controllers;

/// <summary>
/// Hardware-acceleration discovery: reports the ffmpeg hwaccel methods available in
/// this build and whether the matching device was passed into the container, so the
/// UI can offer a real picker instead of a free-text field.
/// </summary>
[ApiController]
[Route("api/v1/hardware")]
public class HardwareController : ControllerBase
{
    private readonly ILogger<HardwareController> _logger;

    public HardwareController(ILogger<HardwareController> logger)
    {
        _logger = logger;
    }

    public class HwAccelOption
    {
        public string Value { get; set; } = "";
        public string Label { get; set; } = "";
        public bool DeviceFound { get; set; }
        public string? Device { get; set; }
    }

    [HttpGet("accelerators")]
    public async Task<ActionResult<IEnumerable<HwAccelOption>>> GetAccelerators()
    {
        var options = new List<HwAccelOption>
        {
            new() { Value = "none", Label = "None (CPU)", DeviceFound = true }
        };

        try
        {
            // Detect the ACTUAL GPU(s) passed into the container by reading the PCI vendor
            // of each /dev/dri render node (/sys/class/drm/<node>/device/vendor), plus any
            // NVIDIA device. This is what lets us show only the accelerators the connected
            // hardware supports (e.g. an Intel card offers VAAPI + QSV, never AMD's AMF).
            var gpus = DetectGpus();
            var intel = gpus.FirstOrDefault(g => g.Vendor == "intel");
            var amd = gpus.FirstOrDefault(g => g.Vendor == "amd");
            var nvidia = gpus.FirstOrDefault(g => g.Vendor == "nvidia");
            // A render node whose vendor we couldn't read: VAAPI still works generically.
            var unknownRender = gpus.FirstOrDefault(g => g.Vendor == "unknown" && g.Device.StartsWith("/dev/dri", StringComparison.Ordinal));

            bool hasIntel = intel.Device != null;
            bool hasAmd = amd.Device != null;
            bool hasNvidia = nvidia.Device != null;
            bool anyVaapi = hasIntel || hasAmd || unknownRender.Device != null;
            var vaapiDevice = intel.Device ?? amd.Device ?? unknownRender.Device;

            var methods = (await GetFfmpegHwAccelsAsync()).ToHashSet();

            void Add(string value, string label, string? device) =>
                options.Add(new HwAccelOption { Value = value, Label = device != null ? $"{label} ({device})" : label, DeviceFound = true, Device = device });

            // Per-vendor, only when ffmpeg supports the method AND the matching GPU exists.
            if (methods.Contains("cuda") && hasNvidia) Add("cuda", "NVIDIA (CUDA)", nvidia.Device);
            else if (methods.Contains("nvdec") && hasNvidia) Add("nvdec", "NVIDIA (NVDEC)", nvidia.Device);
            if (methods.Contains("qsv") && hasIntel) Add("qsv", "Intel QuickSync (QSV)", intel.Device);
            if (methods.Contains("amf") && hasAmd) Add("amf", "AMD (AMF)", amd.Device);
            if (methods.Contains("vaapi") && anyVaapi)
            {
                var vendorName = hasIntel ? "Intel" : hasAmd ? "AMD" : "Intel/AMD";
                Add("vaapi", $"{vendorName} (VAAPI)", vaapiDevice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate hardware accelerators");
        }

        return Ok(options);
    }

    // Enumerate GPUs actually present in the container: each /dev/dri render node tagged
    // with its PCI vendor (from /sys/class/drm/<node>/device/vendor), plus any NVIDIA
    // device. Vendor IDs: Intel 0x8086, AMD 0x1002, NVIDIA 0x10de.
    private List<(string Vendor, string Device)> DetectGpus()
    {
        var gpus = new List<(string Vendor, string Device)>();
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                foreach (var node in Directory.GetFiles("/dev/dri")
                             .Where(f => Path.GetFileName(f).StartsWith("renderD", StringComparison.Ordinal))
                             .OrderBy(f => f))
                {
                    var name = Path.GetFileName(node);
                    var vendor = "unknown";
                    try
                    {
                        var vendorFile = $"/sys/class/drm/{name}/device/vendor";
                        if (System.IO.File.Exists(vendorFile))
                        {
                            var id = (System.IO.File.ReadAllText(vendorFile).Trim()).ToLowerInvariant();
                            vendor = id switch
                            {
                                "0x8086" => "intel",
                                "0x1002" => "amd",
                                "0x10de" => "nvidia",
                                _ => "unknown"
                            };
                        }
                    }
                    catch { /* vendor unreadable - leave as unknown */ }
                    gpus.Add((vendor, node));
                }
            }

            // NVIDIA's proprietary driver exposes /dev/nvidia0 (etc.) rather than a DRM node.
            bool nvDev = System.IO.File.Exists("/dev/nvidia0")
                || (Directory.Exists("/dev") && Directory.GetFiles("/dev")
                        .Select(Path.GetFileName)
                        .Any(n => n != null && n.StartsWith("nvidia", StringComparison.Ordinal) && n.Length > 6 && char.IsDigit(n[^1])));
            if (nvDev && !gpus.Any(g => g.Vendor == "nvidia"))
            {
                gpus.Add(("nvidia", System.IO.File.Exists("/dev/nvidia0") ? "/dev/nvidia0" : "/dev/nvidia"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU detection failed");
        }
        return gpus;
    }

    private async Task<List<string>> GetFfmpegHwAccelsAsync()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -hwaccels",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return result;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            bool started = false;
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Hardware acceleration methods", StringComparison.OrdinalIgnoreCase)) { started = true; continue; }
                if (started && line.Length > 0) result.Add(line.ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg -hwaccels failed");
        }
        return result;
    }
}
