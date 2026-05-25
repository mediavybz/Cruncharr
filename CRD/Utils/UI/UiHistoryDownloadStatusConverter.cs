using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CRD.Downloader.Crunchyroll;
using CRD.Utils;
using CRD.Utils.Structs.History;

namespace CRD.Utils.UI;

public class UiHistoryDownloadStatusConverter : IMultiValueConverter{
    public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture){
        var episode = values.Count > 0 && values[0] is HistoryEpisode ep ? ep : null;
        var series = values.Count > 1 && values[1] is HistorySeries hs ? hs : null;
        var season = values.Count > 2 && values[2] is HistorySeason hsn ? hsn : null;

        var isTooltip = string.Equals(parameter?.ToString(), "Tooltip", StringComparison.OrdinalIgnoreCase);

        if (episode == null || !episode.WasDownloaded){
            return isTooltip ? "Mark as downloaded" : Brushes.Gray;
        }

        var requestedDubs = HistorySeries.GetEffectiveDubLang(series, season);
        var requestedSoftSubs = HistorySeries.GetEffectiveSoftSubs(series, season, episode);
        var isPartial = episode.IsPartiallyDownloaded(requestedDubs, requestedSoftSubs);

        if (isTooltip){
            return BuildTooltip(episode, isPartial);
        }

        return isPartial ? new SolidColorBrush(Color.Parse("#f78c25")) : new SolidColorBrush(Color.Parse("#21a556"));
    }

    public IList<object> ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static string BuildTooltip(HistoryEpisode episode, bool isPartial){
        var lines = new List<string>{
            isPartial ? "Downloaded with missing selected dubs/subs" : "Downloaded"
        };

        if (episode.DownloadedDubLang.Count > 0){
            lines.Add($"Dubs: {string.Join(", ", episode.DownloadedDubLang)}");
        }

        if (episode.DownloadedSoftSubs.Count > 0){
            lines.Add($"Subs: {string.Join(", ", episode.DownloadedSoftSubs)}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
