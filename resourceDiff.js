import { createHash } from 'crypto';
import { copyFileSync, createReadStream, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'fs';
import { join } from 'path';
import { pipeline } from 'stream/promises';
import { findStoreDir } from './store.js';

const SNAPSHOT = './diff-resource/manifest.json';
const MANIFEST = './diff-manifest.json';
const OUTPUT = './diff_out';

const source = findStoreDir();

if (!source) {
    console.error('Diff failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

const previous = existsSync(SNAPSHOT) ? JSON.parse(readFileSync(SNAPSHOT, 'utf8')) : {};
const next = {};

if (existsSync(OUTPUT)) rmSync(OUTPUT, { recursive: true, force: true });
mkdirSync(OUTPUT, { recursive: true });

const bundles = readdirSync(source).filter((name) => name.endsWith('.unity3d')).sort();
let changed = 0;

for (const name of bundles) {
    const file = join(source, name);
    const size = statSync(file).size;
    const hash = await digest(file);
    next[name] = { size, hash };

    const old = previous[name];
    if (old && old.size === size && old.hash === hash) continue;

    copyFileSync(file, join(OUTPUT, name));
    changed++;
}

writeFileSync(MANIFEST, JSON.stringify(next, null, 2) + '\n');
console.log(`diff: ${changed} changed bundle(s) of ${bundles.length}`);

async function digest(file) {
    const hash = createHash('sha256');
    await pipeline(createReadStream(file), hash);
    return hash.digest('hex');
}
