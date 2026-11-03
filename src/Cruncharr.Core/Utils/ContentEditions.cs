using System.Text.RegularExpressions;
using Cruncharr.Core.Models;

namespace Cruncharr.Core.Utils;

public static class ContentEditions
{
    private static readonly Regex EditionSuffix = new(
        @"(?:\(|\[|\s[-–—:]\s)\s*(uncensored|censored|uncut|edited|(?:tv\s+)?broadcast|director['’]s\s+cut|home\s+video)(?:\s+version)?\s*[)\]]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // IDs and primary-source evidence are documented in docs/content-editions.md.
    private const string NobunagaAnnouncement = "https://www.crunchyroll.com/news/latest/2019/4/5/crunchyroll-streams-midnight-occult-civil-servants-tv-anime-and-more";
    private static readonly Dictionary<(string SeriesId, string SeasonId), ContentEdition> VerifiedEditions = new()
    {
        [("GRVD1PE0R", "G63K98G46")] = new("Uncensored",
            "Crunchyroll's original release listing identifies this as the uncensored edition. The current catalog omits that label.", NobunagaAnnouncement),
        [("GRVD1PE0R", "GRVD1PG4R")] = new("TV broadcast",
            "Crunchyroll's original release announcement identifies the other edition as the TV broadcast version. The current catalog omits that label.", NobunagaAnnouncement)
    };

    public static void Apply(string seriesId, IReadOnlyList<SeasonInfo> seasons, IReadOnlyList<EpisodeDisplay> episodes)
    {
        var seasonTitles = seasons.GroupBy(season => season.Id)
            .ToDictionary(group => group.Key, group => group.First().Title);
        var listings = episodes.Where(episode => !string.IsNullOrEmpty(episode.Id)).GroupBy(episode => episode.Id).ToList();
        var repeatedTitles = listings.GroupBy(group => group.First().SeasonTitle.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in listings)
        {
            var titles = listing.Select(episode => episode.SeasonTitle).Prepend(seasonTitles.GetValueOrDefault(listing.Key));
            var labels = titles.Select(FromTitle).Where(label => label != null).Distinct().ToList();
            ContentEdition? edition = null;
            if (labels.Count == 1)
            {
                edition = new(labels[0]!, "Edition label supplied in Crunchyroll's season title. Broadcast and home-video labels do not by themselves establish censorship status.");
            }
            else if (labels.Count > 1)
            {
                edition = new("Edition unknown", "Crunchyroll supplies conflicting edition labels for this listing.");
            }
            else if (VerifiedEditions.TryGetValue((seriesId, listing.Key), out var verified))
            {
                edition = verified;
            }
            else if (repeatedTitles.Contains(listing.First().SeasonTitle.Trim()))
            {
                edition = new("Edition unknown", "Crunchyroll uses this same title for multiple listings without specifying an edition. Matching titles or season numbers do not establish whether either version is edited.");
            }

            foreach (var episode in listing) episode.Edition = edition;
        }
    }

    private static string? FromTitle(string? title)
    {
        var match = EditionSuffix.Match(DownloadQueueItemFactory.StripDubSuffix(title));
        if (!match.Success) return null;
        var label = Regex.Replace(match.Groups[1].Value.ToLowerInvariant(), @"\s+", " ");
        return label switch
        {
            "uncensored" => "Uncensored",
            "censored" => "Censored",
            "uncut" => "Uncut",
            "edited" => "Edited",
            "broadcast" or "tv broadcast" => "TV broadcast",
            "director's cut" or "director’s cut" => "Director's cut",
            "home video" => "Home video",
            _ => null
        };
    }
}
