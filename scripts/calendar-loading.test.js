const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

function app() {
    const label = { dataset: { seriesId: 'SERIES1' }, textContent: 'Season 1' };
    const grid = { innerHTML: '', querySelectorAll: () => [label] };
    const pending = [];
    const context = vm.createContext({
        URL, Headers, AbortController, AbortSignal, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js'),
        document: {
            addEventListener() {}, querySelectorAll: () => [],
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }),
            getElementById: id => id === 'calendar-grid' ? grid : null
        },
        sessionStorage: { getItem() {}, setItem() {}, removeItem() {} },
        localStorage: { getItem() {}, setItem() {}, removeItem() {} },
        setTimeout: () => 0, clearTimeout() {}, clearInterval() {},
        fetch: (url, options) => new Promise((resolve, reject) => {
            options.signal.addEventListener('abort', () => reject(options.signal.reason));
            pending.push({ url, options, resolve, reject });
        })
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    return {
        grid, label, pending, run: code => vm.runInContext(code, context),
        reply: (index, data) => pending[index].resolve({ ok: true, json: async () => data })
    };
}

const week = { days: [{ date: '2026-10-21', dayName: 'Wednesday', episodes: [{
    seriesId: 'SERIES1', seriesTitle: 'Season 1', episodeNumber: '1', isUpcoming: true
}] }] };

test('calendar cards render before a slow optional series-title lookup', async () => {
    const ui = app();
    const loading = ui.run('fetchCalendar()');
    ui.reply(0, week);
    await new Promise(resolve => setImmediate(resolve));
    assert.match(ui.grid.innerHTML, /Wednesday/);
    assert.match(ui.grid.innerHTML, /Season 1/);
    assert.equal(ui.pending.length, 2);
    ui.reply(1, { list: [{ seriesTitle: 'Example show' }] });
    await loading;
    assert.equal(ui.label.textContent, 'Example show');
});

test('leaving calendar aborts optional lookups without repainting the tab', async () => {
    const ui = app();
    const loading = ui.run('fetchCalendar()');
    ui.reply(0, week);
    await new Promise(resolve => setImmediate(resolve));
    ui.run('calendarRequestGate.cancel()');
    await loading;
    assert.equal(ui.pending[1].options.signal.aborted, true);
    assert.doesNotMatch(ui.grid.innerHTML, /Failed to load/);
    assert.equal(ui.label.textContent, 'Season 1');
});

test('a calendar timeout displays a retry instead of an indefinite spinner', async () => {
    const ui = app();
    ui.grid.innerHTML = 'Loading...';
    const loading = ui.run('fetchCalendar()');
    ui.pending[0].reject(new DOMException('Timed out', 'TimeoutError'));
    await loading;
    assert.match(ui.grid.innerHTML, /Failed to load calendar/);
    assert.match(ui.grid.innerHTML, /Retry/);
    assert.doesNotMatch(ui.grid.innerHTML, /Loading/);
});
