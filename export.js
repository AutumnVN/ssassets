import { spawnSync } from 'child_process';
import { existsSync, rmSync } from 'fs';

const FROM = './download';
const TO = './export';
const FROM2 = './download2';
const TO2 = './export2';

if (existsSync(TO)) rmSync(TO, { recursive: true, force: true });
if (existsSync(TO2)) rmSync(TO2, { recursive: true, force: true });

exportAssets(FROM, TO);
if (existsSync(FROM2)) exportAssets(FROM2, TO2);

if (existsSync(FROM)) rmSync(FROM, { recursive: true, force: true });
if (existsSync(FROM2)) rmSync(FROM2, { recursive: true, force: true });

function exportAssets(from, to) {
    const res = spawnSync('dotnet', ['./assetStudioMod/AssetStudioModCLI.dll', from, '-t', 'tex2d', '-o', to, '--image-format', 'webp'], { stdio: 'inherit' });
    if (res.status !== 0) {
        console.error(`Export failed for ${from} (exit code ${res.status}, signal ${res.signal})`);
        process.exit(1);
    }
}
