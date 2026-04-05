import { spawnSync } from 'child_process';
import { existsSync, rmSync } from 'fs';

const FROM = './download';
const TO = './export';
const FROM2 = './download2';
const TO2 = './export2';

if (existsSync(TO)) rmSync(TO, { recursive: true, force: true });
if (existsSync(TO2)) rmSync(TO2, { recursive: true, force: true });

spawnSync('dotnet', ['./assetStudioMod/AssetStudioModCLI.dll', FROM, '-t', 'tex2d', '-o', TO, '--image-format', 'webp'], { stdio: 'inherit' });
spawnSync('dotnet', ['./assetStudioMod/AssetStudioModCLI.dll', FROM2, '-t', 'tex2d', '-o', TO2, '--image-format', 'webp'], { stdio: 'inherit' });

if (existsSync(FROM)) rmSync(FROM, { recursive: true, force: true });
if (existsSync(FROM2)) rmSync(FROM2, { recursive: true, force: true });
