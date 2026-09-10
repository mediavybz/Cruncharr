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
        var matches = providerTitles.Select(Normalize).Where(Specific).ToHashSet();
        matches.IntersectWith(sonarrTitles.Select(Normalize).Where(Specific));
        return matches.Count >= 2;
    }
}
