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
    // Built-in presets. The first 15 mirror upstream FfmpegEncoding.presets; the
    // "[...] Anime" presets are Cruncharr additions (user-requested): unofficial
    // recreations of the settings anime mini-encode groups publish for small files at
    // high perceived quality. Video is re-encoded 10-bit; audio/subs/fonts are stream-
    // copied so nothing else is degraded.
    private static readonly List<VideoPreset> _builtIn = new(){
        // User-provided SVT-AV1 Main10 recipe. Keeps SOURCE resolution and fps (empty
        // Resolution/FrameRate => no scale/fps filter), stream-copies audio/subs/fonts, and
        // stamps CrunchArr metadata. -progress/-nostats are added by the encoder, not here.
        // User-tuned 2026-07-10: SVT preset 8 -> 6 (slower encode, better compression
        // efficiency: higher quality AND smaller files at the same CRF 24). Config stores the
        // preset by NAME, so the old "(SVT preset 8)" name is aliased below — do not remove it.
        new(){ PresetName = "[CrunchArr] AV1 Main10 Source (SVT preset 6)", Codec = "libsvtav1", Resolution = "", FrameRate = "", Crf = 24,
               AdditionalParameters ={ "-map 0", "-pix_fmt yuv420p10le", "-preset 6",
                   "-svtav1-params tune=0:lookahead=120:aq-mode=2:keyint=240:scd=1:enable-overlays=1",
                   "-c:a copy", "-c:s copy", "-c:t copy",
                   "-metadata encoder=CrunchArr", "-metadata encoded_by=CrunchArr",
                   "-metadata encoding_tool=\"FFmpeg Nightly + SVT-AV1\"",
                   "-metadata comment=\"CrunchArr AV1 Main10 Source\"" } },
        // EMBER-style: x265 10-bit, slower preset, anime-tuned psy/aq settings.
        new(){ PresetName = "[EMBER] Anime HEVC 10-bit (unofficial)", Codec = "libx265", Resolution = "-2:1080", FrameRate = "24000/1001", Crf = 21,
               AdditionalParameters ={ "-map 0", "-pix_fmt yuv420p10le", "-preset slow",
                   "-x265-params limit-sao=1:bframes=8:psy-rd=1.5:psy-rdoq=2.0:aq-mode=3:deblock=-1,-1",
                   "-c:a copy", "-c:s copy", "-c:t copy" } },
        // neoHEVC-style: x265 10-bit, veryslow, animation-tuned psy/aq. Unofficial Cruncharr addition.
        new(){ PresetName = "[neoHEVC] Anime HEVC 10-bit (unofficial)", Codec = "libx265", Resolution = "-2:1080", FrameRate = "24000/1001", Crf = 19,
               AdditionalParameters ={ "-map 0", "-pix_fmt yuv420p10le", "-preset veryslow",
                   "-x265-params me=star:subme=7:psy-rd=2.0:psy-rdoq=1.0:aq-mode=3:aq-strength=0.8:deblock=-1,-1:bframes=8:ref=6",
                   "-c:a copy", "-c:s copy", "-c:t copy" } },
        // Trix-style: SVT-AV1 10-bit. Updated 2026-07-10 to the published Trix SvtAv1EncApp
        // recipe (--preset 2 --crf 25 --photon-noise 400 --min-keyint 65 --keyint 193 --scm 0
        // --enable-tf 2 --luminance-qp-bias 33 --fast-decode 1 --enable-dlf 3 --enable-alt-cdef 2),
        // adapted to the MAINLINE SVT-AV1 bundled in our BtbN ffmpeg (verified v4.1.0 in-image):
        // photon-noise / min-keyint / enable-alt-cdef are SVT-AV1-PSY-fork options that ABORT
        // encoder init here ("Error parsing option"), and enable-dlf tops out at 2. photon-noise
        // approximated with film-grain=8 synthesis (denoise off); PSY-only keys dropped; dlf
        // clamped. Source-preserving like the recipe itself (no scale/fps filter).
        new(){ PresetName = "[Trix] Anime AV1 10-bit (unofficial)", Codec = "libsvtav1", Resolution = "", FrameRate = "", Crf = 25,
               AdditionalParameters ={ "-map 0", "-pix_fmt yuv420p10le", "-preset 2",
                   "-svtav1-params film-grain=8:film-grain-denoise=0:keyint=193:scm=0:enable-tf=2:luminance-qp-bias=33:fast-decode=1:enable-dlf=2",
                   "-c:a copy", "-c:s copy", "-c:t copy" } },
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

    // Renamed built-in presets: configs (encoding_preset in cruncharr.yaml) reference presets by
    // NAME, so every historical name must keep resolving or existing setups silently stop encoding.
    private static readonly Dictionary<string, string> _renamedBuiltIns = new()
    {
        ["[CrunchArr] AV1 Main10 Source (SVT preset 8)"] = "[CrunchArr] AV1 Main10 Source (SVT preset 6)",
    };

    private static string NormalizePresetName(string presetName) =>
        _renamedBuiltIns.TryGetValue(presetName, out var current) ? current : presetName;

    public bool IsBuiltIn(string presetName)
    {
        var name = NormalizePresetName(presetName);
        return _builtIn.Any(p => p.PresetName == name);
    }

    public VideoPreset? GetPreset(string presetName)
    {
        var name = NormalizePresetName(presetName);
        lock (_lock)
        {
            return _builtIn.Concat(_custom).FirstOrDefault(x => x.PresetName == name);
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
        var presetPath = Path.Combine(_presetsDir, SanitizeFileName(preset.PresetName!) + ".json");
        var tmp = presetPath + ".tmp";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_presetsDir);
                // Atomic write (temp + rename) so a crash mid-save can't corrupt a custom preset.
                File.WriteAllText(tmp, JsonConvert.SerializeObject(preset, Formatting.Indented));
                File.Move(tmp, presetPath, overwrite: true);
                _custom.RemoveAll(c => c.PresetName == preset.PresetName); // upsert
                _custom.Add(preset);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
                _logger?.LogError(ex, "Failed to persist preset {Name}", preset.PresetName);
                return false;
            }
        }
    }

    public bool RemovePreset(string presetName)
    {
        if (IsBuiltIn(presetName)) return false; // built-ins are not removable
        lock (_lock)
        {
            if (!_custom.Any(c => c.PresetName == presetName)) return false;
            try
            {
                var f = Path.Combine(_presetsDir, SanitizeFileName(presetName) + ".json");
                if (File.Exists(f)) File.Delete(f);
                _custom.RemoveAll(c => c.PresetName == presetName);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete preset file {Name}", presetName);
                return false;
            }
        }
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
