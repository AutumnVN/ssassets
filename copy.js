import { existsSync, mkdirSync, rmSync, copyFileSync, readdirSync } from 'fs';

const INSTALLRESOURCE = 'C:/YostarGames/StellaSora_EN/StellaSora_Data/StreamingAssets/InstallResource';
const ASSETBUNDLEs = 'C:/YostarGames/StellaSora_EN/Persistent_Store/AssetBundles';
const OUT = './download';
const OUT2 = './download2';
const REGEX = /^(?:icon-|image-|ui_activity__6).*\.unity3d$/;

if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
if (existsSync(OUT2)) rmSync(OUT2, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
mkdirSync(OUT2, { recursive: true });

copyAssets(INSTALLRESOURCE, OUT);
copyAssets(ASSETBUNDLEs, OUT2);

function copyAssets(srcDir, destDir) {
    const entries = existsSync(srcDir) ? readdirSync(srcDir, { withFileTypes: true }) : [];
    for (const entry of entries) {
        const srcPath = `${srcDir}/${entry.name}`;
        if (entry.isDirectory()) {
            copyAssets(srcPath, destDir);
        } else if (REGEX.test(entry.name)) {
            const destPath = `${destDir}/${entry.name}`;
            copyFileSync(srcPath, destPath);
            console.log(`Copied: ${entry.name}`);
        }
    }
}
