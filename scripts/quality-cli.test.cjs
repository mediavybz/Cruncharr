const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

test('the quality command rejects unsupported options', () => {
    const result = spawnSync(process.execPath, [path.join(__dirname, 'quality.cjs'), '--skip-tests'], { encoding: 'utf8' });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Usage:/);
});
