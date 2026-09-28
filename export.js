import { spawnSync } from 'child_process';
import { existsSync, mkdirSync, readdirSync, rmSync } from 'fs';

const CONTAINERS = ['./export/assets/assetbundles', './export/assets/assetbundles_en'];

const UNSTABLE = /_#\d+(\.[^.]+)$/;

const TARGETS = {
    store: { types: ['icon', 'image'] },
    ui: { from: './download', types: ['ui_activity'] },
};

const mode = process.argv[2];
const target = TARGETS[mode];

if (!target) {
    console.error(`usage: node export.js <${Object.keys(TARGETS).join('|')}>`);
    process.exit(1);
}

if (mode === 'store') target.from = findStoreDir();

if (!target.from || !existsSync(target.from)) {
    console.error(`Export failed: source ${target.from || '(not found)'} does not exist`);
    process.exit(1);
}

for (const container of CONTAINERS) {
    if (!existsSync(container)) continue;
    for (const type of target.types) {
        const stale = `${container}/${type}`;
        if (existsSync(stale)) {
            rmSync(stale, { recursive: true, force: true });
            console.log(`Removed stale ${stale}`);
        }
    }
}

mkdirSync(CONTAINERS[0], { recursive: true });

console.log(`Exporting ${target.from}`);
const res = spawnSync('dotnet', ['./assetStudioMod/AssetStudioModCLI.dll', target.from, '-t', 'tex2d', '-o', './export', '--image-format', 'webp'], { stdio: 'inherit' });
if (res.status !== 0) {
    console.error(`Export failed for ${target.from} (exit code ${res.status}, signal ${res.signal})`);
    process.exit(1);
}

console.log(`Exported ${target.from} -> ./export`);

for (const type of target.types) {
    for (const container of CONTAINERS) {
        const dropped = dropUnstable(`${container}/${type}`);
        if (dropped) console.log(`Dropped ${dropped} duplicate id file(s) from ${container}/${type}`);
    }
}

function dropUnstable(dir) {
    if (!existsSync(dir)) return 0;
    let dropped = 0;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = `${dir}/${entry.name}`;
        if (entry.isDirectory()) {
            dropped += dropUnstable(full);
        } else if (UNSTABLE.test(entry.name)) {
            if (existsSync(full.replace(UNSTABLE, '$1'))) {
                rmSync(full);
                dropped++;
            } else {
                console.log(`Kept ${entry.name}, no stable counterpart`);
            }
        }
    }
    return dropped;
}

function findStoreDir() {
    const base = './ResourceTool/output/Unpack';
    if (!existsSync(base)) return null;
    for (const region of ['EN', '']) {
        const dir = region ? `${base}/${region}` : base;
        if (existsSync(dir) && readdirSync(dir).some((name) => name.endsWith('.unity3d'))) return dir;
    }
    return null;
}
