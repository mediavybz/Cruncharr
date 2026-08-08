#nullable disable
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Cruncharr.Core.Utils.Parser.Utils;

namespace Cruncharr.Core.Utils.Parser.Segments;

public class DurationTimeParser
{
    public static int? ParseEndNumber(object endNumber)
    {
        return endNumber != null && int.TryParse(endNumber.ToString(), out var parsed)
            ? parsed
            : null;
    }

    public static dynamic GetSegmentRangeStatic(dynamic attributes)
    {
        object attributeObject = attributes;
        var timescale = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
        var duration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "duration"));
        var segmentDuration = duration / timescale;
        int? endNumber = ParseEndNumber(
            (object)ObjectUtilities.GetMemberValue(attributeObject, "endNumber"));

        if (endNumber.HasValue)
        {
            return new { start = 0, end = (double)endNumber.Value };
        }

        var periodDuration = ObjectUtilities.GetMemberValue(attributeObject, "periodDuration");
        if (periodDuration != null)
        {
            return new { start = 0, end = Convert.ToDouble(periodDuration) / segmentDuration };
        }

        var sourceDuration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "sourceDuration") ?? 0);
        return new { start = 0, end = sourceDuration / segmentDuration };
    }

    public static dynamic GetSegmentRangeDynamic(dynamic attributes)
    {
        object attributeObject = attributes;
        var now = (Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "NOW") ?? 0) +
                   Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "clientOffset") ?? 0)) / 1000.0;
        var availabilityStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "availabilityStartTime") ?? 0);
        var periodStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "periodStart") ?? 0);
        var minimumUpdatePeriod = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "minimumUpdatePeriod") ?? 0);
        var timeShiftBufferDepth = Convert.ToDouble(
            ObjectUtilities.GetMemberValue(attributeObject, "timeShiftBufferDepth") ?? double.PositiveInfinity);
        var timescale = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
        var duration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "duration"));
        var periodStartWallClock = availabilityStart + periodStart;

        var segmentCount = (int)Math.Ceiling((now + minimumUpdatePeriod - periodStartWallClock) * timescale / duration);
        var availableStart = (int)Math.Floor((now - periodStartWallClock - timeShiftBufferDepth) * timescale / duration);
        var availableEnd = (int)Math.Floor((now - periodStartWallClock) * timescale / duration);
        int? endNumber = ParseEndNumber(
            (object)ObjectUtilities.GetMemberValue(attributeObject, "endNumber"));

        return new
        {
            start = Math.Max(0, availableStart),
            end = endNumber ?? Math.Min(segmentCount, availableEnd)
        };
    }

    public static dynamic ToSegment(dynamic attributes, int number)
    {
        object attributeObject = attributes;
        var timescale = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
        var periodStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "periodStart") ?? 0);
        var startNumber = Convert.ToInt32(ObjectUtilities.GetMemberValue(attributeObject, "startNumber") ?? 1);
        var duration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "duration"));

        dynamic segment = new ExpandoObject();
        segment.number = startNumber + number;
        segment.duration = duration / timescale;
        segment.timeline = periodStart;
        segment.time = number * duration;
        return segment;
    }

    public static List<dynamic> ParseByDuration(dynamic attributes)
    {
        object attributeObject = attributes;
        var type = Convert.ToString(ObjectUtilities.GetMemberValue(attributeObject, "type")) ?? "static";
        dynamic range = type == "static"
            ? GetSegmentRangeStatic(attributes)
            : GetSegmentRangeDynamic(attributes);

        var segments = new List<dynamic>();
        foreach (var number in Range((int)range.start, Convert.ToDouble(range.end)))
        {
            segments.Add(ToSegment(attributes, number));
        }

        if (type == "static" && segments.Count > 0)
        {
            var lastIndex = segments.Count - 1;
            var periodDuration = ObjectUtilities.GetMemberValue(attributeObject, "periodDuration");
            var sectionDuration = Convert.ToDouble(periodDuration ??
                ObjectUtilities.GetMemberValue(attributeObject, "sourceDuration") ?? 0);
            var duration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "duration"));
            var timescale = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
            segments[lastIndex].duration = sectionDuration - duration / timescale * lastIndex;
        }

        return segments;
    }

    public static List<int> Range(int start, double end)
    {
        var result = new List<int>();
        for (var value = start; value < end; value++) result.Add(value);
        return result;
    }
}
