# Edition labels

Add Download shows edition badges in the season picker, the selected season's
explanation, and episode rows. Season numbers distinguish otherwise identical
options; they are not interpreted as edition indicators. Labels do not alter
episode IDs, queue selection, filenames, or Sonarr matching.

`ContentEditions` reads explicit edition suffixes from the season and episode
season titles. Supported labels are Uncensored, Censored, Uncut, Edited,
TV broadcast, Director's cut, and Home video. Uncut, broadcast, and home-video
labels are preserved as supplied; they do not imply a particular censorship
level. Plot descriptions, maturity ratings, audio languages, and durations are
not evidence of an edition.

Repeated season titles without a reliable label show **Edition unknown**. This
does not assert that the listings contain the same story or different edits.
Conflicting title labels also show Edition unknown. Single unlabeled listings
have no badge, rather than claiming they are censored or uncensored.

## Verified catalog corrections

Crunchyroll sometimes removes edition labels from its current metadata. These
corrections are scoped to exact series/season IDs, not title similarity, order,
or season numbers. Current explicit title labels take precedence. Every new
correction needs primary-source evidence and a regression test.

| Series ID | Season ID | Label |
| --- | --- | --- |
| GRVD1PE0R | G63K98G46 | Uncensored |
| GRVD1PE0R | GRVD1PG4R | TV broadcast |

For Nobunaga teacher's young bride, Crunchyroll's
[April 5, 2019 announcement](https://www.crunchyroll.com/news/latest/2019/4/5/crunchyroll-streams-midnight-occult-civil-servants-tv-anime-and-more)
lists TV Broadcast and Uncensored editions. Its original calendar for
[April 1–7, 2019](https://www.crunchyroll.com/th/simulcastcalendar?date=2019-04-01&filter=premium)
retains the Uncensored label. The indexed title of episode G64PQV5VR also retains
that label; the live catalog places that episode in season G63K98G46. The
remaining parallel 12-episode season is GRVD1PG4R. Checked October 23, 2026.
The broadcast label deliberately follows the announcement rather than assuming
the exact content that was edited.

No extra network requests or scraping are added when loading a series. Where
Crunchyroll supplies neither an edition label nor verifiable historical
evidence, automatic classification remains unavailable.
