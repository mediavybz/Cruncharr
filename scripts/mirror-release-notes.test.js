const test = require('node:test');
const assert = require('node:assert/strict');
const { mirrorReleaseNotes } = require('./mirror-release-notes.cjs');

test('mirrors paginated published notes without deleting backup-only releases or making old releases latest', async () => {
    const source = [
        { tag_name: 'v1', name: 'One', body: 'Notes', draft: false, prerelease: false },
        { tag_name: 'v2', name: 'Two', body: 'Corrected', draft: false, prerelease: true },
        { tag_name: 'draft', name: 'Private', body: 'Hidden', draft: true, prerelease: false }
    ];
    const writes = [];
    const result = await mirrorReleaseNotes({
        sourceUrl: 'https://forgejo.example', sourceRepo: 'owner/repo',
        destinationRepo: 'owner/backup', token: 'test-token',
        fetchImpl: async (url, options) => {
            const parsed = new URL(url);
            if (parsed.hostname === 'forgejo.example') assert.equal(options.headers.Authorization, undefined);
            else assert.equal(options.headers.Authorization, 'Bearer test-token');
            if (options.method !== 'GET') {
                writes.push({ method: options.method, path: parsed.pathname, body: JSON.parse(options.body) });
                return { ok: true, json: async () => ({}) };
            }
            const page = Number(parsed.searchParams.get('page'));
            const data = parsed.hostname === 'forgejo.example'
                ? (page === 1 ? source.slice(0, 1) : page === 2 ? source.slice(1) : [])
                : (page === 1 ? [{ ...source[1], id: 22, body: 'Old' }, { tag_name: 'backup-only', id: 99 }] : []);
            return { ok: true, json: async () => data };
        }
    });
    assert.deepEqual(result, { created: 1, updated: 1, unchanged: 0 });
    assert.equal(writes.length, 2);
    assert.equal(writes[0].method, 'POST');
    assert.equal(writes[0].body.make_latest, 'false');
    assert.equal(writes[1].path, '/repos/owner/backup/releases/22');
    assert.equal(writes[1].body.body, 'Corrected');
    assert.equal(writes[1].body.prerelease, true);
});

test('unchanged release notes do not cause writes', async () => {
    const release = { tag_name: 'v1', name: 'One', body: '', draft: false, prerelease: false };
    const result = await mirrorReleaseNotes({ sourceUrl: 'https://forgejo.example', sourceRepo: 'o/r',
        destinationRepo: 'o/b', token: 'test-token', fetchImpl: async (url, options) => {
            assert.equal(options.method, 'GET');
            return { ok: true, json: async () => new URL(url).searchParams.get('page') === '1' ? [release] : [] };
        } });
    assert.deepEqual(result, { created: 0, updated: 0, unchanged: 1 });
});

test('API failures fail the mirror without logging credentials or response bodies', async () => {
    await assert.rejects(mirrorReleaseNotes({ sourceUrl: 'https://forgejo.example', sourceRepo: 'o/r',
        destinationRepo: 'o/b', token: 'test-token', fetchImpl: async () => ({ ok: false, status: 503 })
    }), /^Error: GET \/api\/v1\/repos\/o\/r\/releases: HTTP 503$/);
});
