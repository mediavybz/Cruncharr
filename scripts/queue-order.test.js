const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const requests = require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

// Selected episodes are grouped by dub choice before they are queued, so they used to be posted in
// grouping/selection order and downloads started out of release order.
function app(multiDubReply) {
    const posted = [];
    const element = { innerHTML: '', style: {}, classList: { add() {}, remove() {}, toggle() {} }, appendChild() {}, remove() {} };
    const context = vm.createContext({
        URL, Headers, AbortController, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: requests,
        document: {
            addEventListener() {},
            querySelectorAll() { return []; },
            createElement: () => ({ ...element, textContent: '' }),
            getElementById: () => element
        },
        sessionStorage: { getItem() {}, setItem() {}, removeItem() {} },
        localStorage: { getItem() {}, setItem() {}, removeItem() {} },
        setTimeout: () => 0,
        clearTimeout() {},
        fetch: async (url, options) => {
            if (url === '/api/v1/series/item-select-multi-dub') return { ok: true, json: async () => multiDubReply };
            if (url === '/api/v1/queue') {
                posted.push(JSON.parse(options.body).episodeId);
                return { ok: true, status: 200, clone() { return this; }, json: async () => ({ added: true }) };
            }
            return { ok: true, json: async () => ({}) };
        }
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    return { posted, run: code => vm.runInContext(code, context) };
}

const meta = (episodeId, season, episodeNumber) => ({ episodeId, season, episodeNumber, selectedDubs: ['ja-JP'] });

test('selected episodes are queued in release order', async () => {
    const ui = app({
        k5: meta('S1E5', '1', '5'),
        k1: meta('S1E1', '1', '1'),
        sp: meta('S1SP', '1', 'SP'),
        k10: meta('S1E10', '1', '10'),
        s2: meta('S2E1', '2', '1'),
        k2: meta('S1E2', '1', '2'),
        k24: meta('S1E24.5', '1', '24.5')
    });
    ui.run(`addDownloadSeriesData = { k1:{}, k2:{}, k5:{}, k10:{}, sp:{}, s2:{}, k24:{} };
            ['s2','k10','k5','sp','k1','k2','k24'].forEach(key => selectedEpisodes.add(key));`);

    await ui.run('addSelectedToQueue()');

    assert.deepEqual(ui.posted, ['S1E1', 'S1E2', 'S1E5', 'S1E10', 'S1E24.5', 'S1SP', 'S2E1']);
});

test('release order puts specials after numbered episodes and keeps ties stable', () => {
    const ui = app({});
    const order = ui.run(`[
        { id: 'a', season: '1', episodeNumber: 'SP' },
        { id: 'b', season: '1', episodeNumber: '2' },
        { id: 'c', season: '1', episodeNumber: '' },
        { id: 'd', season: '1', episodeNumber: '1' }
    ].map((item, index) => ({ item, index }))
     .sort((x, y) => compareReleaseOrder(x.item, y.item) || x.index - y.index)
     .map(x => x.item.id).join(',')`);
    assert.equal(order, 'd,b,a,c');
});
