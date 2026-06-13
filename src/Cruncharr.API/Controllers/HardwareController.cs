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
            // Devices passed into the container (GPU passthrough)
            var renderNodes = Directory.Exists("/dev/dri")
                ? Directory.GetFiles("/dev/dri").Where(f => Path.GetFileName(f).StartsWith("renderD", StringComparison.Ordinal)).OrderBy(f => f).ToList()
                : new List<string>();
            var firstRender = renderNodes.FirstOrDefault();
            bool hasNvidia = System.IO.File.Exists("/dev/nvidia0")
                || (Directory.Exists("/dev") && Directory.GetFiles("/dev").Any(f => Path.GetFileName(f).StartsWith("nvidia", StringComparison.Ordinal)));

            var methods = await GetFfmpegHwAccelsAsync();

            // Map each ffmpeg-supported method to a friendly label + device availability
            foreach (var m in methods)
            {
                switch (m)
                {
                    case "vaapi":
                        options.Add(new HwAccelOption { Value = "vaapi", Label = LabelFor("VAAPI — Intel/AMD", firstRender), DeviceFound = firstRender != null, Device = firstRender });
                        break;
                    case "qsv":
                        options.Add(new HwAccelOption { Value = "qsv", Label = LabelFor("Intel QuickSync (QSV)", firstRender), DeviceFound = firstRender != null, Device = firstRender });
                        break;
                    case "vdpau":
                        options.Add(new HwAccelOption { Value = "vdpau", Label = LabelFor("VDPAU", firstRender), DeviceFound = firstRender != null, Device = firstRender });
                        break;
                    case "drm":
                        options.Add(new HwAccelOption { Value = "drm", Label = LabelFor("DRM", firstRender), DeviceFound = firstRender != null, Device = firstRender });
                        break;
                    case "vulkan":
                        options.Add(new HwAccelOption { Value = "vulkan", Label = LabelFor("Vulkan", firstRender), DeviceFound = firstRender != null, Device = firstRender });
                        break;
                    case "cuda":
                    case "nvdec":
                        options.Add(new HwAccelOption { Value = m, Label = m == "cuda" ? "NVIDIA CUDA" + (hasNvidia ? "" : " (no NVIDIA device detected)") : "NVIDIA NVDEC" + (hasNvidia ? "" : " (no NVIDIA device detected)"), DeviceFound = hasNvidia });
                        break;
                    default:
                        options.Add(new HwAccelOption { Value = m, Label = m.ToUpperInvariant(), DeviceFound = false });
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate hardware accelerators");
        }

        return Ok(options);
    }

    private static string LabelFor(string name, string? device) =>
        device != null ? $"{name} ({device})" : $"{name} (no /dev/dri device passed in)";

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
