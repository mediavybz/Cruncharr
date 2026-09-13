using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cruncharr.Core.Services;

// Similar names only select candidates. Episode evidence must resolve editions and spin-offs.
public sealed class SonarrTitleIndex
{
    private readonly Dictionary<string, List<SonarrSeries>> _exact = new();
    private readonly Dictionary<string, List<SonarrSeries>> _shortened = new();
    private readonly Dictionary<string, List<SonarrSeries>> _editions = new();
    private readonly Dictionary<string, List<(SonarrSeries Series, HashSet<string> Words)>> _words = new();

    public SonarrTitleIndex(IEnumerable<SonarrSeries> series)
    {
        foreach (var item in series)
        foreach (var title in Titles(item))
        {
            Add(_exact, Normalize(title), item);
            Add(_editions, Normalize(WithoutYear(title)), item);
            foreach (var key in CandidateKeys(title)) Add(_shortened, key, item);
            var words = TitleWords(title);
            foreach (var word in words)
            {
                if (!_words.TryGetValue(word, out var entries)) _words[word] = entries = [];
                entries.Add((item, words));
            }
        }
    }

    public static IEnumerable<string> Titles(SonarrSeries item) =>
        new[] { item.Title, item.CleanTitle }.Concat(item.AlternateTitles?.Select(a => a.Title) ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!);

