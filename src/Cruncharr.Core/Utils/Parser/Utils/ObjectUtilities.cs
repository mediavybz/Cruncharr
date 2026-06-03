#nullable disable
using System;
using System.Collections.Generic;
using System.Dynamic;

#pragma warning disable IL2026

namespace Cruncharr.Core.Utils.Parser.Utils;

public class ObjectUtilities{
    public static ExpandoObject MergeExpandoObjects(dynamic target, dynamic source){
        var result = new ExpandoObject();
        var resultDict = result as IDictionary<string, object>;

        var targetDict = target as IDictionary<string, object>;
        var sourceDict = source as IDictionary<string, object>;

        if (targetDict == null && sourceDict == null){
            Console.WriteLine("Nothing Merged; both are empty");
            return result;
        }

        if (targetDict != null){
            foreach (var kvp in targetDict){
                resultDict[kvp.Key] = kvp.Value;
            }
        }

        if (sourceDict != null){
            foreach (var kvp in sourceDict){
                resultDict[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    public static void SetAttributeWithDefault(dynamic ob, string attributeName, string defaultValue){
        var obDict = ob as IDictionary<string, object>;

        if (obDict == null){
            throw new ArgumentException("Provided object must be an ExpandoObject.");
        }

        if (obDict.TryGetValue(attributeName, out object value) && value != null && !string.IsNullOrEmpty(value.ToString())){
            obDict[attributeName] = value;
        } else{
            obDict[attributeName] = defaultValue;
        }
    }

    public static object GetAttributeWithDefault(dynamic ob, string attributeName, string? defaultValue){
        var obDict = ob as IDictionary<string, object>;

        if (obDict == null){
            throw new ArgumentException("Provided object must be an ExpandoObject.");
        }

        if (obDict.TryGetValue(attributeName, out object value) && value != null && !string.IsNullOrEmpty(value.ToString())){
            return value;
        } else{
            return defaultValue;
        }
    }

    public static void SetFieldFromOrToDefault(dynamic targetObject, string fieldToSet, string fieldToGetValueFrom, object defaultValue){
        var targetDict = targetObject as IDictionary<string, object>;

        if (targetDict == null){
            throw new ArgumentException("Provided targetObject must be an ExpandoObject.");
        }

        object valueToSet = defaultValue;
        if (targetDict.TryGetValue(fieldToGetValueFrom, out object valueFromField) && valueFromField != null){
            valueToSet = valueFromField;
        }

        targetDict[fieldToSet] = valueToSet;
    }

    public static object? GetMemberValue(dynamic obj, string memberName){
        if (obj is ExpandoObject expando){
            var dictionary = (IDictionary<string, object?>)expando;
            if (dictionary.TryGetValue(memberName, out object? value)){
                return value;
            }
        } else if (obj != null){
            try{
                return obj.GetType().GetProperty(memberName)?.GetValue(obj, null) ??
                       obj.GetType().GetField(memberName)?.GetValue(obj);
            } catch{
            }
        }

        return null;
    }
}

#pragma warning restore IL2026
