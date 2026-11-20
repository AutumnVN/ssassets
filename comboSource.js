import { spawnSync } from 'child_process';
import { existsSync } from 'fs';

const source = process.argv[2] ?? './ResourceTool/output/Unpack/EN/win';

const dll = './tools/Extract/bin/Release/net9.0/Extract.dll';
const args = existsSync(dll)
    ? [dll, 'comboSource', source]
    : ['run', '--project', './tools/Extract', '-c', 'Release', '--', 'comboSource', source];
const result = spawnSync('dotnet', args, { stdio: 'inherit' });

process.exit(result.status ?? 1);
