const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const requests = require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

// Audit F07: an older seasonal response (or error) used to overwrite the newer selection.
function app() {
    const content = { innerHTML: '' };
    const pending = [];
    const context = vm.createContext({
        URL, Headers, AbortController, AbortSignal, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: requests,
        document: {
            addEventListener() {},
            querySelectorAll() { return []; },
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'); } }),
            getElementById: id => id === 'seasonal-content' ? content : null
        },
        sessionStorage: { getItem() {}, setItem() {}, removeItem() {} },
        localStorage: { getItem() {}, setItem() {}, removeItem() {} },
        setTimeout: () => 0,
        clearTimeout() {},
        clearInterval() {},
        fetch: (url, options) => new Promise((resolve, reject) => {
            options?.signal?.addEventListener('abort', () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })));
            pending.push({ url, options, resolve, reject });
        })
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    return {
        content, pending,
        run: code => vm.runInContext(code, context),
        reply: (index, title, ok = true) => pending[index].resolve({ ok, status: ok ? 200 : 503, json: async () => [{ title }] })
    };
}

function select(ui, season, year = 2026) {
    return ui.run(`seasonalSeason = '${season}'; seasonalYear = ${year}; fetchSeasonalSeries()`);
}

test('an older season finishing last does not replace the newer selection', async () => {
    const ui = app();
    const winter = select(ui, 'winter');
    const summer = select(ui, 'summer');
    assert.equal(ui.pending[0].options.signal.aborted, true);

    ui.reply(1, 'Summer');
    await summer;
    ui.reply(0, 'Winter');
    await winter;

    assert.match(ui.content.innerHTML, /Summer/);
    assert.doesNotMatch(ui.content.innerHTML, /Winter/);
});

test('an older failure does not replace newer results', async () => {
    const ui = app();
    const winter = select(ui, 'winter');
    const summer = select(ui, 'summer');

    ui.reply(1, 'Summer');
    await summer;
    ui.reply(0, 'Winter', false);
    await winter;

    assert.match(ui.content.innerHTML, /Summer/);
    assert.doesNotMatch(ui.content.innerHTML, /Failed to load/);
});

test('rapid year changes render only the last year', async () => {
    const ui = app();
    const calls = [2024, 2025, 2026].map(year => select(ui, 'fall', year));
    assert.match(ui.pending[2].url, /year=2026/);

    for (const [index, title] of [[2, 'Fall 2026'], [0, 'Fall 2024'], [1, 'Fall 2025']]) {
        ui.reply(index, title);
        await calls[index];
    }

    assert.match(ui.content.innerHTML, /Fall 2026/);
    assert.doesNotMatch(ui.content.innerHTML, /Fall 2024|Fall 2025/);
});

test('leaving the seasonal page cancels its request', async () => {
    const ui = app();
    ui.run(`currentPage = 'seasonal'`);
    const winter = select(ui, 'winter');
    try { ui.run(`loadPage('downloads')`); } catch { /* the rest of the page swap needs a real DOM */ }

    assert.equal(ui.pending[0].options.signal.aborted, true);
    ui.reply(0, 'Winter');
    await winter;
    assert.doesNotMatch(ui.content.innerHTML, /Winter|Failed to load/);
});
