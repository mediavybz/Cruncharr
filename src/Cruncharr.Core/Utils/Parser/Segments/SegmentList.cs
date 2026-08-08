using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Cruncharr.Core.Utils.Parser.Utils;

namespace Cruncharr.Core.Utils.Parser.Segments;

public class SegmentList
{
    public static List<dynamic> SegmentsFromList(dynamic attributes, List<dynamic> segmentTimeline)
    {
        var duration = ObjectUtilities.GetMemberValue(attributes, "duration");
        if ((duration == null && segmentTimeline == null) ||
            (duration != null && segmentTimeline != null))
        {
            throw new Exception("Segment time unspecified");
        }

        var segmentUrls = (ObjectUtilities.GetMemberValue(attributes, "segmentUrls") as IEnumerable<object>)?
            .Cast<dynamic>()
            .ToList() ?? [];
        var segmentUrlMap = segmentUrls.Select(segmentUrlObject => SegmentURLToSegmentObject(attributes, segmentUrlObject)).ToList();

        List<dynamic> segmentTimeInfo = duration != null
            ? DurationTimeParser.ParseByDuration(attributes)
            : TimelineTimeParser.ParseByTimeline(attributes, segmentTimeline);

        var segments = segmentTimeInfo.Select((segmentTime, index) =>
        {
            if (index >= segmentUrlMap.Count) return null;

            var segment = segmentUrlMap[index];
            var timescale = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributes, "timescale") ?? 1);
            var presentationTimeOffset = Convert.ToDouble(
                ObjectUtilities.GetMemberValue(attributes, "presentationTimeOffset") ?? 0);
            var periodStart = Convert.ToDouble(ObjectUtilities.GetMemberValue(attributes, "periodStart") ?? 0);

            segment.timeline = ObjectUtilities.GetMemberValue(segmentTime, "timeline");
            segment.duration = ObjectUtilities.GetMemberValue(segmentTime, "duration");
            segment.number = ObjectUtilities.GetMemberValue(segmentTime, "number");
            segment.presentationTime = periodStart +
                (Convert.ToDouble(ObjectUtilities.GetMemberValue(segmentTime, "time")) - presentationTimeOffset) /
                timescale;

            return segment;
        }).Where(segment => segment != null).Cast<dynamic>().ToList();

        return segments;
    }

    public static dynamic SegmentURLToSegmentObject(dynamic attributes, dynamic segmentUrl)
    {
        dynamic initialization = ObjectUtilities.GetMemberValue(attributes, "initialization") ?? new ExpandoObject();
        var initSegment = UrlType.UrlTypeToSegment(new
        {
            baseUrl = ObjectUtilities.GetMemberValue(attributes, "baseUrl"),
            source = ObjectUtilities.GetMemberValue(initialization, "sourceURL"),
            range = ObjectUtilities.GetMemberValue(initialization, "range")
        });

        var segment = UrlType.UrlTypeToSegment(new
        {
            baseUrl = ObjectUtilities.GetMemberValue(attributes, "baseUrl"),
            source = ObjectUtilities.GetMemberValue(segmentUrl, "media"),
            range = ObjectUtilities.GetMemberValue(segmentUrl, "mediaRange")
        });

        segment.map = initSegment;
        return segment;
    }
}
