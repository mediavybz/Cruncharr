using System.Dynamic;
using System.Text.RegularExpressions;
using Cruncharr.Core.Utils.HLS;

namespace Cruncharr.Core.Utils.Parser;

public static class M3u8MediaPlaylistParser
{
    public static M3U8Json Parse(string content, string baseUrl)
    {
        var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<dynamic>();
        Key? currentKey = null;
        ByteRange? currentByteRange = null;
        dynamic? currentMap = null;
        int mediaSequence = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#EXTM3U")) continue;

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                var match = Regex.Match(line, @"#EXT-X-MEDIA-SEQUENCE:(\d+)");
                if (match.Success)
                {
                    mediaSequence = int.Parse(match.Groups[1].Value);
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:"))
            {
                currentKey = ParseKey(line, baseUrl);
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                currentByteRange = ParseByteRange(line);
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:"))
            {
                currentMap = ParseMap(line, baseUrl);
                continue;
            }

            if (line.StartsWith("#EXTINF:"))
            {
                // Next line should be the segment URI
                if (i + 1 < lines.Length)
                {
                    var uri = lines[i + 1].Trim();
                    if (!string.IsNullOrEmpty(uri) && !uri.StartsWith("#"))
                    {
                        var segment = new ExpandoObject() as IDictionary<string, object>;
                        segment["uri"] = ResolveUri(uri, baseUrl);
                        if (currentKey != null) segment["key"] = currentKey;
                        if (currentByteRange != null) segment["byteRange"] = currentByteRange;
                        if (currentMap != null) segment["map"] = currentMap;
                        segments.Add(segment);
                        i++; // Skip the URI line
                    }
                }
                continue;
            }

            // Handle segment URI without EXTINF (though this is non-standard)
            if (!line.StartsWith("#"))
            {
                var segment = new ExpandoObject() as IDictionary<string, object>;
                segment["uri"] = ResolveUri(line, baseUrl);
                if (currentKey != null) segment["key"] = currentKey;
                if (currentByteRange != null) segment["byteRange"] = currentByteRange;
                if (currentMap != null) segment["map"] = currentMap;
                segments.Add(segment);
            }
        }

        return new M3U8Json
        {
            Segments = segments,
            MediaSequence = mediaSequence
        };
    }

    private static Key ParseKey(string line, string baseUrl)
    {
        var key = new Key();
        var uriMatch = Regex.Match(line, @"URI=""([^""]+)""");
        if (uriMatch.Success)
        {
            key.Uri = ResolveUri(uriMatch.Groups[1].Value, baseUrl);
        }

        var ivMatch = Regex.Match(line, @"IV=0x([0-9A-Fa-f]+)");
        if (ivMatch.Success)
        {
            var ivHex = ivMatch.Groups[1].Value;
            key.Iv = new List<int>();
            for (int i = 0; i < ivHex.Length; i += 8)
            {
                var chunk = ivHex.Substring(i, Math.Min(8, ivHex.Length - i));
                key.Iv.Add(Convert.ToInt32(chunk, 16));
            }
        }
        else
        {
            // Default IV: sequence number
            key.Iv = new List<int> { 0, 0, 0, 0 };
        }

        return key;
    }

    private static ByteRange ParseByteRange(string line)
    {
        var match = Regex.Match(line, @"#EXT-X-BYTERANGE:(\d+)(?:@(\d+))?");
        if (match.Success)
        {
            return new ByteRange
            {
                Length = long.Parse(match.Groups[1].Value),
                Offset = match.Groups[2].Success ? long.Parse(match.Groups[2].Value) : 0
            };
        }
        return new ByteRange();
    }

    private static dynamic ParseMap(string line, string baseUrl)
    {
        var map = new ExpandoObject() as IDictionary<string, object>;
        var uriMatch = Regex.Match(line, @"URI=""([^""]+)""");
        if (uriMatch.Success)
        {
            map["uri"] = ResolveUri(uriMatch.Groups[1].Value, baseUrl);
        }
        var rangeMatch = Regex.Match(line, @"BYTERANGE=""(\d+)@(\d+)""");
        if (rangeMatch.Success)
        {
            map["byteRange"] = new ByteRange
            {
                Length = long.Parse(rangeMatch.Groups[1].Value),
                Offset = long.Parse(rangeMatch.Groups[2].Value)
            };
        }
        return map;
    }

    private static string ResolveUri(string uri, string baseUrl)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }
        if (string.IsNullOrEmpty(baseUrl)) return uri;

        var baseUri = new Uri(baseUrl);
        return new Uri(baseUri, uri).ToString();
    }
}
