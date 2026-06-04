using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Xml;
using Cruncharr.Core.Utils.Parser.Utils;

namespace Cruncharr.Core.Utils.Parser;

public class ParseAttribute
{
    public static Dictionary<string, Func<string, object>> ParsersDictionary = new Dictionary<string, Func<string, object>>{
        { "mediaPresentationDuration", MediaPresentationDuration },
        { "availabilityStartTime", AvailabilityStartTime },
        { "minimumUpdatePeriod", MinimumUpdatePeriod },
        { "suggestedPresentationDelay", SuggestedPresentationDelay },
        { "type", Type },
        { "timeShiftBufferDepth", TimeShiftBufferDepth },
        { "start", Start },
        { "width", Width },
        { "height", Height },
        { "bandwidth", Bandwidth },
        { "audioSamplingRate", AudioSamplingRate },
        { "frameRate", FrameRate },
        { "startNumber", StartNumber },
        { "timescale", Timescale },
        { "presentationTimeOffset", PresentationTimeOffset },
        { "duration", Duration },
        { "d", D },
        { "t", T },
        { "r", R },
        { "presentationTime", PresentationTime },
        { "DEFAULT", DefaultParser }
    };

    public static object MediaPresentationDuration(string value) => DurationParser.ParseDuration(value);
    public static object AvailabilityStartTime(string value) => DurationParser.ParseDate(value) / 1000;
    public static object MinimumUpdatePeriod(string value) => DurationParser.ParseDuration(value);
    public static object SuggestedPresentationDelay(string value) => DurationParser.ParseDuration(value);
    public static object Type(string value) => value;
    public static object TimeShiftBufferDepth(string value) => DurationParser.ParseDuration(value);
    public static object Start(string value) => DurationParser.ParseDuration(value);
    public static object Width(string value) => int.Parse(value);
    public static object Height(string value) => int.Parse(value);
    public static object Bandwidth(string value) => int.Parse(value);
    public static object AudioSamplingRate(string value) => int.Parse(value);
    public static object FrameRate(string value) => DivisionValueParser.ParseDivisionValue(value);
    public static object StartNumber(string value) => int.Parse(value);
    public static object Timescale(string value) => int.Parse(value);
    public static object PresentationTimeOffset(string value) => int.Parse(value);

    public static object Duration(string value)
    {
        if (int.TryParse(value, out int parsedValue))
        {
            return parsedValue;
        }

        return DurationParser.ParseDuration(value);
    }

    public static object D(string value) => int.Parse(value);
    public static object T(string value) => int.Parse(value);
    public static object R(string value) => int.Parse(value);
    public static object PresentationTime(string value) => int.Parse(value);
    public static object DefaultParser(string value) => value;

    public static dynamic ParseAttributes(XmlNode? el)
    {
        var expandoObj = new ExpandoObject() as IDictionary<string, object>;

        if (el is { Attributes: not null })
        {
            foreach (XmlAttribute attr in el.Attributes)
            {
                Func<string, object>? parseFn;
                if (ParsersDictionary.TryGetValue(attr.Name, out parseFn))
                {
                    expandoObj[attr.Name] = parseFn(attr.Value);
                }
                else
                {
                    expandoObj[attr.Name] = DefaultParser(attr.Value);
                }
            }
        }

        return expandoObj;
    }
}
