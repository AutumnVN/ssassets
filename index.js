const fs = require('fs');

const FROM = 'C:\\YostarGames\\StellaSora_EN\\Persistent_Store\\AssetBundles';
const TO = '.\\unity3d';

const regexes = [
    // /^cg_.*\.unity3d$/,
    // /^disc_.*\.unity3d$/,
    /^icon-.*\.unity3d$/,
    // /^image-.*\.unity3d$/,
    /^ui_.*\.unity3d$/,
];

if (!fs.existsSync(TO)) {
    fs.mkdirSync(TO);
}

fs.readdirSync(FROM).forEach(file => {
    for (const regex of regexes) {
        if (regex.test(file)) {
            fs.copyFileSync(`${FROM}\\${file}`, `${TO}\\${file}`);
            console.log(`Copied: ${file}`);
            break;
        }
    }
});
