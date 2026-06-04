using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Cruncharr.Core.Utils.DRM;

public class ContentKey
{
    [JsonPropertyName("key_id")] public byte[] KeyID { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("type")] public string Type { get; set; } = "";

    [JsonPropertyName("bytes")] public byte[] Bytes { get; set; } = Array.Empty<byte>();

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    public override string ToString()
    {
        return $"{BitConverter.ToString(KeyID).Replace("-", "").ToLower()}:{BitConverter.ToString(Bytes).Replace("-", "").ToLower()}";
    }
}
