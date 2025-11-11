const fs = require('fs');

const FROM = 'C:\\YostarGames\\StellaSora_EN\\StellaSora_Data\\StreamingAssets\\InstallResource';
const TO = '.\\unity3d';

const regexes = [
    // /^cg_.{4}\.unity3d$/,
    // /^disc_.{4}\.unity3d$/,
    /^icon-.{1}\.unity3d$/,
    // /^image-.{4}\.unity3d$/,
    // /^ui_.*\.unity3d$/,
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