    public static string Normalize(string? title) => new((title ?? "").Replace("&", "and")
        .Normalize(NormalizationForm.FormKD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Identity(SonarrSeries item) => item.TvdbId > 0 ? $"tvdb:{item.TvdbId}" : $"sonarr:{item.Id}";
    private static void Add(Dictionary<string, List<SonarrSeries>> index, string key, SonarrSeries item)
    {
        if (key.Length == 0) return;
        if (!index.TryGetValue(key, out var values)) index[key] = values = [];
        if (values.All(v => Identity(v) != Identity(item))) values.Add(item);
    }

    public IReadOnlyList<SonarrSeries> Exact(string title) => _exact.GetValueOrDefault(Normalize(title)) ?? [];
    public SonarrSeries? FindExact(string title) => Exact(title) is { Count: 1 } matches &&
        (_editions.GetValueOrDefault(Normalize(title))?.Count ?? 0) <= 1 ? matches[0] : null;

    public List<SonarrSeries> Candidates(string title)
    {
        var matches = Exact(title).ToList();
        foreach (var key in CandidateKeys(title))
            matches.AddRange(_shortened.GetValueOrDefault(key) ?? []);
        var words = TitleWords(title);
        foreach (var entry in words.SelectMany(word => _words.GetValueOrDefault(word) ?? []))
        {
            var shared = words.Intersect(entry.Words).Count();
            if (shared >= 2 && 2.0 * shared / (words.Count + entry.Words.Count) >= 0.7)
                matches.Add(entry.Series);
        }
        // Compound franchise names can omit a scene alias (e.g. a Monogatari chapter).
        foreach (var word in words.Where(word => word.Length >= 8))
        foreach (var pair in _words.Where(pair => pair.Key.Length >= 8 && pair.Key != word &&
                     (word.EndsWith(pair.Key, StringComparison.Ordinal) || pair.Key.EndsWith(word, StringComparison.Ordinal)) &&
                     (double)Math.Min(word.Length, pair.Key.Length) / Math.Max(word.Length, pair.Key.Length) >= 0.65))
            matches.AddRange(pair.Value.Select(entry => entry.Series));
        return matches.DistinctBy(Identity).ToList();
    }

    private static IEnumerable<string> CandidateKeys(string title)
    {
        // Keep years in exact identities. Removing one here only permits an episode comparison.
        var withoutYear = WithoutYear(title);
        yield return Normalize(withoutYear);
        var prefix = Regex.Split(withoutYear, @"\s+[-–—]\s+|:\s+")[0];
        if (prefix != withoutYear && Normalize(prefix).Length >= 4) yield return Normalize(prefix);
    }

    private static string WithoutYear(string title) => Regex.Replace(title, @"\s*\((?:19|20)\d{2}\)\s*$", "");
    public static bool IsYearQualifiedEdition(string title, SonarrSeries candidate) => Titles(candidate)
        .Any(alias => WithoutYear(alias) != alias && Normalize(WithoutYear(alias)) == Normalize(title));

    private static HashSet<string> TitleWords(string title) => Regex.Matches(title.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => Normalize(m.Value)).Where(w => w.Length >= 3 && !CommonWords.Contains(w)).ToHashSet();

    public static SonarrSeries? ResolveLookup(string title, List<SonarrSeries> library, List<SonarrSeries> lookup)
    {
        // Lookup includes series NOT owned by this user. An exact spin-off/remake takes priority
        // over a similar owned parent. Enrich owned entries with Sonarr's scene aliases.
        var enriched = lookup.Where(s => s.TvdbId > 0).Select(s => new SonarrSeries
        {
            TvdbId = s.TvdbId, Title = s.Title, CleanTitle = s.CleanTitle,
            AlternateTitles = (s.AlternateTitles ?? []).Concat(library.Where(l => l.TvdbId == s.TvdbId)
                .SelectMany(l => l.AlternateTitles ?? [])).ToList()
        }).ToList();
        var index = new SonarrTitleIndex(enriched);
        var matches = index.Exact(title);
        if (matches.Count == 0) matches = index.Candidates(title);
        if (matches.Count != 1) return null;
        return library.SingleOrDefault(s => s.TvdbId == matches[0].TvdbId);
    }

    public static bool EpisodesConfirmIdentity(IEnumerable<string?> providerTitles, IEnumerable<string?> sonarrTitles)
    {
        // TVDB may prepend the arc and part to every episode title.
        string NormalizeEpisode(string? title) => Normalize(Regex.Replace(title ?? "",
            @"^.*?\b(?:arc|chapter)\s+[ivxlcdm\d]+:\s*", "", RegexOptions.IgnoreCase));
        bool Specific(string title) => title.Length >= 5 && !Regex.IsMatch(title, @"^(?:episode|ep|chapter|part)?\d+$");
        var provider = providerTitles.Where(t => Specific(NormalizeEpisode(t))).DistinctBy(NormalizeEpisode).ToList();
        var sonarr = sonarrTitles.Where(t => Specific(NormalizeEpisode(t))).DistinctBy(NormalizeEpisode).ToList();
        var matches = provider.Select(NormalizeEpisode).ToHashSet();
        matches.IntersectWith(sonarr.Select(NormalizeEpisode));
        if (matches.Count >= 2) return true;
        // Translations can preserve nouns while changing almost all wording. Require one long
        // exact title AND two additional, distinct episodes sharing two meaningful words each.
        // For example: "My Favorite Animal is Pegasus" / "The Animal I Like is the Pegasus".
        if (!matches.Any(t => t.Length >= 20)) return false;
        var used = new HashSet<string>(matches);
        var supporting = 0;
        foreach (var title in provider.Where(t => !matches.Contains(NormalizeEpisode(t))))
        {
            var words = EvidenceWords(title!);
            var match = sonarr.FirstOrDefault(t => !used.Contains(NormalizeEpisode(t)) && words.Intersect(EvidenceWords(t!)).Count() >= 2);
            if (match == null) continue;
            used.Add(NormalizeEpisode(match));
            if (++supporting >= 2) return true;
        }
        return false;
    }

    private static readonly HashSet<string> CommonWords = new("episode chapter part special this that with from your have will here there what when them they always first last final story season about".Split(' '));
    private static HashSet<string> EvidenceWords(string title) => Regex.Matches(title.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => Normalize(m.Value)).Where(w => w.Length >= 4 && !CommonWords.Contains(w)).ToHashSet();
}
