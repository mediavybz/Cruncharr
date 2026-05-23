using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Cruncharr.Core.Configuration;

public class CruncharrConfig{
    [JsonPropertyName("crunchyroll")]
    [YamlMember(Alias = "crunchyroll", ApplyNamingConventions = false)]
    public CrunchyrollConfig Crunchyroll { get; set; } = new();
    
    [JsonPropertyName("download")]
    [YamlMember(Alias = "download", ApplyNamingConventions = false)]
    public DownloadConfig Download { get; set; } = new();
    
    [JsonPropertyName("history")]
    [YamlMember(Alias = "history", ApplyNamingConventions = false)]
    public HistoryConfig History { get; set; } = new();
    
    [JsonPropertyName("queue")]
    [YamlMember(Alias = "queue", ApplyNamingConventions = false)]
    public QueueConfig Queue { get; set; } = new();
    
    [JsonPropertyName("token_file")]
    [YamlMember(Alias = "token_file", ApplyNamingConventions = false)]
    public string TokenFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cruncharr", "token.json");

    [JsonPropertyName("notifications")]
    [YamlMember(Alias = "notifications", ApplyNamingConventions = false)]
    public NotificationsConfig Notifications { get; set; } = new();
    
    public void ApplyEnvironmentVariables(){
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_EMAIL")))
            Crunchyroll.Email = Environment.GetEnvironmentVariable("CRUNCHYROLL_EMAIL")!;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_PASSWORD")))
            Crunchyroll.Password = Environment.GetEnvironmentVariable("CRUNCHYROLL_PASSWORD")!;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_OUTPUT_DIR")))
            Download.OutputDirectory = Environment.GetEnvironmentVariable("CRUNCHYROLL_OUTPUT_DIR")!;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_TEMP_DIR")))
            Download.TempDirectory = Environment.GetEnvironmentVariable("CRUNCHYROLL_TEMP_DIR")!;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_WEBHOOK_URL")))
            Notifications.WebhookUrl = Environment.GetEnvironmentVariable("CRUNCHYROLL_WEBHOOK_URL")!;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_PERSIST")))
            Queue.PersistQueue = Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_PERSIST")!.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_AUTO_DOWNLOAD")))
            Queue.AutoDownload = Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_AUTO_DOWNLOAD")!.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_PROCESSING_JOBS")))
            Queue.SimultaneousProcessingJobs = int.Parse(Environment.GetEnvironmentVariable("CRUNCHYROLL_QUEUE_PROCESSING_JOBS")!);
    }
    
    public static CruncharrConfig Load(string configPath){
        if (File.Exists(configPath)){
            var content = File.ReadAllText(configPath);
            
            if (configPath.EndsWith(".yaml") || configPath.EndsWith(".yml")){
                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .Build();
                return deserializer.Deserialize<CruncharrConfig>(content) ?? new CruncharrConfig();
            }
            
            return Newtonsoft.Json.JsonConvert.DeserializeObject<CruncharrConfig>(content) ?? new CruncharrConfig();
        }
        return new CruncharrConfig();
    }
    
    public void Save(string configPath){
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        
        if (configPath.EndsWith(".yaml") || configPath.EndsWith(".yml")){
            var serializer = new SerializerBuilder()
                .Build();
            File.WriteAllText(configPath, serializer.Serialize(this));
        } else{
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(configPath, json);
        }
    }
}

public class CrunchyrollConfig{
    [YamlMember(Alias = "email", ApplyNamingConventions = false)]
    public string Email { get; set; } = "";
    
    [YamlMember(Alias = "password", ApplyNamingConventions = false)]
    public string Password { get; set; } = "";
}

public class DownloadConfig{
    [YamlMember(Alias = "output_dir", ApplyNamingConventions = false)]
    public string OutputDirectory { get; set; } = "/downloads";
    
    [YamlMember(Alias = "temp_dir", ApplyNamingConventions = false)]
    public string TempDirectory { get; set; } = "/tmp/cruncharr";
    
    [YamlMember(Alias = "filename_template", ApplyNamingConventions = false)]
    public string FilenameTemplate { get; set; } = "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}";
    
    [YamlMember(Alias = "quality", ApplyNamingConventions = false)]
    public string Quality { get; set; } = "best";
    
    [YamlMember(Alias = "dub_languages", ApplyNamingConventions = false)]
    public List<string> DubLanguages { get; set; } = new() { "ja-JP" };
    
    [YamlMember(Alias = "subtitle_languages", ApplyNamingConventions = false)]
    public List<string> SubtitleLanguages { get; set; } = new() { "en-US" };
    
    [YamlMember(Alias = "default_audio", ApplyNamingConventions = false)]
    public string DefaultAudio { get; set; } = "ja-JP";
    
    [YamlMember(Alias = "default_subtitle", ApplyNamingConventions = false)]
    public string DefaultSubtitle { get; set; } = "en-US";
    
    [YamlMember(Alias = "simultaneous_downloads", ApplyNamingConventions = false)]
    public int SimultaneousDownloads { get; set; } = 2;
    
    [YamlMember(Alias = "retry_attempts", ApplyNamingConventions = false)]
    public int RetryAttempts { get; set; } = 5;
    
    [YamlMember(Alias = "retry_delay_seconds", ApplyNamingConventions = false)]
    public int RetryDelaySeconds { get; set; } = 5;
    
    [YamlMember(Alias = "skip_muxing", ApplyNamingConventions = false)]
    public bool SkipMuxing { get; set; } = false;
    
    [YamlMember(Alias = "mux_fonts", ApplyNamingConventions = false)]
    public bool MuxFonts { get; set; } = true;
    
    [YamlMember(Alias = "include_chapters", ApplyNamingConventions = false)]
    public bool IncludeChapters { get; set; } = true;
    
    [YamlMember(Alias = "convert_vtt_to_ass", ApplyNamingConventions = false)]
    public bool ConvertVttToAss { get; set; } = true;
    
    [YamlMember(Alias = "history_enabled", ApplyNamingConventions = false)]
    public bool HistoryEnabled { get; set; } = true;
}

public class HistoryConfig{
    [YamlMember(Alias = "enabled", ApplyNamingConventions = false)]
    public bool Enabled { get; set; } = true;
    
    [YamlMember(Alias = "remove_missing", ApplyNamingConventions = false)]
    public bool RemoveMissing { get; set; } = true;
}

public class QueueConfig{
    [YamlMember(Alias = "persist", ApplyNamingConventions = false)]
    public bool PersistQueue { get; set; } = true;
    
    [YamlMember(Alias = "auto_download", ApplyNamingConventions = false)]
    public bool AutoDownload { get; set; } = true;
    
    [YamlMember(Alias = "simultaneous_processing_jobs", ApplyNamingConventions = false)]
    public int SimultaneousProcessingJobs { get; set; } = 2;
    
    [YamlMember(Alias = "queue_file_path", ApplyNamingConventions = false)]
    public string QueueFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "Cruncharr", "queue.json");
}

public class NotificationsConfig{
    [YamlMember(Alias = "webhook_url", ApplyNamingConventions = false)]
    public string? WebhookUrl { get; set; }
    
    [YamlMember(Alias = "webhook_method", ApplyNamingConventions = false)]
    public string WebhookMethod { get; set; } = "POST";
    
    [YamlMember(Alias = "on_complete", ApplyNamingConventions = false)]
    public bool OnComplete { get; set; } = true;
    
    [YamlMember(Alias = "on_error", ApplyNamingConventions = false)]
    public bool OnError { get; set; } = true;
}
