using System.Globalization;
using System.Reflection;
using Cruncharr.Core.Configuration;
using YamlDotNet.Serialization;

namespace Cruncharr.CLI;

/// <summary>
/// Reads and writes single settings for `config get/set`. A key is a dotted path of property
/// names or their config-file names, matched case-insensitively: <c>LogMode</c>,
/// <c>Download.OutputDirectory</c> and <c>download.output_dir</c> all work. Values are converted
/// to the setting's type; unknown keys and invalid values are errors, never silently ignored.
/// </summary>
public static class ConfigEditor
{
    // `config get`: prints the effective value (environment overrides included). Returns the exit code.
    public static int Get(string configPath, string key, TextWriter output, TextWriter errors)
    {
        try
        {
            var config = CruncharrConfig.Load(configPath);
            config.ApplyEnvironmentVariables();
            if (!TryGet(config, key, out var value, out var error))
            {
                errors.WriteLine(error);
                return 1;
            }
            output.WriteLine(value);
            return 0;
        }
        catch (ConfigLoadException ex)
        {
            errors.WriteLine(ex.Message);
            return 1;
        }
    }

    // `config set`: edits the file as stored (environment overrides are not written into it).
    public static int Set(string configPath, string key, string value, TextWriter output, TextWriter errors)
    {
        try
        {
            var config = CruncharrConfig.Load(configPath);
            if (!TrySet(config, key, value, out var error))
            {
                errors.WriteLine(error);
                return 1;
            }
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            config.Save(configPath);
            TryGet(config, key, out var saved, out _);
            output.WriteLine($"Set {key} = {saved}");
            return 0;
        }
        catch (Exception ex) when (ex is ConfigLoadException or InvalidOperationException)
        {
            errors.WriteLine(ex.Message);
            return 1;
        }
    }

    public static bool TryGet(CruncharrConfig config, string key, out string value, out string error)
    {
        value = "";
        if (!TryResolve(config, key, out var owner, out var property, out error)) return false;
        var current = property.GetValue(owner);
        value = current switch
        {
            null => "",
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable<string> list => string.Join(",", list),
            bool flag => flag ? "true" : "false",
            _ when IsSettable(property.PropertyType) => current.ToString() ?? "",
            _ => new SerializerBuilder().Build().Serialize(current).TrimEnd()
        };
        return true;
    }

    public static bool TrySet(CruncharrConfig config, string key, string value, out string error)
    {
        if (!TryResolve(config, key, out var owner, out var property, out error)) return false;
        if (!property.CanWrite || !IsSettable(property.PropertyType))
        {
            error = $"'{key}' is a group of settings; set one of its keys instead (for example {key}.<name>).";
            return false;
        }
        if (!TryConvert(value, property.PropertyType, out var converted))
        {
            error = $"'{value}' is not a valid {Describe(property.PropertyType)} for '{key}'.";
            return false;
        }
        property.SetValue(owner, converted);
        return true;
    }

    private static bool TryResolve(object root, string key, out object owner, out PropertyInfo property, out string error)
    {
        owner = root;
        property = null!;
        error = "";
        var segments = key.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(string.IsNullOrEmpty))
        {
            error = $"'{key}' is not a valid setting name.";
            return false;
        }
        for (var index = 0; index < segments.Length; index++)
        {
            var match = Find(owner.GetType(), segments[index]);
            if (match == null)
            {
                error = $"Unknown setting '{key}'.";
                return false;
            }
            property = match;
            if (index == segments.Length - 1) return true;
            if (IsSettable(match.PropertyType) || match.GetValue(owner) is not { } child)
            {
                error = $"Unknown setting '{key}'.";
                return false;
            }
            owner = child;
        }
        return true;
    }

    private static PropertyInfo? Find(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<YamlIgnoreAttribute>() == null)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(p.GetCustomAttribute<YamlMemberAttribute>()?.Alias, name, StringComparison.OrdinalIgnoreCase));

    // Plain values a single command-line argument can hold.
    private static bool IsSettable(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type.IsEnum || type == typeof(DateTime) ||
               type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal) ||
               type == typeof(List<string>);
    }

    private static bool TryConvert(string value, Type type, out object? converted)
    {
        converted = null;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null && (value.Length == 0 || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
            return true;
        var target = underlying ?? type;
        var invariant = CultureInfo.InvariantCulture;

        if (target == typeof(string)) { converted = value; return true; }
        if (target == typeof(List<string>))
        {
            converted = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            return true;
        }
        if (target == typeof(bool) && bool.TryParse(value, out var flag)) { converted = flag; return true; }
        if (target == typeof(int) && int.TryParse(value, NumberStyles.Integer, invariant, out var i)) { converted = i; return true; }
        if (target == typeof(long) && long.TryParse(value, NumberStyles.Integer, invariant, out var l)) { converted = l; return true; }
        if (target == typeof(double) && double.TryParse(value, NumberStyles.Float, invariant, out var d) && double.IsFinite(d)) { converted = d; return true; }
        if (target == typeof(decimal) && decimal.TryParse(value, NumberStyles.Number, invariant, out var m)) { converted = m; return true; }
        if (target == typeof(DateTime) && DateTime.TryParse(value, invariant, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            converted = date;
            return true;
        }
        if (target.IsEnum && Enum.TryParse(target, value, ignoreCase: true, out var member) && Enum.IsDefined(target, member!))
        {
            converted = member;
            return true;
        }
        return false;
    }

    private static string Describe(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool)) return "true/false value";
        if (target == typeof(int) || target == typeof(long)) return "whole number";
        if (target == typeof(double) || target == typeof(decimal)) return "number";
        if (target == typeof(DateTime)) return "date/time";
        if (target.IsEnum) return "value (" + string.Join(", ", Enum.GetNames(target)) + ")";
        return "value";
    }
}
