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
            var vaapiName = intel.Device != null ? intel.Name : amd.Device != null ? amd.Name : unknownRender.Name;

            var methods = (await GetFfmpegHwAccelsAsync()).ToHashSet();

            // Label: exact card name when we could resolve it ("AMD Raphael — VAAPI
            // (/dev/dri/renderD128)"), otherwise the generic vendor label as before.
            void Add(string value, string methodLabel, string genericLabel, string? gpuName, string? device)
            {
                var display = !string.IsNullOrEmpty(gpuName) ? $"{gpuName} — {methodLabel}" : genericLabel;
                options.Add(new HwAccelOption { Value = value, Label = device != null ? $"{display} ({device})" : display, DeviceFound = true, Device = device });
            }

            // Per-vendor, only when ffmpeg supports the method AND the matching GPU exists.
            if (methods.Contains("cuda") && hasNvidia) Add("cuda", "CUDA", "NVIDIA (CUDA)", nvidia.Name, nvidia.Device);
            else if (methods.Contains("nvdec") && hasNvidia) Add("nvdec", "NVDEC", "NVIDIA (NVDEC)", nvidia.Name, nvidia.Device);
            if (methods.Contains("qsv") && hasIntel) Add("qsv", "QuickSync (QSV)", "Intel QuickSync (QSV)", intel.Name, intel.Device);
            if (methods.Contains("amf") && hasAmd) Add("amf", "AMF", "AMD (AMF)", amd.Name, amd.Device);
            if (methods.Contains("vaapi") && anyVaapi)
            {
                var vendorName = hasIntel ? "Intel" : hasAmd ? "AMD" : "Intel/AMD";
                Add("vaapi", "VAAPI", $"{vendorName} (VAAPI)", vaapiName, vaapiDevice);
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
    // device. Vendor IDs: Intel 0x8086, AMD 0x1002, NVIDIA 0x10de. Name is the exact
    // marketing/board name when resolvable (pci.ids lookup, or nvidia-smi for NVIDIA).
    private List<(string Vendor, string Device, string? Name)> DetectGpus()
    {
        var gpus = new List<(string Vendor, string Device, string? Name)>();
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
                    string? vendorId = null, deviceId = null;
                    try
                    {
                        var vendorFile = $"/sys/class/drm/{name}/device/vendor";
                        if (System.IO.File.Exists(vendorFile))
                        {
                            vendorId = System.IO.File.ReadAllText(vendorFile).Trim().ToLowerInvariant();
                            vendor = vendorId switch
                            {
                                "0x8086" => "intel",
                                "0x1002" => "amd",
                                "0x10de" => "nvidia",
                                _ => "unknown"
                            };
                        }
                        var deviceFile = $"/sys/class/drm/{name}/device/device";
                        if (System.IO.File.Exists(deviceFile))
                        {
                            deviceId = System.IO.File.ReadAllText(deviceFile).Trim().ToLowerInvariant();
                        }
                    }
                    catch { /* vendor unreadable - leave as unknown */ }
                    gpus.Add((vendor, node, LookupPciDeviceName(vendorId, deviceId)));
                }
            }

            // NVIDIA's proprietary driver exposes /dev/nvidia0 (etc.) rather than a DRM node.
            bool nvDev = System.IO.File.Exists("/dev/nvidia0")
                || (Directory.Exists("/dev") && Directory.GetFiles("/dev")
                        .Select(Path.GetFileName)
                        .Any(n => n != null && n.StartsWith("nvidia", StringComparison.Ordinal) && n.Length > 6 && char.IsDigit(n[^1])));
            if (nvDev && !gpus.Any(g => g.Vendor == "nvidia"))
            {
                gpus.Add(("nvidia", System.IO.File.Exists("/dev/nvidia0") ? "/dev/nvidia0" : "/dev/nvidia", GetNvidiaSmiName()));
            }
            else if (gpus.Any(g => g.Vendor == "nvidia" && g.Name == null))
            {
                // DRM node identified as NVIDIA but pci.ids couldn't name it — nvidia-smi
                // (injected by the NVIDIA Container Toolkit) knows the exact model.
                var smiName = GetNvidiaSmiName();
                if (smiName != null)
                {
                    for (int i = 0; i < gpus.Count; i++)
                        if (gpus[i].Vendor == "nvidia" && gpus[i].Name == null)
                            gpus[i] = (gpus[i].Vendor, gpus[i].Device, smiName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU detection failed");
        }
        return gpus;
    }

    // Exact NVIDIA model from nvidia-smi (present when the NVIDIA Container Toolkit
    // injects the host driver). Best source for names like "NVIDIA GeForce RTX 3070".
    private string? GetNvidiaSmiName()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var line = p.StandardOutput.ReadLine()?.Trim();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch { return null; } // nvidia-smi absent — non-NVIDIA box or no toolkit
    }

    // Resolve "0x1002"/"0x164e" to the exact device name via the pci.ids database
    // (shipped in the image as the Debian pci.ids package). Vendor lines have no
    // indent ("1002  Advanced Micro Devices..."), device lines one tab ("\t164e  Raphael").
    private static readonly Dictionary<string, string?> _pciNameCache = new();
    private static readonly object _pciCacheLock = new();

    private static string? LookupPciDeviceName(string? vendorHex, string? deviceHex)
    {
        if (string.IsNullOrEmpty(vendorHex) || string.IsNullOrEmpty(deviceHex)) return null;
        var vendorId = vendorHex.Replace("0x", "");
        var deviceId = deviceHex.Replace("0x", "");
        var cacheKey = vendorId + ":" + deviceId;
        lock (_pciCacheLock)
        {
            if (_pciNameCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        string? result = null;
        try
        {
            var idsPath = new[] { "/usr/share/misc/pci.ids", "/usr/share/hwdata/pci.ids" }
                .FirstOrDefault(System.IO.File.Exists);
            if (idsPath != null)
            {
                string? vendorName = null;
                bool inVendor = false;
                foreach (var line in System.IO.File.ReadLines(idsPath))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] != '\t')
                    {
                        // New vendor block. Stop once we've left the block we wanted.
                        if (inVendor) break;
                        if (line.StartsWith(vendorId, StringComparison.OrdinalIgnoreCase))
                        {
                            inVendor = true;
                            vendorName = line.Substring(vendorId.Length).Trim();
                        }
                    }
                    else if (inVendor && line.Length > 1 && line[1] != '\t')
                    {
                        var entry = line.TrimStart('\t');
                        if (entry.StartsWith(deviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            var deviceName = entry.Substring(deviceId.Length).Trim();
                            // Short vendor prefix: "AMD Raphael", not the full legal name.
                            var shortVendor = vendorId switch
                            {
                                "8086" => "Intel",
                                "1002" => "AMD",
                                "10de" => "NVIDIA",
                                _ => vendorName
                            };
                            result = string.IsNullOrEmpty(shortVendor) ? deviceName : $"{shortVendor} {deviceName}";
                            break;
                        }
                    }
                }
            }
        }
        catch { /* no pci.ids or unreadable — fall back to generic labels */ }

        lock (_pciCacheLock) { _pciNameCache[cacheKey] = result; }
        return result;
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
