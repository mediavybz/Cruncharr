const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const requests = require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

function app() {
    const popup = { innerHTML: '', style: {}, insertAdjacentHTML(_, html) { this.innerHTML += html; } };
    const input = { value: '' };
    const pending = [];
    const timers = new Map();
    const storage = new Map();
    let nextTimer = 0;
    const context = vm.createContext({
        URL, Headers, AbortController, console,
        CruncharrCalendarRequests: requests,
        document: {
            addEventListener() {},
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'); } }),
            getElementById: id => id === 'global-search-popup' ? popup : id === 'global-search' ? input : null
        },
        sessionStorage: {
            getItem: key => storage.get(key),
            setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        },
        setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout: id => timers.delete(id),
        fetch: (url, options) => new Promise(resolve => pending.push({ url, options, resolve }))
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    return {
        popup, input, pending, storage, timers,
        run: code => vm.runInContext(code, context),
        reply: (index, data, ok = true) => pending[index].resolve({ ok, status: ok ? 200 : 503, json: async () => data })
    };
}

test('typing shows cached matches synchronously before the network debounce', () => {
    const ui = app();
    ui.run('allBrowseSeries = [{id:"N",title:"Naruto"},{id:"S",title:"Naruto Shippuden"}]; browseCatalogLoaded = true; onGlobalSearchInput("naru");');
    assert.match(ui.popup.innerHTML, /Naruto/);
    assert.doesNotMatch(ui.popup.innerHTML, /Searching/);
    assert.equal(ui.pending.length, 0);
    assert.equal(ui.timers.size, 1);
});

test('a new query cancels and ignores the older response even before its debounce runs', async () => {
    const ui = app();
    const old = ui.run('doGlobalSearch("Naruto")');
    ui.run('onGlobalSearchInput("One Piece")');
    assert.equal(ui.pending[0].options.signal.aborted, true);
    ui.reply(0, [{ id: 'N', title: 'Naruto' }]);
    await old;
    assert.doesNotMatch(ui.popup.innerHTML, /Naruto/);
});

test('clearing or dismissing search cannot reopen it when a response arrives', async () => {
    for (const action of ['onGlobalSearchInput("")', 'closeGlobalSearch()']) {
        const ui = app();
        const old = ui.run('doGlobalSearch("Naruto")');
        ui.run(action);
        ui.reply(0, [{ id: 'N', title: 'Naruto' }]);
        await old;
        assert.equal(ui.popup.style.display, 'none');
        assert.equal(ui.popup.innerHTML, '');
        assert.equal(ui.timers.size, 0);
    }
});

test('Enter cancels the scheduled duplicate search', async () => {
    const ui = app();
    ui.input.value = 'Naruto';
    ui.run('onGlobalSearchInput("Naruto"); onGlobalSearchEnter();');
    assert.equal(ui.timers.size, 0);
    assert.equal(ui.pending.length, 1);
    ui.reply(0, []);
});

test('out-of-order search completions preserve the latest results', async () => {
    const ui = app();
    const old = ui.run('doGlobalSearch("Naruto")');
    const latest = ui.run('doGlobalSearch("One Piece")');
    ui.reply(1, [{ id: 'P', title: 'One Piece' }]);
    await latest;
    ui.reply(0, [{ id: 'N', title: 'Naruto' }]);
    await old;
    assert.match(ui.popup.innerHTML, /One Piece/);
    assert.doesNotMatch(ui.popup.innerHTML, /Naruto/);
});

test('failed remote search preserves cached matches and exposes the failure', async () => {
    const ui = app();
    ui.run('allBrowseSeries = [{id:"N",title:"Naruto"}];');
    const search = ui.run('doGlobalSearch("Naruto")');
    ui.reply(0, {}, false);
    await search;
    assert.match(ui.popup.innerHTML, /Naruto/);
    assert.match(ui.popup.innerHTML, /Live search is unavailable/);
});

test('empty catalog cache is rejected and a failed load can be retried', async () => {
    const ui = app();
    ui.storage.set('cruncharrBrowseCatalogV2', JSON.stringify({ cachedAt: Date.now(), series: [] }));
    assert.equal(ui.run('restoreBrowseCatalog()'), false);
    const first = ui.run('loadAllBrowseSeries()');
    ui.reply(0, [], true);
    await assert.rejects(first, /Catalog is empty/);
    assert.equal(ui.run('browseCatalogLoaded'), false);
    const retry = ui.run('loadAllBrowseSeries()');
    ui.reply(1, [{ id: 'N', title: 'Naruto', episodeCount: 220 }]);
    assert.equal((await retry).length, 1);
    assert.equal(ui.run('browseCatalogLoaded'), true);
});

test('concurrent catalog loads share one request', async () => {
    const ui = app();
    const first = ui.run('loadAllBrowseSeries()');
    const second = ui.run('loadAllBrowseSeries()');
    assert.equal(ui.pending.length, 1);
    ui.reply(0, [{ id: 'N', title: 'Naruto', episodeCount: 220 }]);
    await Promise.all([first, second]);
});

test('download rejection displays the server reason', async () => {
    const ui = app();
    await assert.rejects(ui.run('readQueueAdmission({ok:false,status:403,json:async()=>({message:"Log in to a Crunchyroll Premium account to download."})})'), /Premium account/);
});

test('seasonal browsing follows calendar quarters without advancing September or December', () => {
    const ui = app();
    const seasons = ['winter', 'winter', 'winter', 'spring', 'spring', 'spring', 'summer', 'summer', 'summer', 'fall', 'fall', 'fall'];
    seasons.forEach((season, month) => assert.equal(ui.run(`currentAnimeSeason(new Date(2026, ${month}, 8))`), season));
});

test('guest browsing stays quiet, but pending downloads explain the Premium requirement', async () => {
    for (const state of ['', 'Done', 'Queued']) {
        const ui = app();
        ui.run('let warnings = []; showToast = message => warnings.push(message);');
        ui.run(`queueData = [{downloadProgress:{state:${JSON.stringify(state)}}}];`);
        const check = ui.run('checkAuthStatus()');
        ui.reply(0, { isAuthenticated: false, hasPremium: false });
        await check;
        assert.equal(ui.run('warnings.length'), state === 'Queued' ? 1 : 0);
        assert.equal(ui.timers.size, 0);
    }
});
