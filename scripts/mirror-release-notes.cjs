// Git pushes do not copy release records. Keep published release notes in the
// GitHub backup too; never delete destination-only releases or uploaded assets.
const fields = ['tag_name', 'name', 'body', 'draft', 'prerelease'];

async function mirrorReleaseNotes({ sourceUrl, sourceRepo, destinationRepo, token, fetchImpl = fetch }) {
    async function request(url, method = 'GET', body) {
        const headers = { Accept: 'application/json' };
        if (new URL(url).hostname === 'api.github.com') {
            headers.Authorization = `Bearer ${token}`;
            headers['X-GitHub-Api-Version'] = '2022-11-28';
        }
        if (body) headers['Content-Type'] = 'application/json';
        const response = await fetchImpl(url, {
            method, headers, body: body ? JSON.stringify(body) : undefined,
            signal: AbortSignal.timeout(30000)
        });
        if (!response.ok) throw new Error(`${method} ${new URL(url).pathname}: HTTP ${response.status}`);
        return response.json();
    }

    async function pages(base, pageSizeKey) {
        const releases = [];
        for (let page = 1; ; page++) {
            const batch = await request(`${base}?${pageSizeKey}=50&page=${page}`);
            if (!Array.isArray(batch)) throw new Error('Expected a release list');
            if (batch.length === 0) return releases;
            releases.push(...batch);
        }
    }

    const source = await pages(`${sourceUrl.replace(/\/$/, '')}/api/v1/repos/${sourceRepo}/releases`, 'limit');
    const destinationBase = `https://api.github.com/repos/${destinationRepo}/releases`;
    const destination = await pages(destinationBase, 'per_page');
    const byTag = new Map(destination.map(release => [release.tag_name, release]));
    const counts = { created: 0, updated: 0, unchanged: 0 };
    for (const release of source.filter(release => !release.draft)) {
        const payload = Object.fromEntries(fields.map(field => [field, release[field] ?? (field === 'draft' || field === 'prerelease' ? false : '')]));
        const existing = byTag.get(release.tag_name);
        if (!existing) {
            await request(destinationBase, 'POST', { ...payload, make_latest: 'false' });
            counts.created++;
        } else if (fields.some(field => (existing[field] ?? '') !== payload[field])) {
            await request(`${destinationBase}/${existing.id}`, 'PATCH', payload);
            counts.updated++;
        } else {
            counts.unchanged++;
        }
    }
    return counts;
}

module.exports = { mirrorReleaseNotes };
if (require.main === module) {
    const { GITHUB_SERVER_URL, GITHUB_REPOSITORY, GH_TOKEN } = process.env;
    if (!GITHUB_SERVER_URL || !GITHUB_REPOSITORY || !GH_TOKEN) {
        console.error('GITHUB_SERVER_URL, GITHUB_REPOSITORY and GH_TOKEN are required');
        process.exitCode = 1;
    } else if (new URL(GITHUB_SERVER_URL).hostname !== 'github.com') {
        mirrorReleaseNotes({ sourceUrl: GITHUB_SERVER_URL, sourceRepo: GITHUB_REPOSITORY,
            destinationRepo: 'mediavybz/Cruncharr', token: GH_TOKEN })
            .then(counts => console.log('Release notes:', JSON.stringify(counts)))
            .catch(error => { console.error(error.message); process.exitCode = 1; });
    }
}
