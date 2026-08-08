#nullable disable
#pragma warning disable CS8632
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;

#pragma warning disable IL2026

namespace Cruncharr.Core.Utils.Parser.Utils;

public class ObjectUtilities
{
    public static ExpandoObject MergeExpandoObjects(dynamic target, dynamic source)
    {
        var result = new ExpandoObject();
        var resultDict = result as IDictionary<string, object>;
        CopyMembers(target, resultDict);
        CopyMembers(source, resultDict);
        return result;
    }

    // MPD inheritance frequently merges ExpandoObjects with anonymous objects. The old
    // implementation silently ignored anonymous-object properties, losing initialization,
    // segment URL, and period metadata along the way.
    private static void CopyMembers(object? source, IDictionary<string, object> target)
    {
        if (source == null) return;

        if (source is IDictionary<string, object> dictionary)
        {
            foreach (var item in dictionary)
            {
                target[item.Key] = item.Value;
            }
            return;
        }

        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                target[property.Name] = property.GetValue(source)!;
            }
        }
    }

    public static void SetAttributeWithDefault(dynamic ob, string attributeName, string defaultValue)
    {
        var obDict = ob as IDictionary<string, object>;

        if (obDict == null)
        {
            throw new ArgumentException("Provided object must be an ExpandoObject.");
        }

        if (obDict.TryGetValue(attributeName, out object value) && value != null && !string.IsNullOrEmpty(value.ToString()))
        {
            obDict[attributeName] = value;
        }
        else
        {
            obDict[attributeName] = defaultValue;
        }
    }

    public static object GetAttributeWithDefault(dynamic ob, string attributeName, string? defaultValue)
    {
        var obDict = ob as IDictionary<string, object>;

        if (obDict == null)
        {
            throw new ArgumentException("Provided object must be an ExpandoObject.");
        }

        if (obDict.TryGetValue(attributeName, out object value) && value != null && !string.IsNullOrEmpty(value.ToString()))
        {
            return value;
        }
        else
        {
            return defaultValue;
        }
    }

    public static void SetFieldFromOrToDefault(dynamic targetObject, string fieldToSet, string fieldToGetValueFrom, object defaultValue)
    {
        var targetDict = targetObject as IDictionary<string, object>;

        if (targetDict == null)
        {
            throw new ArgumentException("Provided targetObject must be an ExpandoObject.");
        }

        object valueToSet = defaultValue;
        if (targetDict.TryGetValue(fieldToGetValueFrom, out object valueFromField) && valueFromField != null)
        {
            valueToSet = valueFromField;
        }

        targetDict[fieldToSet] = valueToSet;
    }

    public static object? GetMemberValue(dynamic obj, string memberName)
    {
        if (obj is ExpandoObject expando)
        {
            var dictionary = (IDictionary<string, object?>)expando;
            if (dictionary.TryGetValue(memberName, out object? value))
            {
                return value;
            }
        }
        else if (obj != null)
        {
            try
            {
                return obj.GetType().GetProperty(memberName)?.GetValue(obj, null) ??
                       obj.GetType().GetField(memberName)?.GetValue(obj);
            }
            catch
            {
            }
        }

        return null;
    }
}

#pragma warning restore IL2026
