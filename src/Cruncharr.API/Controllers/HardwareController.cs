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

            // Only surface methods whose underlying device is actually present in the
            // container. Methods ffmpeg supports but with no matching device passed in are
            // omitted entirely, so the dropdown lists only GPUs available right now.
            foreach (var m in methods)
            {
                bool found;
                string label;
                string? device = null;
                switch (m)
                {
                    case "vaapi": found = firstRender != null; device = firstRender; label = "Intel / AMD (VAAPI)"; break;
                    case "qsv":   found = firstRender != null; device = firstRender; label = "Intel QuickSync (QSV)"; break;
                    case "vdpau": found = firstRender != null; device = firstRender; label = "VDPAU"; break;
                    case "drm":   found = firstRender != null; device = firstRender; label = "DRM"; break;
                    case "amf":   found = firstRender != null; device = firstRender; label = "AMD (AMF)"; break;
                    case "vulkan": found = firstRender != null || hasNvidia; label = "Vulkan"; break;
                    case "opencl": found = firstRender != null || hasNvidia; label = "OpenCL"; break;
                    case "cuda":  found = hasNvidia; label = "NVIDIA (CUDA)"; break;
                    case "nvdec": found = hasNvidia; label = "NVIDIA (NVDEC)"; break;
                    default:      found = false; label = m.ToUpperInvariant(); break;
                }
                if (found)
                {
                    options.Add(new HwAccelOption
                    {
                        Value = m,
                        Label = device != null ? $"{label} ({device})" : label,
                        DeviceFound = true,
                        Device = device
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate hardware accelerators");
        }

        return Ok(options);
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
