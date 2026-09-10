using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cruncharr.Core.Services;

// A subtitle is a candidate, never proof of identity: "Code Geass" can refer to several
// different TVDB series. Resolve candidates against Sonarr's complete lookup results.
public sealed class SonarrTitleIndex
{
    private readonly Dictionary<string, List<SonarrSeries>> _exact = new();
    private readonly Dictionary<string, List<SonarrSeries>> _shortened = new();

    public SonarrTitleIndex(IEnumerable<SonarrSeries> series)
    {
        foreach (var item in series)
        foreach (var title in Titles(item))
        {
            Add(_exact, Normalize(title), item);
            var prefix = Regex.Split(title, @"\s+[-–—]\s+|:\s+")[0];
            if (prefix != title) Add(_shortened, Normalize(prefix), item);
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
    public SonarrSeries? FindExact(string title) => Exact(title) is { Count: 1 } matches ? matches[0] : null;

    public List<SonarrSeries> Candidates(string title)
    {
        var matches = new List<SonarrSeries>();
        matches.AddRange(_shortened.GetValueOrDefault(Normalize(title)) ?? []);
        var prefix = Regex.Split(title, @"\s+[-–—]\s+|:\s+")[0];
        if (prefix != title) matches.AddRange(_exact.GetValueOrDefault(Normalize(prefix)) ?? []);
        return matches.DistinctBy(Identity).ToList();
    }

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
        bool Specific(string title) => title.Length >= 5 && !Regex.IsMatch(title, @"^(?:episode|ep|chapter|part)?\d+$");
        var provider = providerTitles.Where(t => Specific(Normalize(t))).DistinctBy(Normalize).ToList();
        var sonarr = sonarrTitles.Where(t => Specific(Normalize(t))).DistinctBy(Normalize).ToList();
        var matches = provider.Select(Normalize).ToHashSet();
        matches.IntersectWith(sonarr.Select(Normalize));
        if (matches.Count >= 2) return true;
        // Translations can preserve nouns while changing almost all wording. Require one long
        // exact title AND two additional, distinct episodes sharing two meaningful words each.
        // For example: "My Favorite Animal is Pegasus" / "The Animal I Like is the Pegasus".
        if (!matches.Any(t => t.Length >= 20)) return false;
        var used = new HashSet<string>(matches);
        var supporting = 0;
        foreach (var title in provider.Where(t => !matches.Contains(Normalize(t))))
        {
            var words = EvidenceWords(title!);
            var match = sonarr.FirstOrDefault(t => !used.Contains(Normalize(t)) && words.Intersect(EvidenceWords(t!)).Count() >= 2);
            if (match == null) continue;
            used.Add(Normalize(match));
            if (++supporting >= 2) return true;
        }
        return false;
    }

    private static readonly HashSet<string> CommonWords = new("episode chapter part special this that with from your have will here there what when them they always first last final story season about".Split(' '));
    private static HashSet<string> EvidenceWords(string title) => Regex.Matches(title.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => Normalize(m.Value)).Where(w => w.Length >= 4 && !CommonWords.Contains(w)).ToHashSet();
}
