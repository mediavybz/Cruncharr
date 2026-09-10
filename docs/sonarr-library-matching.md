# Sonarr library detection (1.0.83)

Browse and Search now share verified Crunchyroll-to-Sonarr series identities with
History and download naming. A show does not have to be in Cruncharr History to
display its Sonarr badge. Multiple Crunchyroll listings can belong to one Sonarr
series, as with Senran Kagura and SENRAN KAGURA SHINOVI MASTER.

Exact primary, clean and alternate titles match after punctuation, accent and
ampersand normalization. Shortened titles and subtitles are candidates only.
Sonarr's metadata lookup checks those candidates against other TVDB shows,
including shows outside the user's library. Two distinct episode titles must
also agree before a non-exact series match is accepted. Translated episode names
can instead qualify with one long exact title and two additional episodes sharing
multiple meaningful words. Ambiguous franchises,
generic episode numbers, and repeated dub versions cannot prove identity.

Exact badges appear immediately. The Browse status explains when alternate names
are being checked. Successful metadata lookups and episode confirmations are
cached for a day; a negative episode comparison is retried after five minutes.
File availability uses the existing fresh Sonarr checks, independently of these
identity caches. Failed verification is reported and retried; it is not cached
as a missing series. Existing saved History links remain intact.

Verified against the actual library: Haganai (24/24 provider episodes with files),
We Without Wings (12/12), Senran Kagura (12/12), and SHINOVI MASTER (24/24).
The broader catalog also recovered Basilisk: The Ouka Ninja Scrolls, Corpse
Princess, MAGATSU WAHRHEIT and Sankarea. Episode confirmation rejected unrelated
concerts, movies and spin-offs returned by broad title searches.

“In Sonarr” identifies the series, not completeness of every season or language.
Open History to inspect individual episode file status. Shows with no files use
“Tracked in Sonarr.” A missing badge is not a guarantee that a show is absent:
ambiguous names or unavailable metadata remain unverified rather than being
assigned to the wrong series.
