using System.Collections;

namespace Cruncharr.Core.Utils;

// The single byte-range type shared by the manifest parsers and the segment downloader.
public class ByteRange
{
    public long Offset { get; set; }
    public long Length { get; set; }

    // Parsers hand ranges over as this class, as anonymous { offset, length } objects, or as
    // expando/dictionary members. Assigning those to a ByteRange through `dynamic` threw a
    // RuntimeBinderException, so every boundary converts explicitly instead.
    public static ByteRange? From(object? value)
    {
        if (value is null or ByteRange) return (ByteRange?)value;
        var offset = Member(value, nameof(Offset));
        var length = Member(value, nameof(Length));
        if (offset == null || length == null)
        {
            throw new InvalidDataException($"Unsupported byte range value of type {value.GetType().Name}.");
        }
        return new ByteRange { Offset = Convert.ToInt64(offset), Length = Convert.ToInt64(length) };
    }

    private static object? Member(object value, string name)
    {
        if (value is IDictionary<string, object?> members)
        {
            return members.FirstOrDefault(member => string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        }
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key as string, name, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            }
            return null;
        }
        var type = value.GetType();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;
        return type.GetProperty(name, flags)?.GetValue(value) ?? type.GetField(name, flags)?.GetValue(value);
    }
}
