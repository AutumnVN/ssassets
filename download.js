import { existsSync, mkdirSync, writeFileSync, rmSync } from 'fs';
import { join } from 'path';
import { createHash } from 'crypto';

const API = 'https://api-launcher-en.yo-star.com';
const CDN = 'https://launcher-pkg-ss-en.yo-star.com';
const OUT = './download';
const REGEX = /^(?:icon-|image-).*\.unity3d$/;

const lastestYml = await fetch(`${CDN}/install_pkg/game_launcher/StellaSora_EN/latest.yml`).then((res) => res.text());
const launcherVersion = lastestYml.match(/version: (.*)/)[1];
console.log('Launcher version:', launcherVersion);

const gameInfo = await fetch(`${API}/api/launcher/game/config`, { headers: authHeaders() }).then((res) => res.json());
const gameLatestVersion = gameInfo.data.game_latest_version;
const gameLatestFilePath = gameInfo.data.game_latest_file_path;
console.log('Game latest version:', gameLatestVersion);
console.log('Game latest file path:', gameLatestFilePath);

const manifestUrlRes = await fetch(`${API}/api/launcher/game/config/json?version=${gameLatestVersion}&file_path=${gameLatestFilePath}`, { headers: authHeaders() }).then((res) => res.json());
const manifestUrl = manifestUrlRes.data.url;
console.log('Manifest URL:', manifestUrl);

const manifest = await fetch(`${manifestUrl}?nocache=${Date.now()}`).then((res) => res.json());
const files = manifest.file.filter((file) => REGEX.test(file.path.split('/').pop()));
console.log(`Downloading ${files.length} files...`);

if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

let downloaded = 0;
const paths = files.map((f) => f.path);

while (paths.length > 0) {
    const chunkSize = 16;
    const chunk = paths.splice(0, chunkSize);
    const promises = chunk.map(async (path) => {
        const fileUrl = CDN + manifest.source + path;
        try {
            const fileBuf = await fetch(fileUrl).then((res) => res.arrayBuffer());
            writeFileSync(join(OUT, path.split('/').pop()), Buffer.from(fileBuf));
            downloaded++;
            console.log(`Downloaded ${downloaded}/${files.length}: ${path.split('/').pop()}`);
        } catch (err) {
            console.error(`Failed to download ${path}: ${err.message}`);
            paths.push(path);
        }
    });

    await Promise.all(promises);
}

console.log('All files downloaded successfully!');

function authHeaders() {
    const salt = 'DE7108E9B2842FD460F4777702727869';
    const head = { game_tag: 'StellaSora_EN', time: Math.floor(Date.now() / 1000), version: launcherVersion };
    const sign = createHash('md5').update(JSON.stringify(head) + salt).digest('hex');
    return { 'Authorization': JSON.stringify({ head, sign }) };
}
