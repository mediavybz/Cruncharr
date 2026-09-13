using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cruncharr.Core.Services;

public static class EncodingCommand
{
    public static int MaxQuality(string? codec) => codec is "libaom-av1" or "libsvtav1" or "librav1e" or "av1_nvenc" or "av1_qsv" or "av1_amf" ? 63 : 51;

    public static void Validate(VideoPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.PresetName)) throw new ArgumentException("Preset name is required.");
        if (!string.IsNullOrEmpty(preset.Codec) && !Regex.IsMatch(preset.Codec, "^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Invalid codec name.");
        if (preset.Crf < -1 || preset.Crf > MaxQuality(preset.Codec))
            throw new ArgumentException($"Quality must be -1 (encoder default) or 0–{MaxQuality(preset.Codec)} for this codec.");
        if (!string.IsNullOrEmpty(preset.Resolution) && !Regex.IsMatch(preset.Resolution, "^(?:-2|[1-9][0-9]*):(?:-2|[1-9][0-9]*)$"))
            throw new ArgumentException("Resolution must be width:height, with -2 allowed for automatic aspect ratio.");
        if (preset.Resolution == "-2:-2") throw new ArgumentException("Specify at least one resolution dimension.");
        if (!string.IsNullOrEmpty(preset.FrameRate) && !Regex.IsMatch(preset.FrameRate, "^[1-9][0-9]*(?:[.][0-9]+|/[1-9][0-9]*)?$"))
            throw new ArgumentException("Frame rate must be a positive number or fraction (for example 24000/1001).");
        if (preset.AdditionalParameters == null || preset.AdditionalParameters.Any(p => p == null))
            throw new ArgumentException("Additional parameters must be a list of strings.");
        var extra = preset.AdditionalParameters.SelectMany(SplitArguments).ToList();
        if (string.IsNullOrEmpty(preset.Codec) && !extra.Any(a => a is "-c:v" or "-codec:v" or "-vcodec"))
            throw new ArgumentException("Specify a video codec or provide -c:v in Additional Parameters.");
    }

    public static IEnumerable<string> QualityArguments(VideoPreset preset)
    {
        if (preset.Crf == -1 || preset.Codec is "copy" or null or "") return [];
        var q = preset.Crf.ToString(CultureInfo.InvariantCulture);
        return preset.Codec switch
        {
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => ["-cq", q, "-b:v", "0"],
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => ["-global_quality", q],
            "h264_amf" => ["-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q],
            "hevc_amf" or "av1_amf" => ["-rc", "cqp", "-qp_i", q, "-qp_p", q],
            "h264_vaapi" or "hevc_vaapi" or "av1_vaapi" => ["-qp", q],
            "libaom-av1" => ["-crf", q, "-b:v", "0"],
            _ => ["-crf", q]
        };
    }

    public static List<string> Build(VideoPreset preset, string inputPath, string outputPath)
    {
        Validate(preset);
        // Preserve every audio/subtitle/attachment stream by default. FFmpeg's automatic
        // codec selection otherwise transcodes audio and can fail on ASS subtitles/fonts.
        var args = new List<string> { "-nostdin", "-hide_banner", "-y", "-i", inputPath, "-map", "0", "-c", "copy" };
        if (!string.IsNullOrEmpty(preset.Codec)) args.AddRange(["-c:v", preset.Codec]);
        args.AddRange(QualityArguments(preset));
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(preset.Resolution)) filters.Add($"scale={preset.Resolution}");
        if (!string.IsNullOrWhiteSpace(preset.FrameRate)) filters.Add($"fps={preset.FrameRate}");
        if (filters.Count > 0) args.AddRange(["-vf", string.Join(",", filters)]);
        // Historical presets already contain -map 0. Avoid mapping every stream twice.
        var extra = preset.AdditionalParameters.SelectMany(SplitArguments).ToList();
        if (extra.Any(a => a == "-map")) args.RemoveRange(args.IndexOf("-map"), 2);
        args.AddRange(extra);
        args.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    public static IEnumerable<string> SplitArguments(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in commandLine)
        {
            if (c == quote) { quote = null; continue; }
            if (quote == null && c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c) && quote == null)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (quote != null) throw new ArgumentException("Unclosed quote in Additional Parameters.");
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }
}
