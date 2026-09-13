# Sonarr library detection (1.0.86)

Browse and Search now share verified Crunchyroll-to-Sonarr series identities with
History and download naming. A show does not have to be in Cruncharr History to
display its Sonarr badge. Multiple Crunchyroll listings can belong to one Sonarr
series, as with Senran Kagura and SENRAN KAGURA SHINOVI MASTER.

Exact primary, clean and alternate titles match after punctuation, accent and
ampersand normalization. Shared subtitles, meaningful title words, compound
franchise names and omitted year suffixes select candidates for verification.
Sonarr's metadata lookup checks candidates against other TVDB shows, including
shows outside the user's library. An unsuffixed title cannot choose between
multiple year-qualified editions without episode evidence. Two distinct episode
titles must agree before a non-exact series match is accepted. Translated episode names
can instead qualify with one long exact title and two additional episodes sharing
multiple meaningful words. Ambiguous franchises,
generic episode numbers, and repeated dub versions cannot prove identity. Arc
labels prepended by TVDB are ignored during this series comparison.

Exact badges and saved verified identities appear in the first library response.
Alternate-name verification runs once in the background for all browsers, with
progress available without waiting for the entire catalog. Closing a page does
not cancel that work. Verified identities are saved beside the configuration in
`sonarr-library-identities.json` for up to a day and survive restarts. Changing the
Sonarr connection, series identities or aliases invalidates the saved matches.
File availability still uses fresh Sonarr data independently of identity caching.

While **Hide series in Sonarr** is on, candidates awaiting verification are also
excluded; Browse shows the number still unverified. A rejected candidate becomes
visible again. Failed checks remain marked as unverified and retry after five
minutes. A failed library refresh keeps known matches instead of resetting the
filter. Turn the filter off to inspect unverified listings. History entries are
not required for these badges or filters.

The September 12 filter audit found 21 verified alternate-name listings missing
from the former initial response, including We Without Wings, Haganai, Witchblade
and WorldEnd. This was a loading and cache problem after series identification;
the fix applies to every verified identity rather than adding title exceptions.

UI scripts and styles now revalidate on page load. Earlier builds cached these
files for a week, so an updated server could still run an older filter in the
browser. The 1.0.86 asset URLs also bypass those existing cached copies.

Verified against the actual library: Haganai (24/24 provider episodes with files),
We Without Wings (12/12), Senran Kagura (12/12), and SHINOVI MASTER (24/24).
The broader catalog also recovered Basilisk: The Ouka Ninja Scrolls, Corpse
Princess, MAGATSU WAHRHEIT and Sankarea. Episode confirmation rejected unrelated
concerts, movies and spin-offs returned by broad title searches.

“In Sonarr” identifies the series, not completeness of every season or language.
Open History to inspect individual episode file status. Shows with no files use
“Tracked in Sonarr.” A missing badge is not a guarantee that a show is absent:
ambiguous names or unavailable metadata remain unverified rather than being
assigned to the wrong series. The library API's `matchingFailures` field identifies
individual titles whose metadata could not be verified.

## Episode mapping

A missing Crunchyroll season number no longer makes a regular season a Sonarr
special. Neither does a word such as "Special" inside a series title. Explicit
special seasons and episode flags still map to Sonarr season zero.

Several matching titles can establish the numbering of a provider season. Recaps
that reuse a regular episode's number still need title or overview evidence.
Translated titles can corroborate a numeric fallback when at least two distinct
episode numbers agree with their metadata. A matching number alone cannot assign
a remastered cut to a different regular season or turn an HD episode into a movie.
Weak saved mappings are reconsidered when History refreshes.

Separate cuts or dub listings may reuse an established episode identity when their
numbers and episode evidence agree. This also works when the two Crunchyroll
editions share a title but Sonarr uses a different translation. Entries within the
same provider season retain separate episode identities.

After updating the testing image, refresh affected History series to apply these
rules. **Match Episodes** explicitly rematches all episodes in the selected series.
Browse and Search verify their badges without requiring a History entry.

## Catalog audit, September 12, 2026

The audit compared all 1,978 regional catalog listings with 853 Sonarr series and
their aliases. It inspected episode metadata for all 682 listings with candidate
series matches, using isolated History files. The live queue and History were not
changed. It also reviewed unmatched titles for aliases that the candidate index
had missed.

The final pass resolved 608 listings and 15,677 of their 15,936 provider episode
entries. These counts include separate provider editions of the same episode.
All resolved listings selected the same Sonarr identity in Browse and History.
Seventy other candidate listings remained unverified, and four returned no episode
metadata. An unverified candidate may be an unrelated spin-off or promotional listing.

WorldEnd's shared subtitle and Witchblade's year-qualified anime edition now
resolve through the general matcher. Both have complete file coverage in the
audited library: WorldEnd 12/12 and Witchblade 24/24. Haganai, We Without Wings and
both Senran Kagura listings remain regression checks. Additional probes cover
missing season numbers, misleading special-season names, repeated recap numbers,
alternate editions, and Wistoria and Slime's season-zero entries.

Catalog presence does not guarantee usable episode metadata. Accel World, Endride,
The Law of Ueki and Fairy Tail Podcast returned no episodes during this audit.
Some promotional videos, combined episodes, re-edited seasons and translated OVAs
also lack enough evidence for an individual Sonarr match. Those entries remain
unverified; a series badge does not assert that every cut, extra or language is
already downloaded.
