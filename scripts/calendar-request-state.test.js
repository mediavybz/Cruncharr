const test = require('node:test');
const assert = require('node:assert/strict');
const {
    createLatestRequestGate,
    normalizeDubFilter
} = require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js');

test('new calendar request cancels and supersedes the initial all-language request', () => {
    const gate = createLatestRequestGate();
    const allLanguages = gate.begin();
    const english = gate.begin();
    let rendered = '';

    assert.equal(allLanguages.signal.aborted, true);
    if (gate.isCurrent(english)) rendered = 'en-US';
    if (gate.isCurrent(allLanguages)) rendered = 'none';

    assert.equal(rendered, 'en-US');
    assert.equal(gate.isCurrent(allLanguages), false);
    assert.equal(gate.isCurrent(english), true);
});

test('leaving calendar invalidates the active request', () => {
    const gate = createLatestRequestGate();
    const request = gate.begin();

    gate.cancel();

    assert.equal(request.signal.aborted, true);
    assert.equal(gate.isCurrent(request), false);
});

test('saved dub filter is normalized case-insensitively and legacy filters fall back', () => {
    const options = [{ value: 'ja-JP' }, { value: 'en-US' }];

    assert.equal(normalizeDubFilter('EN-us', options), 'en-US');
    assert.equal(normalizeDubFilter('dubbed', options), 'none');
    assert.equal(normalizeDubFilter('', options), 'none');
});
