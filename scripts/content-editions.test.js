const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

function app(episodes) {
    const elements = Object.fromEntries(['add-edition-note', 'add-episodes-list', 'add-all-checkbox', 'add-btn', 'season-dropdown']
        .map(id => [id, { innerHTML: '', hidden: true, value: '', checked: false, disabled: true }]));
    const context = vm.createContext({
        URL, Headers, AbortController, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js'),
        document: {
            addEventListener() {}, querySelectorAll: () => [],
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'); } }),
            getElementById: id => elements[id] || null
        },
        sessionStorage: { getItem() {}, setItem() {}, removeItem() {} },
        setTimeout: () => 0, clearTimeout() {},
        fetch() { throw new Error('Edition display must not send requests'); }
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    vm.runInContext(`addDownloadSelectedSeries = {id:'SERIES'}; addDownloadSeriesData = {}; addDownloadEpisodeList = ${JSON.stringify(episodes)};`, context);
    return { elements, run: code => vm.runInContext(code, context), async select(id) {
        elements['season-dropdown'].value = id;
        await vm.runInContext(`onSeasonChange(${JSON.stringify(id)})`, context);
    } };
}

const episode = (id, edition) => ({ id, e: `${id}E1`, name: `${id} episode`, season: '1', seasonTitle: 'Same title', edition });
const verified = { label: 'Uncensored', explanation: 'Verified original label.', sourceUrl: 'https://www.crunchyroll.com/news/example' };

test('selecting an edition shows its badge, explanation, source and episode rows', async () => {
    const ui = app([episode('A', verified), episode('B', { label: 'TV broadcast', explanation: 'Original broadcast label.' })]);
    await ui.select('A');
    assert.equal(ui.elements['add-edition-note'].hidden, false);
    assert.match(ui.elements['add-edition-note'].innerHTML, /Uncensored/);
    assert.match(ui.elements['add-edition-note'].innerHTML, /Verified original label/);
    assert.match(ui.elements['add-edition-note'].innerHTML, /rel="noopener noreferrer"/);
    assert.match(ui.elements['add-episodes-list'].innerHTML, /Uncensored/);
    assert.doesNotMatch(ui.elements['add-episodes-list'].innerHTML, /B episode/);

    ui.run("selectedEpisodes.add('AE1')");
    await ui.select('B');
    assert.match(ui.elements['add-edition-note'].innerHTML, /TV broadcast/);
    assert.doesNotMatch(ui.elements['add-edition-note'].innerHTML, /Uncensored/);
    assert.match(ui.elements['add-episodes-list'].innerHTML, /B episode/);
    assert.equal(ui.run('selectedEpisodes.size'), 0);
    assert.equal(ui.elements['add-btn'].disabled, true);
});

test('unlabeled seasons clear the previous edition notice', async () => {
    const ui = app([episode('A', verified), episode('B', null)]);
    await ui.select('A');
    await ui.select('B');
    assert.equal(ui.elements['add-edition-note'].hidden, true);
    assert.equal(ui.elements['add-edition-note'].innerHTML, '');
    assert.doesNotMatch(ui.elements['add-episodes-list'].innerHTML, /edition-badge/);
});

test('unknown editions are visible without calling them censored', async () => {
    const ui = app([episode('A', { label: 'Edition unknown', explanation: 'Duplicate title without an edition label.' })]);
    await ui.select('A');
    assert.match(ui.elements['add-edition-note'].innerHTML, /edition-unknown/);
    assert.match(ui.elements['add-episodes-list'].innerHTML, /Edition unknown/);
    assert.doesNotMatch(ui.elements['add-episodes-list'].innerHTML, /Uncensored|Censored/);
});

test('edition text is escaped and unsafe source links are omitted', async () => {
    const ui = app([episode('A', { label: '<img src=x>', explanation: '\" onclick=\"alert(1)', sourceUrl: 'javascript:alert(1)' })]);
    await ui.select('A');
    const html = ui.elements['add-edition-note'].innerHTML;
    assert.match(html, /&lt;img src=x&gt;/);
    assert.doesNotMatch(html, /<img|href=|title="" onclick=/);
});
