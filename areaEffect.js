import { spawnSync } from 'child_process';
import { existsSync } from 'fs';
import { findStoreDir } from './store.js';

const source = process.argv[2] ?? findStoreDir();

if (!source) {
    console.error('Area effect export failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

const dll = './tools/Extract/bin/Release/net9.0/Extract.dll';
const args = existsSync(dll)
    ? [dll, 'areaEffect', source]
    : ['run', '--project', './tools/Extract', '-c', 'Release', '--', 'areaEffect', source];
const result = spawnSync('dotnet', args, { stdio: 'inherit' });

process.exit(result.status ?? 1);
