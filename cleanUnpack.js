import { existsSync, readdirSync, readFileSync, rmSync } from 'fs';
import { join } from 'path';

const MANIFEST = 'ResourceTool/output/Manifest';
const UNPACK = 'ResourceTool/output/Unpack';
const PATCH_PREFIX = /^p_\d+_[un]\./;

const expected = new Set();

if (existsSync(MANIFEST)) {
    for (const region of readdirSync(MANIFEST, { withFileTypes: true })) {
        if (!region.isDirectory()) continue;
        for (const file of ['win_resource_manifest.json', 'win_patch_manifest.json']) {
            const manifestPath = join(MANIFEST, region.name, file);
            if (!existsSync(manifestPath)) continue;
            const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
            for (const name of Object.keys(manifest)) {
                expected.add(name.replace(PATCH_PREFIX, ''));
            }
        }
    }
}

if (expected.size === 0) {
    console.log('No manifests found, skipping stale bundle cleanup');
    process.exit(0);
}

let removed = 0;

if (existsSync(UNPACK)) {
    removed = clean(UNPACK);
}

console.log(`Removed ${removed} stale bundle(s)`);

function clean(dir) {
    let count = 0;
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) {
            count += clean(full);
        } else if (entry.name.endsWith('.unity3d') && !expected.has(entry.name)) {
            rmSync(full);
            count++;
        }
    }
    return count;
}
