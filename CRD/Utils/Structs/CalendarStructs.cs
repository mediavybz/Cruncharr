using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using CRD.Downloader;
using CRD.Downloader.Crunchyroll;

namespace CRD.Utils.Structs;

public class CalendarWeek{
    public DateTime FirstDayOfWeek{ get; set; }
    public string? FirstDayOfWeekString{ get; set; }
    public List<CalendarDay>? CalendarDays{ get; set; }
}

public class CalendarDay{
    public DateTime DateTime{ get; set; }
    public string? DayName{ get; set; }
    public List<CalendarEpisode> CalendarEpisodes{ get; set; } =[];
}

public enum CalendarHistoryDownloadState{
    None,
    NotDownloaded,
    PartlyDownloaded,
    Downloaded
}

public partial class CalendarEpisode : INotifyPropertyChanged{
    private bool _isInHistory;
    private bool _showHistoryMark = true;
    private CalendarHistoryDownloadState _historyDownloadState;

    public DateTime DateTime{ get; set; }
    public bool? HasPassed{ get; set; }
    public string? EpisodeName{ get; set; }
    public string? SeriesUrl{ get; set; }
    public string? EpisodeUrl{ get; set; }
    public string? ThumbnailUrl{ get; set; }
    public Bitmap? ImageBitmap{ get; set; }

    public string? EpisodeNumber{ get; set; }

    public bool IsPremiumOnly{ get; set; }
    public bool IsPremiere{ get; set; }

    public string? SeasonName{ get; set; }

    public string? CrSeriesID{ get; set; }

    public string? CrSeasonID{ get; set; }

    public string? CrEpisodeID{ get; set; }

    public bool AnilistEpisode{ get; set; }

    public bool FilteredOut{ get; set; }
    
    public Locale? AudioLocale{ get; set; }

    public List<CrBrowseEpisodeVersion>? Versions{ get; set; }

    public string? OriginalEpisodeGuid{ get; set; }

    public string? OriginalSeasonGuid{ get; set; }

    public List<string> VersionGuids{ get; set; } =[];

    public bool IsInHistory{
        get => _isInHistory;
        set{
            if (_isInHistory == value){
                return;
            }

            _isInHistory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInHistory)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHistoryMarkVisible)));
        }
    }

    public bool ShowHistoryMark{
        get => _showHistoryMark;
        set{
            if (_showHistoryMark == value){
                return;
            }

            _showHistoryMark = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHistoryMark)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHistoryMarkVisible)));
        }
    }

    public bool IsHistoryMarkVisible => IsInHistory && ShowHistoryMark && !AnilistEpisode;

    public CalendarHistoryDownloadState HistoryDownloadState{
        get => _historyDownloadState;
        set{
            if (_historyDownloadState == value){
                return;
            }

            _historyDownloadState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryDownloadState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryDownloadStatusBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryDownloadStatusTooltip)));
        }
    }

    public IBrush HistoryDownloadStatusBrush => HistoryDownloadState switch{
        CalendarHistoryDownloadState.Downloaded => new SolidColorBrush(Color.Parse("#21a556")),
        CalendarHistoryDownloadState.PartlyDownloaded => new SolidColorBrush(Color.Parse("#f78c25")),
        CalendarHistoryDownloadState.NotDownloaded => Brushes.Gray,
        _ => Brushes.Transparent
    };

    public string HistoryDownloadStatusTooltip => HistoryDownloadState switch{
        CalendarHistoryDownloadState.Downloaded => "Downloaded",
        CalendarHistoryDownloadState.PartlyDownloaded => "Partly downloaded",
        CalendarHistoryDownloadState.NotDownloaded => "Not downloaded",
        _ => string.Empty
    };
    
    public List<CalendarEpisode> CalendarEpisodes{ get; set; } =[];

    public event PropertyChangedEventHandler? PropertyChanged;

    [RelayCommand]
    public async Task AddEpisodeToQue(){
        if (EpisodeUrl != null){
            var match = Regex.Match(EpisodeUrl, "/([^/]+)/watch/([^/]+)");

            if (match.Success){
                var locale = match.Groups[1].Value; // Capture the locale part
                var id = match.Groups[2].Value; // Capture the ID part
                await CrunchyrollManager.Instance.CrQueue.CrAddEpisodeToQueue(id, Languages.Locale2language(locale).CrLocale, CrunchyrollManager.Instance.CrunOptions.DubLang, true);
            }
        }

        if (CalendarEpisodes.Count > 0){
            foreach (var calendarEpisode in CalendarEpisodes){
                calendarEpisode.AddEpisodeToQue();
            }
        }
    }

    public async Task LoadImage(int width = 0, int height = 0){
        try{
            if (!string.IsNullOrEmpty(ThumbnailUrl)){
                ImageBitmap = await Helpers.LoadImage(ThumbnailUrl, width, height);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageBitmap)));
            }
        } catch (Exception ex){
            // Handle exceptions
            Console.Error.WriteLine("Failed to load image: " + ex.Message);
        }
    }
}
