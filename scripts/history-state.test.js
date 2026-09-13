const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

function app() {
    const pending = [];
    const elements = new Map(['history-content', 'history-status', 'modal-title', 'modal-body', 'modal-footer', 'modal'].map(id => [id, {
        innerHTML: '', textContent: '', classList: { active: false, add() { this.active = true; }, remove() { this.active = false; }, contains() { return this.active; } }
    }]));
    const context = vm.createContext({
        URL, Headers, AbortController, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js'),
        document: {
            addEventListener() {}, getElementById: id => elements.get(id),
            createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } })
        },
        fetch: (url, options) => new Promise(resolve => pending.push({ url, options, resolve }))
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    vm.runInContext(`let renders = 0; let detail = ''; renderHistoryContent = () => { renders++; }; renderHistorySeriesDetailContent = series => { detail = series.seriesId; }; maybeRefreshHistoryCoverArt = () => {}; maybeAutoMatchSonarr = () => {};`, context);
    return {
        elements, pending, run: code => vm.runInContext(code, context),
        reply: (index, data, ok = true) => pending[index].resolve({ ok, status: ok ? 200 : 503, json: async () => data })
    };
}

test('history polling shares an in-flight request and does not repaint unchanged results', async () => {
    const ui = app();
    const first = ui.run('fetchHistoryData()');
    const second = ui.run('fetchHistoryData()');
    assert.equal(first, second);
    assert.equal(ui.pending.length, 1);
    ui.reply(0, [{ seriesId: 'A' }]);
    await first;
    const poll = ui.run('fetchHistoryData()');
    ui.reply(1, [{ seriesId: 'A' }]);
    await poll;
    assert.equal(ui.run('renders'), 1);
});

test('a failed refresh preserves loaded history and shows a recoverable error', async () => {
    const ui = app();
    const first = ui.run('fetchHistoryData()');
    ui.reply(0, [{ seriesId: 'A' }]);
    await first;
    const poll = ui.run('fetchHistoryData()');
    ui.reply(1, null, false);
    await poll;
    assert.equal(ui.run('historyData[0].seriesId'), 'A');
    assert.match(ui.elements.get('history-status').textContent, /refresh failed/);
});

test('manual refresh supersedes a slow poll without allowing stale results to overwrite it', async () => {
    const ui = app();
    const poll = ui.run('fetchHistoryData()');
    const refresh = ui.run('fetchHistoryData(true)');
    assert.equal(ui.pending[0].options.signal.aborted, true);
    assert.match(ui.pending[1].url, /forceRefresh=true/);
    ui.reply(1, [{ seriesId: 'CURRENT' }]);
    await refresh;
    ui.reply(0, [{ seriesId: 'OLD' }]);
    await poll;
    assert.equal(ui.run('historyData[0].seriesId'), 'CURRENT');
});

test('series detail paints cached data immediately and ignores an older series response', async () => {
    const ui = app();
    ui.run('historyData = [{seriesId:"A",seriesTitle:"First"},{seriesId:"B",seriesTitle:"Second"}];');
    const first = ui.run('showHistorySeriesDetail("A")');
    assert.equal(ui.run('detail'), 'A');
    assert.equal(ui.pending[0].url, '/api/v1/history/series/A');
    const second = ui.run('showHistorySeriesDetail("B")');
    ui.reply(1, { seriesId: 'B', seriesTitle: 'Second', seasons: [] });
    await second;
    ui.reply(0, { seriesId: 'A', seriesTitle: 'First', seasons: [] });
    await first;
    assert.equal(ui.run('detail'), 'B');
    assert.equal(ui.elements.get('modal-title').textContent, 'Second');
});

test('Sonarr counts remain visible when excluded from progress and outages are not called missing files', () => {
    const ui = app();
    ui.run('config = {history:{countSonarr:false}}; const episode = {hasLocalArtifact:false,sonarrHasFile:true}; const series = {sonarrSeriesId:"10",seasons:[{episodes:[episode]}]};');
    assert.equal(ui.run('seriesHaveCount(series)'), 0);
    assert.equal(ui.run('historySonarrSummary(series)'), '1 in Sonarr');
    ui.run('series.sonarrStatusUnavailable = true');
    assert.equal(ui.run('historySonarrSummary(series)'), 'Sonarr status unavailable');
    assert.match(ui.run('getEpisodeStatusTooltip(episode, series)'), /temporarily unavailable/);
});

test('override dialogs restore saved selections and expose default inheritance', () => {
    const ui = app();
    const html = ui.run('historyOverrideForm("override-",{videoQuality:"720",dubLanguages:["en-US"],softSubs:["fr-FR","all"]})');
    assert.match(html, /value="720" selected/);
    assert.match(html, /value="en-US" selected/);
    assert.match(html, /value="fr-FR" selected/);
    assert.match(html, /value="all" selected/);
    assert.match(ui.run('historyOverrideForm("override-season-",{})'), /value="" selected/);
});

test('History queue defaults use season overrides before series overrides', () => {
    const ui = app();
    ui.run('historyData=[{settingsOverride:{dubLanguages:["en-US"],softSubs:["fr-FR"]},seasons:[{settingsOverride:{dubLanguages:["de-DE"]},episodes:[{episodeId:"EP"}]}]}]');
    assert.equal(ui.run('historyQueueOverrides("EP").selectedDubs[0]'), 'de-DE');
    assert.equal(ui.run('historyQueueOverrides("EP").selectedSubs[0]'), 'fr-FR');
    ui.run('historyData[0].seasons[0].settingsOverride.dubLanguages=[]');
    assert.equal(ui.run('historyQueueOverrides("EP").selectedDubs[0]'), 'en-US');
});
