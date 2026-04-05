import { existsSync, mkdirSync, rmSync, copyFileSync, readdirSync } from 'fs';
import path from 'path';

const FROM = './export2';
const TO = './export';

function copyRecursiveSync(src, dest) {
    const entries = readdirSync(src, { withFileTypes: true });
    for (const entry of entries) {
        const srcPath = path.join(src, entry.name);
        const destPath = path.join(dest, entry.name);

        if (entry.isDirectory()) {
            if (!existsSync(destPath)) {
                mkdirSync(destPath);
            }
            copyRecursiveSync(srcPath, destPath);
        } else if (entry.isFile() || entry.isSymbolicLink()) {
            const destDir = path.dirname(destPath);
            if (!existsSync(destDir)) {
                mkdirSync(destDir, { recursive: true });
            }
            copyFileSync(srcPath, destPath);
        }
    }
}

if (!existsSync(FROM)) {
    console.log(`"${FROM}" does not exist. Nothing to do.`);
    process.exit(0);
}

if (!existsSync(TO)) {
    mkdirSync(TO, { recursive: true });
}

try {
    copyRecursiveSync(FROM, TO);
    rmSync(FROM, { recursive: true, force: true });
    console.log(`Copied "${FROM}" -> "${TO}" and removed "${FROM}".`);
} catch (err) {
    console.error('Error during merge:', err);
    process.exit(1);
}

