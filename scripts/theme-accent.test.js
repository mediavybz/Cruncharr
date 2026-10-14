const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const requests = require('../src/Cruncharr.API/wwwroot/js/calendar-request-state.js');
const source = fs.readFileSync(require.resolve('../src/Cruncharr.API/wwwroot/js/app.js'), 'utf8');

// A custom accent used to change only --accent, so the theme's own glows, soft fills, gradient and
// the text colour on accent fills stayed behind.
function app() {
    const props = new Map();
    const attributes = new Map();
    const background = { style: {}, dataset: {}, setAttribute() {}, replaceWith() {} };
    const root = {
        style: {
            setProperty: (name, value) => props.set(name, value),
            removeProperty: name => props.delete(name),
            getPropertyValue: name => props.get(name) ?? ''
        },
        setAttribute: (name, value) => attributes.set(name, value),
        removeAttribute: name => attributes.delete(name)
    };
    const context = vm.createContext({
        URL, Headers, AbortController, console,
        CruncharrLibrary: require('../src/Cruncharr.API/wwwroot/js/library-state.js'),
        CruncharrCalendarRequests: requests,
        document: {
            documentElement: root,
            body: { prepend() {}, classList: { toggle() {} } },
            addEventListener() {},
            querySelectorAll() { return []; },
            createElement: () => ({ ...background }),
            getElementById: id => id === 'custom-background' ? background : null
        },
        sessionStorage: { getItem() {}, setItem() {}, removeItem() {} },
        localStorage: { getItem() {}, setItem() {}, removeItem() {} },
        setTimeout: () => 0,
        clearTimeout() {},
        fetch: async () => ({ ok: true, json: async () => ({}) })
    });
    context.window = { fetch: context.fetch, location: { href: 'http://localhost/', origin: 'http://localhost' }, matchMedia: () => ({ addEventListener() {} }) };
    vm.runInContext(source, context);
    return {
        props, attributes,
        apply: (theme, accentColor) => vm.runInContext(`config = { appearance: ${JSON.stringify({ theme, accentColor })} }; applyTheme();`, context)
    };
}

test('a custom accent recolours every derived token and picks readable text', () => {
    const ui = app();

    ui.apply('Seerr', '#22aa88');
    assert.equal(ui.attributes.get('data-theme'), 'seerr');
    assert.equal(ui.props.get('--accent'), '#22aa88');
    assert.equal(ui.props.get('--accent-soft'), 'rgba(34, 170, 136, 0.16)');
    assert.match(ui.props.get('--accent-grad'), /#22aa88/);
    assert.equal(ui.props.get('--on-accent'), '#0a0a0a');

    ui.apply('Dark', '#1e3a8a');
    assert.equal(ui.props.get('--on-accent'), '#ffffff');
});

test('the default orange or no accent leaves the theme its own colours', () => {
    const ui = app();
    ui.apply('Sonarr', '#22aa88');
    ui.apply('Sonarr', '#F47521');
    assert.equal(ui.props.size, 0);
    ui.apply('Seerr', '');
    assert.equal(ui.props.size, 0);
    ui.apply('Dark', 'not-a-colour');
    assert.equal(ui.props.size, 0);
    assert.equal(ui.attributes.has('data-theme'), false);
});
