using System;
using System.Reflection;
using Newtonsoft.Json;

namespace Cruncharr.Core.Utils;

public class LocaleConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(Locale);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.Value == null)
        {
            return Locale.Unknown;
        }

        string? value = reader.Value.ToString();
        if (string.IsNullOrEmpty(value))
        {
            return Locale.DefaulT;
        }

        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            FieldInfo fi = typeof(Locale).GetField(locale.ToString())!;
            var attr = fi.GetCustomAttribute<System.Runtime.Serialization.EnumMemberAttribute>();
            if (attr != null && attr.Value == value)
            {
                return locale;
            }
        }

        return Locale.Unknown;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is Locale locale)
        {
            FieldInfo fi = typeof(Locale).GetField(locale.ToString())!;
            var attr = fi.GetCustomAttribute<System.Runtime.Serialization.EnumMemberAttribute>();
            writer.WriteValue(attr?.Value ?? locale.ToString());
        }
        else
        {
            writer.WriteNull();
        }
    }
}
