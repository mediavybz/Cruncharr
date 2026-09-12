const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');
function app(replies, choice) {
    const calls = [];
    const context = vm.createContext({
        Map, JSON, config: { sonarr: { enabled: true } }, authStatus: {}, sonarrLibraryCheckedAt: 1,
        fetch: async (url, options) => { calls.push({url, ...options}); return replies.shift(); },
        chooseSonarrSeries: async () => choice, loadSonarrLibrary() {},
    });
    const start = source.indexOf('        async function sendDownloadRequest(');
    const end = source.indexOf('        let currentPage', start);
    vm.runInContext('const sonarrChoices = new Map();' + source.slice(start, end), context);
    return { calls, run: code => vm.runInContext(code, context) };
}
const reply = (data, status = 200) => ({ ok: status < 300, status, json: async () => data, clone() { return this; } });
const conflict = () => reply({ seriesId: 'CR1', candidates: [{tvdbId: 123, title: 'Example (2006)'}] }, 409);
test('ambiguous Sonarr requests retry only with the user-selected TVDB edition', async () => {
    const ui = app([conflict(), reply({added:true, destination:'sonarr'})], 123);
    const response = await ui.run(`sendDownloadRequest({method:'POST',body:JSON.stringify({episodeId:'EP1'})})`);
    assert.equal(response.ok, true);
    assert.deepEqual(JSON.parse(ui.calls[1].body), {episodeId:'EP1',sonarrTvdbId:123});
});
test('cancelling a Sonarr choice never submits another request', async () => {
    const ui = app([conflict()], null);
    await assert.rejects(ui.run(`sendDownloadRequest({method:'POST',body:'{}'})`), /cancelled/);
    assert.equal(ui.calls.length, 1);
});
test('Sonarr errors reach the caller and guest requests get the correct action label', async () => {
    const ui = app([]);
    assert.equal(ui.run('downloadActionLabel()'), 'Request in Sonarr');
    assert.equal(ui.run('authStatus={isAuthenticated:true,hasPremium:true}; downloadActionLabel()'), 'Download with Cruncharr');
    const failure = app([reply({message:'Choose a Sonarr root folder'},500)]);
    await assert.rejects(failure.run(`sendDownloadRequest({method:'POST',body:'{}'}).then(readQueueAdmission)`), /Choose a Sonarr root folder/);
});
