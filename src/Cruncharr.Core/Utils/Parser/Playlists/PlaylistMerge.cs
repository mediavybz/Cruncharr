#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Cruncharr.Core.Utils.Parser.Utils;

namespace Cruncharr.Core.Utils.Parser;

public class PlaylistMerge
{
    public static List<dynamic> Union(List<List<dynamic>> lists, Func<dynamic, dynamic> keyFunction)
    {
        var uniqueElements = new Dictionary<dynamic, dynamic>();

        foreach (var list in lists)
        {
            foreach (var element in list)
            {
                dynamic key = keyFunction(element);
                if (!uniqueElements.ContainsKey(key))
                {
                    uniqueElements[key] = element;
                }
            }
        }

        return uniqueElements.Values.ToList();
    }

    public static List<dynamic> GetUniqueTimelineStarts(List<List<dynamic>> timelineStarts)
    {
        var uniqueStarts = Union(timelineStarts, el => el.timeline);

        return uniqueStarts.OrderBy(el => el.timeline).ToList();
    }

    public static dynamic PositionManifestOnTimeline(dynamic oldManifest, dynamic newManifest)
    {
        List<dynamic> oldPlaylists = new List<dynamic>(oldManifest.playlists);
        oldPlaylists.AddRange(GetMediaGroupPlaylists(oldManifest));
        List<dynamic> newPlaylists = new List<dynamic>(newManifest.playlists);
        newPlaylists.AddRange(GetMediaGroupPlaylists(newManifest));

        newManifest.timelineStarts = GetUniqueTimelineStarts(new List<List<dynamic>> { oldManifest.timelineStarts, newManifest.timelineStarts });

        UpdateSequenceNumbers(oldPlaylists, newPlaylists, newManifest.timelineStarts);

        return newManifest;
    }

    private static readonly string[] SupportedMediaTypes = { "AUDIO", "SUBTITLES" };

    public static List<dynamic> GetMediaGroupPlaylists(dynamic manifest)
    {
        var mediaGroupPlaylists = new List<dynamic>();

        foreach (var mediaType in SupportedMediaTypes)
        {
            var allMediaGroups = (IDictionary<string, object>)manifest.mediaGroups;
            var mediaGroups = (IDictionary<string, object>)allMediaGroups[mediaType];
            foreach (var groupKey in mediaGroups.Keys)
            {
                var labels = (IDictionary<string, object>)mediaGroups[groupKey];
                foreach (var labelKey in labels.Keys)
                {
                    var properties = (dynamic)labels[labelKey];
                    if (properties.playlists != null)
                    {
                        mediaGroupPlaylists.AddRange(properties.playlists);
                    }
                }
            }
        }

        return mediaGroupPlaylists;
    }

    private const double TimeFudge = 1 / (double)60;

    public static void UpdateSequenceNumbers(List<dynamic> oldPlaylists, List<dynamic> newPlaylists, List<dynamic> timelineStarts)
    {
        foreach (dynamic playlist in newPlaylists)
        {
            playlist.discontinuitySequence = timelineStarts.FindIndex(ts => ts.timeline == playlist.timeline);

            dynamic oldPlaylist = FindPlaylistWithName(oldPlaylists, playlist.attributes.NAME);

            if (oldPlaylist == null)
            {
                continue;
            }

            if (ObjectUtilities.GetMemberValue(playlist, "sidx") != null)
            {
                continue;
            }

            var newSegments = ((IEnumerable<object>)playlist.segments).Cast<dynamic>().ToList();
            if (newSegments.Count == 0) continue;

            dynamic firstNewSegment = newSegments[0];
            var oldSegments = ((IEnumerable<object>)oldPlaylist.segments).Cast<dynamic>().ToList();
            var oldMatchingSegmentIndex = oldSegments.FindIndex(
                oldSegment => Math.Abs(oldSegment.presentationTime - firstNewSegment.presentationTime) < TimeFudge
            );

            if (oldMatchingSegmentIndex == -1)
            {
                UpdateMediaSequenceForPlaylist(playlist, oldPlaylist.mediaSequence + oldSegments.Count);
                firstNewSegment.discontinuity = true;
                playlist.discontinuityStarts.Insert(0, 0);

                if ((oldSegments.Count == 0 && playlist.timeline > oldPlaylist.timeline) ||
                    (oldSegments.Count > 0 && playlist.timeline > oldSegments.Last().timeline))
                {
                    playlist.discontinuitySequence--;
                }

                continue;
            }

            var oldMatchingSegment = oldSegments[oldMatchingSegmentIndex];

            if ((ObjectUtilities.GetMemberValue(oldMatchingSegment, "discontinuity") as bool? ?? false) &&
                !(ObjectUtilities.GetMemberValue(firstNewSegment, "discontinuity") as bool? ?? false))
            {
                firstNewSegment.discontinuity = true;
                playlist.discontinuityStarts.Insert(0, 0);
                playlist.discontinuitySequence--;
            }

            UpdateMediaSequenceForPlaylist(playlist, oldMatchingSegment.number);
        }
    }

    public static dynamic FindPlaylistWithName(List<dynamic> playlists, string name)
    {
        return playlists.FirstOrDefault(playlist =>
            string.Equals(
                Convert.ToString(ObjectUtilities.GetMemberValue(playlist.attributes, "NAME")),
                name,
                StringComparison.Ordinal));
    }

    public static void UpdateMediaSequenceForPlaylist(dynamic playlist, int mediaSequence)
    {
        playlist.mediaSequence = mediaSequence;

        if (playlist.segments == null) return;

        for (int index = 0; index < playlist.segments.Count; index++)
        {
            playlist.segments[index].number = playlist.mediaSequence + index;
        }
    }
}
