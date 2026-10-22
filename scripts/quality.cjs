const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.resolve(__dirname, '..');

function run(command, args, capture = false) {
    const result = spawnSync(command, args, {
        cwd: root,
        encoding: 'utf8',
        stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
        maxBuffer: 16 * 1024 * 1024
    });
    if (result.error) throw new Error(`${command}: ${result.error.message}`);
    if (result.status !== 0) {
        throw new Error(`${command} failed (${result.status ?? result.signal})${capture ? `\n${result.stderr}\n${result.stdout}` : ''}`);
    }
    return result.stdout;
}

function main() {
    const args = process.argv.slice(2);
    if (args.length !== 0 && (args.length !== 2 || args[0] !== '--base' || args[1].startsWith('-'))) {
        throw new Error('Usage: node scripts/quality.cjs [--base <base-commit>]');
    }
    if (Number(process.versions.node.split('.')[0]) < 22) throw new Error('Node.js 22 or newer is required.');
    const base = run('git', ['rev-parse', '--verify', `${args[1] || 'HEAD'}^{commit}`], true).trim();
    run('git', ['diff', '--check', base]);

    const files = run('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], true)
        .split('\0').filter(file => file && fs.existsSync(path.join(root, file)));
    const scripts = [...new Set(files)].filter(file =>
        (file.startsWith('scripts/') || file.startsWith('src/Cruncharr.API/wwwroot/js/')) && /\.(cjs|mjs|js)$/.test(file));
    console.log(`Checking JavaScript syntax in ${scripts.length} files.`);
    for (const file of scripts) run(process.execPath, ['--check', file]);
    const tests = scripts.filter(file => /\.test\.(js|cjs|mjs)$/.test(file));
    if (tests.length === 0) throw new Error('No JavaScript tests found.');
    run(process.execPath, ['--test', ...tests]);

    const ffmpeg = process.env.CRUNCHARR_TEST_FFMPEG;
    if (ffmpeg && !fs.existsSync(ffmpeg)) throw new Error('CRUNCHARR_TEST_FFMPEG must point to an existing executable.');
    run(ffmpeg || 'ffmpeg', ['-version'], true);
    run('dotnet', ['restore', 'cruncharr.sln']);
    const changed = run('git', ['diff', '--name-only', '--diff-filter=ACMR', '-z', base, '--', '*.cs'], true)
        .split('\0').filter(Boolean);
    const untracked = run('git', ['ls-files', '--others', '--exclude-standard', '-z', '--', '*.cs'], true)
        .split('\0').filter(Boolean);
    const csharp = [...new Set([...changed, ...untracked])];
    if (csharp.length) {
        run('dotnet', ['format', 'whitespace', 'cruncharr.sln', '--no-restore', '--verify-no-changes', '--include', ...csharp]);
    } else {
        console.log('No changed C# files to format-check against the selected base.');
    }
    run('dotnet', ['build', 'cruncharr.sln', '--configuration', 'Release', '--no-restore', '-p:RunAnalyzersDuringBuild=true']);
    run('dotnet', ['test', 'src/Cruncharr.Core.Tests/Cruncharr.Core.Tests.csproj', '--configuration', 'Release', '--no-build', '--no-restore']);
    console.log('Quality checks passed. Live integrations and rendered UI require separate checks when affected.');
}

try {
    main();
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
