using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

public interface IEncodingService
{
    List<VideoPreset> GetPresets();
    List<VideoPreset> GetCustomPresets();
    VideoPreset? GetPreset(string presetName);
    bool IsBuiltIn(string presetName);
    bool AddPreset(VideoPreset preset);
    bool RemovePreset(string presetName);
}

public class EncodingService : IEncodingService
{
    // Built-in presets (mirrors upstream FfmpegEncoding.presets - the first 15).
    private static readonly List<VideoPreset> _builtIn = new(){
        new(){ PresetName = "AV1 1080p24", Codec = "libaom-av1", Resolution = "1920:1080", FrameRate = "24000/1001", Crf = 30, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "AV1 720p24", Codec = "libaom-av1", Resolution = "1280:720", FrameRate = "24000/1001", Crf = 30, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "AV1 480p24", Codec = "libaom-av1", Resolution = "854:480", FrameRate = "24000/1001", Crf = 30, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "AV1 360p24", Codec = "libaom-av1", Resolution = "640:360", FrameRate = "24000/1001", Crf = 30, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "AV1 240p24", Codec = "libaom-av1", Resolution = "426:240", FrameRate = "24000/1001", Crf = 30, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.265 1080p24", Codec = "libx265", Resolution = "1920:1080", FrameRate = "24000/1001", Crf = 28, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.265 720p24", Codec = "libx265", Resolution = "1280:720", FrameRate = "24000/1001", Crf = 28, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.265 480p24", Codec = "libx265", Resolution = "854:480", FrameRate = "24000/1001", Crf = 28, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.265 360p24", Codec = "libx265", Resolution = "640:360", FrameRate = "24000/1001", Crf = 28, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.265 240p24", Codec = "libx265", Resolution = "426:240", FrameRate = "24000/1001", Crf = 28, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.264 1080p24", Codec = "libx264", Resolution = "1920:1080", FrameRate = "24000/1001", Crf = 23, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.264 720p24", Codec = "libx264", Resolution = "1280:720", FrameRate = "24000/1001", Crf = 23, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.264 480p24", Codec = "libx264", Resolution = "854:480", FrameRate = "24000/1001", Crf = 23, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.264 360p24", Codec = "libx264", Resolution = "640:360", FrameRate = "24000/1001", Crf = 23, AdditionalParameters ={ "-map 0" } },
        new(){ PresetName = "H.264 240p24", Codec = "libx264", Resolution = "426:240", FrameRate = "24000/1001", Crf = 23, AdditionalParameters ={ "-map 0" } },
    };

    // User-created presets, persisted as JSON files (mirrors upstream's
    // PathENCODING_PRESETS_DIR; loaded at startup, written on add).
    private readonly List<VideoPreset> _custom = new();
    private readonly object _lock = new();
    private readonly string _presetsDir;
    private readonly ILogger<EncodingService>? _logger;

    public EncodingService(ILogger<EncodingService>? logger = null)
    {
        _logger = logger;
        var cfgPath = Environment.GetEnvironmentVariable("CRUNCHYROLL_CONFIG_PATH") ?? "/config/cruncharr.yaml";
        _presetsDir = Path.Combine(Path.GetDirectoryName(cfgPath) ?? ".", "encoding-presets");
        LoadCustomPresets();
    }

    private void LoadCustomPresets()
    {
        try
        {
            if (!Directory.Exists(_presetsDir)) return;
            foreach (var file in Directory.GetFiles(_presetsDir, "*.json"))
            {
                try
                {
                    var p = JsonConvert.DeserializeObject<VideoPreset>(File.ReadAllText(file));
                    if (p != null && !string.IsNullOrWhiteSpace(p.PresetName)
                        && !_builtIn.Any(b => b.PresetName == p.PresetName)
                        && !_custom.Any(c => c.PresetName == p.PresetName))
                    {
                        _custom.Add(p);
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Skipping invalid preset file {File}", file); }
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to load custom encoding presets"); }
    }

    public List<VideoPreset> GetPresets() { lock (_lock) return _builtIn.Concat(_custom).ToList(); }

    public List<VideoPreset> GetCustomPresets() { lock (_lock) return _custom.ToList(); }

    public bool IsBuiltIn(string presetName) => _builtIn.Any(p => p.PresetName == presetName);

    public VideoPreset? GetPreset(string presetName)
    {
        lock (_lock)
        {
            return _builtIn.Concat(_custom).FirstOrDefault(x => x.PresetName == presetName);
        }
    }

    public bool AddPreset(VideoPreset preset)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.PresetName)) return false;
        if (IsBuiltIn(preset.PresetName!))
        {
            _logger?.LogWarning("Cannot overwrite built-in preset {Name}", preset.PresetName);
            return false;
        }
        lock (_lock)
        {
            _custom.RemoveAll(c => c.PresetName == preset.PresetName); // upsert
            _custom.Add(preset);
        }
        try
        {
            Directory.CreateDirectory(_presetsDir);
            File.WriteAllText(Path.Combine(_presetsDir, SanitizeFileName(preset.PresetName!) + ".json"),
                JsonConvert.SerializeObject(preset, Formatting.Indented));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist preset {Name}", preset.PresetName);
            return false;
        }
    }

    public bool RemovePreset(string presetName)
    {
        if (IsBuiltIn(presetName)) return false; // built-ins are not removable
        bool removed;
        lock (_lock) { removed = _custom.RemoveAll(c => c.PresetName == presetName) > 0; }
        try
        {
            var f = Path.Combine(_presetsDir, SanitizeFileName(presetName) + ".json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete preset file {Name}", presetName); }
        return removed;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

public class VideoPreset
{
    public string? PresetName { get; set; }
    public string? Codec { get; set; }
    public string? Resolution { get; set; }
    public string? FrameRate { get; set; }
    public int Crf { get; set; }
    public List<string> AdditionalParameters { get; set; } = new List<string>();
}
