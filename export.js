import { spawnSync } from 'child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, copyFileSync } from 'fs';
import { tmpdir } from 'os';
import { join, dirname } from 'path';
import { findStoreDir } from './store.js';

const ROOT = './export';
const CONTAINERS = ['assets/assetbundles', 'assets/assetbundles_en'];

const TYPES = [
    { name: 'icon', bundles: /^icon-/ },
    { name: 'image', bundles: /^image-/ },
    { name: 'ui_activity', bundles: /^ui_activity/ },
];

const UNSTABLE = /_#\d+(\.[^.]+)$/;

const source = process.argv[2] ?? findStoreDir();

if (!source) {
    console.error('Export failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

console.log(`Exporting ${source}`);

const bundles = readdirSync(source).filter((name) => name.endsWith('.unity3d'));
const work = mkdtempSync(join(tmpdir(), 'ssassets-export-'));

try {
    for (const type of TYPES) {
        const selected = bundles.filter((name) => type.bundles.test(name));
        if (selected.length === 0) {
            console.log(`No ${type.name} bundle found`);
            continue;
        }

        const input = join(work, type.name, 'in');
        const output = join(work, type.name, 'out');
        mkdirSync(input, { recursive: true });
        for (const name of selected) copyFileSync(join(source, name), join(input, name));

        console.log(`Exporting ${selected.length} ${type.name} bundle(s)`);

        const res = spawnSync(
            'dotnet',
            ['./assetStudioMod/AssetStudioModCLI.dll', input, '-t', 'tex2d', '-o', output, '--image-format', 'webp'],
            { stdio: 'inherit' },
        );
        if (res.status !== 0) {
            console.error(`Export failed for ${type.name} (exit code ${res.status}, signal ${res.signal})`);
            process.exit(1);
        }

        for (const container of CONTAINERS) {
            const from = join(output, container, type.name);
            if (!existsSync(from)) continue;
            const to = join(ROOT, container, type.name);
            mkdirSync(dirname(to), { recursive: true });
            cpSync(from, to, { recursive: true, force: true });
        }
    }
} finally {
    rmSync(work, { recursive: true, force: true });
}

for (const type of TYPES) {
    for (const container of CONTAINERS) {
        const dropped = dropUnstable(`${ROOT}/${container}/${type.name}`);
        if (dropped) console.log(`Dropped ${dropped} duplicate id file(s) from ${ROOT}/${container}/${type.name}`);
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
