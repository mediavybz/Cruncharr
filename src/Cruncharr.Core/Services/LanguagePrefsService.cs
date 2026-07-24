using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Cruncharr.Core.Services;

/// <summary>
/// Persisted state for adaptive language defaults. Counts how often the user picks each audio/
/// subtitle locale as the PRIMARY language of a download, so the app can suggest making a
/// frequently-chosen language the default (a "frecency"/MRU style learned preference).
/// </summary>
public class LanguagePrefsState
{
    // Opt-in: nothing is learned or suggested until the user turns it on.
    public bool Enabled { get; set; } = false;

    // locale -> pick count (recency-biased via decay-on-cap, see RecordPick).
    public Dictionary<string, int> AudioCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> SubCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Locales the user said "don't ask again" for -> never suggested until reset.
    public List<string> AudioDeclined { get; set; } = new();
    public List<string> SubDeclined { get; set; } = new();

    // Locale -> count at the moment the user hit "Not now". Re-suggested only once the count
    // climbs another MARGIN above that snapshot, so "Not now" snoozes without nagging.
    public Dictionary<string, int> AudioSnooze { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> SubSnooze { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A pending "make this your default?" suggestion for one category.</summary>
public record LanguageSuggestion(string Category, string Locale, int Count, int CurrentDefaultCount);

public interface ILanguagePrefsService
{
    LanguagePrefsState State { get; }
    bool Enabled { get; }
    void SetEnabled(bool enabled);
    /// <summary>Record one primary-language pick. category is "audio" or "sub". No-op when disabled or locale blank.</summary>
    void RecordPick(string category, string? locale);
    /// <summary>The single highest-priority suggestion (audio before sub), or null. Pass the CURRENT config defaults.</summary>
    LanguageSuggestion? GetSuggestion(string? currentAudioDefault, string? currentSubDefault);
    void Decline(string category, string locale);
    void Dismiss(string category, string locale);
    void Reset();
}

public class LanguagePrefsService : ILanguagePrefsService
{
    // Industry-standard-ish tuning for a confirm-prompt learned default:
    //  - MinPicks: don't pester a brand-new user; need real signal first.
    //  - Margin: the challenger must lead the current default by this much to prompt (sticky).
    //  - DecayCap: when any count hits this, halve all counts so recent habits outweigh old ones.
    private const int MinPicks = 5;
    private const int Margin = 5;
    private const int DecayCap = 40;

    private readonly string? _path;
    private readonly ILogger<LanguagePrefsService>? _logger;
    private readonly object _lock = new();
    private LanguagePrefsState _state = new();

    public LanguagePrefsService(string? path = null, ILogger<LanguagePrefsService>? logger = null)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    public LanguagePrefsState State
    {
        get
        {
            lock (_lock)
            {
                return new LanguagePrefsState
                {
                    Enabled = _state.Enabled,
                    AudioCounts = new Dictionary<string, int>(_state.AudioCounts, StringComparer.OrdinalIgnoreCase),
                    SubCounts = new Dictionary<string, int>(_state.SubCounts, StringComparer.OrdinalIgnoreCase),
                    AudioDeclined = new List<string>(_state.AudioDeclined),
                    SubDeclined = new List<string>(_state.SubDeclined),
                    AudioSnooze = new Dictionary<string, int>(_state.AudioSnooze, StringComparer.OrdinalIgnoreCase),
                    SubSnooze = new Dictionary<string, int>(_state.SubSnooze, StringComparer.OrdinalIgnoreCase)
                };
            }
        }
    }
    public bool Enabled { get { lock (_lock) return _state.Enabled; } }

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _state.Enabled = enabled;
            Save();
        }
    }

    public void RecordPick(string category, string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        lock (_lock)
        {
            if (!_state.Enabled) return;
            var counts = CountsFor(category);
            if (counts == null) return;
            counts[locale] = counts.GetValueOrDefault(locale, 0) + 1;

            // Decay-on-cap: keep counts bounded and bias toward recent behaviour.
            if (counts.Count > 0 && counts.Values.Max() >= DecayCap)
            {
                foreach (var key in counts.Keys.ToList())
                {
                    var halved = counts[key] / 2;
                    if (halved <= 0) counts.Remove(key);
                    else counts[key] = halved;
                }
            }
            Save();
        }
    }

    public LanguageSuggestion? GetSuggestion(string? currentAudioDefault, string? currentSubDefault)
    {
        lock (_lock)
        {
            if (!_state.Enabled) return null;
            // Audio takes priority so we only ever surface one prompt at a time.
            return Evaluate("audio", _state.AudioCounts, _state.AudioDeclined, _state.AudioSnooze, currentAudioDefault)
                ?? Evaluate("sub", _state.SubCounts, _state.SubDeclined, _state.SubSnooze, currentSubDefault);
        }
    }

    private static LanguageSuggestion? Evaluate(string category, Dictionary<string, int> counts,
        List<string> declined, Dictionary<string, int> snooze, string? currentDefault)
    {
        if (counts.Count == 0) return null;
        var leader = counts.OrderByDescending(kv => kv.Value).First();
        var locale = leader.Key;
        var count = leader.Value;

        if (count < MinPicks) return null;
        if (!string.IsNullOrEmpty(currentDefault) && string.Equals(locale, currentDefault, StringComparison.OrdinalIgnoreCase))
            return null; // already the default
        if (declined.Any(d => string.Equals(d, locale, StringComparison.OrdinalIgnoreCase)))
            return null; // user said don't ask again

        var currentCount = string.IsNullOrEmpty(currentDefault) ? 0 : counts.GetValueOrDefault(currentDefault, 0);
        if (count - currentCount < Margin) return null; // not a clear enough lead yet

        if (snooze.TryGetValue(locale, out var snoozedAt) && count - snoozedAt < Margin)
            return null; // "Not now" snooze still in effect

        return new LanguageSuggestion(category, locale, count, currentCount);
    }

    public void Decline(string category, string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        lock (_lock)
        {
            var declined = category == "sub" ? _state.SubDeclined : _state.AudioDeclined;
            if (!declined.Any(d => string.Equals(d, locale, StringComparison.OrdinalIgnoreCase)))
                declined.Add(locale);
            Save();
        }
    }

    public void Dismiss(string category, string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return;
        lock (_lock)
        {
            var counts = CountsFor(category);
            var snooze = category == "sub" ? _state.SubSnooze : _state.AudioSnooze;
            if (counts != null) snooze[locale] = counts.GetValueOrDefault(locale, 0);
            Save();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            var enabled = _state.Enabled; // a reset clears history but keeps the on/off choice
            _state = new LanguagePrefsState { Enabled = enabled };
            Save();
        }
    }

    private Dictionary<string, int>? CountsFor(string category) =>
        category switch { "audio" => _state.AudioCounts, "sub" => _state.SubCounts, _ => null };

    private void Load()
    {
        try
        {
            if (!string.IsNullOrEmpty(_path) && File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonConvert.DeserializeObject<LanguagePrefsState>(json);
                if (loaded != null) _state = loaded;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load language prefs; starting fresh");
            _state = new LanguagePrefsState();
        }
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            var json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            // Atomic write: temp + move so a crash/power-loss can't leave a half-written file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save language prefs");
        }
    }
}
