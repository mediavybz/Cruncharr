using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Cruncharr.Core.Utils.Parser.Utils;

#pragma warning disable IL2026

namespace Cruncharr.Core.Utils.Parser.Segments;

public class TimelineTimeParser
{
    public static int GetLiveRValue(dynamic attributes, long time, long duration)
    {
        object attributeObject = attributes;
        var now = (Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "NOW") ?? 0) +
                   Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "clientOffset") ?? 0)) / 1000.0;
        var availabilityStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "availabilityStartTime") ?? 0);
        var periodStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "periodStart") ?? 0);
        var minimumUpdatePeriod = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "minimumUpdatePeriod") ?? 0);
        var timescale = Convert.ToInt64(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
        var periodStartWallClock = availabilityStart + periodStart;

        return (int)Math.Ceiling(((now + minimumUpdatePeriod - periodStartWallClock) * timescale - time) / duration);
    }

    public static List<dynamic> ParseByTimeline(dynamic attributes, IEnumerable<dynamic> segmentTimeline)
    {
        object attributeObject = attributes;
        var timelineEntries = segmentTimeline.Cast<object>().ToList();
        var segments = new List<dynamic>();
        var type = Convert.ToString(ObjectUtilities.GetMemberValue(attributeObject, "type")) ?? string.Empty;
        var minimumUpdatePeriod = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "minimumUpdatePeriod") ?? 0);
        var media = Convert.ToString(ObjectUtilities.GetMemberValue(attributeObject, "media")) ?? string.Empty;
        var sourceDuration = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "sourceDuration") ?? 0);
        var timescale = Convert.ToInt64(ObjectUtilities.GetMemberValue(attributeObject, "timescale") ?? 1);
        var startNumber = Convert.ToInt32(ObjectUtilities.GetMemberValue(attributeObject, "startNumber") ?? 1);
        var timeline = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributeObject, "periodStart") ?? 0);
        long time = -1;

        for (var index = 0; index < timelineEntries.Count; index++)
        {
            var entry = timelineEntries[index];
            var duration = Convert.ToInt64(ObjectUtilities.GetMemberValue(entry, "d"));
            var repeat = Convert.ToInt32(ObjectUtilities.GetMemberValue(entry, "r") ?? 0);
            var segmentTime = Convert.ToInt64(ObjectUtilities.GetMemberValue(entry, "t") ?? 0);

            if (time < 0 || segmentTime > 0 && segmentTime > time) time = segmentTime;

            int count;
            if (repeat >= 0)
            {
                count = repeat + 1;
            }
            else if (index + 1 < timelineEntries.Count)
            {
                var nextTime = Convert.ToInt64(ObjectUtilities.GetMemberValue(timelineEntries[index + 1], "t"));
                count = (int)((nextTime - time) / duration);
            }
            else if (type == "dynamic" && minimumUpdatePeriod > 0 &&
                     media.IndexOf("$Number$", StringComparison.Ordinal) > 0)
            {
                count = GetLiveRValue(attributes, time, duration);
            }
            else
            {
                count = (int)((sourceDuration * timescale - time) / duration);
            }

            var end = startNumber + segments.Count + Math.Max(0, count);
            for (var number = startNumber + segments.Count; number < end; number++)
            {
                dynamic segment = new ExpandoObject();
                segment.number = number;
                segment.duration = duration / (double)timescale;
                segment.time = time;
                segment.timeline = timeline;
                segments.Add(segment);
                time += duration;
            }
        }

        return segments;
    }
}

#pragma warning restore IL2026
