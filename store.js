import { existsSync, readdirSync } from 'fs';

export function findStoreDir() {
    const base = './ResourceTool/output/Unpack';
    for (const dir of [`${base}/EN/win`, `${base}/EN`, `${base}/win`, base]) {
        if (existsSync(dir) && readdirSync(dir).some((name) => name.endsWith('.unity3d'))) return dir;
    }
    return null;
}
