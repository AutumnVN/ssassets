import { spawnSync } from 'child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'fs';
import { tmpdir } from 'os';
import { findStoreDir } from './store.js';

const OUTPUT = './bullet.json';
const ASSET_STUDIO = './assetStudioMod/AssetStudioModCLI.dll';
const BUNDLE_PATTERN = /^char_\d+(_(?:buff|breakout|combos|combosmonster|config|configs|fx|soldier|towerdefense|weapon|weaponmodel|weapons))?\.unity3d$/;

function parseNum(text, re) {
    const match = text.match(re);
    return match ? Number(match[1]) : undefined;
}

function parseString(text, field) {
    const match = text.match(new RegExp(`${field} = "([^"]*)"`));
    return match ? match[1] : undefined;
}

function parsePathId(text, field) {
    const match = text.match(new RegExp(`${field}[\\s\\S]*?m_PathID = (-?\\d+)`));
    if (!match || match[1] === '0') return undefined;
    return match[1];
}

function parseIds(text) {
    const ids = [];
    const single = parseNum(text, /\bhitDamageId = (-?\d+)/);
    if (single > 0) ids.push(single);

    const dataStart = text.indexOf('_hitdamagedatas');
    if (dataStart >= 0) {
        const chunk = text.slice(dataStart);
        const size = parseNum(chunk, /int size = (\d+)/) || 0;
        if (size > 0) {
            let added = 0;
            for (const match of chunk.matchAll(/\b(?:H|h)itDamageId = (-?\d+)/g)) {
                ids.push(Number(match[1]));
                if (++added >= size) break;
            }
        }
    }

    for (const field of ['hitDamageIdList', '_hitDamageIdList']) {
        const start = text.indexOf(field);
        if (start < 0) continue;

        const chunk = text.slice(start);
        const size = parseNum(chunk, /int size = (\d+)/) || 0;
        if (size <= 0) continue;

        let added = 0;
        for (const match of chunk.matchAll(/\[\d+\]\s*\n\s*int data = (-?\d+)/g)) {
            ids.push(Number(match[1]));
            if (++added >= size) break;
        }
    }

    return [...new Set(ids.filter(id => id > 0))];
}

function parseAsset(text) {
    const first = text.split(/\r?\n/, 1)[0];

    if (first === 'GameObject Base') {
        return { kind: 'GameObject', name: parseString(text, 'm_Name') };
    }

    if (first === 'MonoBehaviour Base') {
        const hasDamage = /\bhitDamageId = -?\d+/.test(text) ||
            text.includes('hitDamageIdList') ||
            text.includes('_hitdamagedatas');
        if (!hasDamage) return {};

        return {
            kind: 'MonoBehaviour',
            gameObject: parsePathId(text, 'm_GameObject'),
            ids: parseIds(text),
        };
    }

    return {};
}

function extractMap(bundleDir) {
    const bundleNames = readdirSync(bundleDir)
        .filter(name => BUNDLE_PATTERN.test(name))
        .sort();

    const work = mkdtempSync(`${tmpdir()}/ssassets-bullet-`);
    const map = {};
    let resolved = 0;
    let unresolved = 0;

    for (const bundle of bundleNames) {
        const outDir = `${work}/${bundle}`;
        mkdirSync(outDir, { recursive: true });
        console.log(`Dumping ${bundle}`);
        const result = spawnSync('dotnet', [
            ASSET_STUDIO,
            `${bundleDir}/${bundle}`,
            '-m', 'dump', '--load-all', '-t', 'all',
            '-f', 'assetName_pathID',
            '-o', outDir, '-r', '--log-level', 'error',
        ], { stdio: 'inherit' });
        if (result.status !== 0) {
            console.error(`bullet: failed ${bundle} (exit code ${result.status}, signal ${result.signal})`);
            continue;
        }

        const files = [];
        (function walk(dir) {
            for (const entry of readdirSync(dir, { withFileTypes: true })) {
                const file = `${dir}/${entry.name}`;
                if (entry.isDirectory()) walk(file);
                else if (entry.name.endsWith('.txt')) files.push(file);
            }
        })(outDir);

        const gameObjects = new Map();
        const sources = [];

        for (const file of files) {
            const asset = parseAsset(readFileSync(file, 'utf8'));
            const idMatch = file.match(/@(-?\d+)\.txt$/);
            if (!idMatch) continue;

            if (asset.kind === 'GameObject' && asset.name) {
                gameObjects.set(idMatch[1], asset.name);
            } else if (asset.kind === 'MonoBehaviour' && asset.ids.length) {
                sources.push(asset);
            }
        }

        for (const source of sources) {
            const name = source.gameObject && gameObjects.get(source.gameObject);
            if (!name) {
                unresolved++;
                continue;
            }

            resolved++;
            const ids = new Set(map[name] || []);
            for (const id of source.ids) ids.add(id);
            map[name] = [...ids].sort((a, b) => a - b);
        }
    }

    rmSync(work, { recursive: true, force: true });

    console.log(`bullet: ${resolved} damage source(s) matched a GameObject, ${unresolved} unresolved`);
    return map;
}

const source = process.argv[2] ?? findStoreDir();

if (!source) {
    console.error('Bullet export failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

const map = extractMap(source);
const existing = existsSync(OUTPUT) ? JSON.parse(readFileSync(OUTPUT, 'utf8')) : {};
const merged = new Map();

for (const [name, ids] of Object.entries(existing)) {
    merged.set(name, new Set(Array.isArray(ids) ? ids : [ids]));
}

for (const [name, ids] of Object.entries(map)) {
    const mergedIds = merged.get(name) || new Set();
    for (const id of ids) mergedIds.add(id);
    merged.set(name, mergedIds);
}

if (!merged.size) {
    console.error('Bullet export failed: no bullet hitDamage ids found');
    process.exit(1);
}

const sorted = {};

for (const name of [...merged.keys()].sort()) {
    sorted[name] = [...merged.get(name)].sort((a, b) => a - b);
}

writeFileSync(OUTPUT, JSON.stringify(sorted, null, 2) + '\n');
console.log(`${Object.keys(sorted).length} bullet prefabs`);
