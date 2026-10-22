import { spawnSync } from 'child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'fs';
import { tmpdir } from 'os';
import { findStoreDir } from './store.js';

const OUTPUT = './areaEffect.json';
const ASSET_STUDIO = './assetStudioMod/AssetStudioModCLI.dll';
const BUNDLE_PATTERN = /^char_\d+(_(?:buff|breakout|combos|combosmonster|config|configs|fx|soldier|towerdefense|weapon|weaponmodel|weapons))?\.unity3d$/;

function parseNum(text, re) {
    const match = text.match(re);
    return match ? Number(match[1]) : undefined;
}

function parseFlag(text, field) {
    const match = text.match(new RegExp(`${field} = (\\d+|[Tt]rue|[Ff]alse)`));
    if (!match) return undefined;
    const value = match[1];
    if (/^\d+$/.test(value)) return value === '1';
    return value.toLowerCase() === 'true';
}

function parseFieldNumber(text, field) {
    const start = text.indexOf(`${field} =`);
    if (start < 0) return undefined;

    const match = text.slice(start, start + 200).match(/-?\d+(?:\.\d+)?/);
    return match ? Number(match[0]) : undefined;
}

function getHitCount(duration, hitDelay, hitInterval, hitImmediately) {
    const firstHit = hitDelay > 0 ? hitDelay : hitImmediately ? 0 : hitInterval;
    if (duration < firstHit) return 0;
    if (hitInterval <= 0) return 1;
    return 1 + Math.floor((duration - firstHit) / hitInterval);
}

function parseString(text, field) {
    const match = text.match(new RegExp(`${field} = "([^"]*)"`));
    return match ? match[1] : undefined;
}

function parsePathId(text, field) {
    const match = text.match(new RegExp(`${field}[\\s\\S]*?m_PathID = (-?\\d+)`));
    if (!match || match[1] === '0') return undefined;
    return match[1];
}

function parseArrayInts(text, field) {
    const start = text.indexOf(field);
    if (start < 0) return [];
    const chunk = text.slice(start);
    const size = parseNum(chunk, /int size = (\d+)/) || 0;
    const values = [];
    const re = /\[(\d+)\]\s*\n\s*int data = (-?\d+)/g;
    let match;
    while ((match = re.exec(chunk))) values.push(Number(match[2]));
    return values.slice(0, size);
}

function parseAsset(text) {
    const first = text.split(/\r?\n/, 1)[0];

    if (first === 'MonoScript Base') {
        return { kind: 'MonoScript', className: parseString(text, 'm_ClassName') };
    }

    if (first === '{') {
        const match = text.match(/"m_ClassName":\s*"([^"]*)"/);
        if (match && text.includes('"type": "MonoScript"')) {
            return { kind: 'MonoScript', className: match[1] };
        }
        return {};
    }

    if (first === 'MonoBehaviour Base') {
        return {
            kind: 'MonoBehaviour',
            gameObject: parsePathId(text, 'm_GameObject'),
            script: parsePathId(text, 'm_Script'),
            text,
        };
    }

    return {};
}

function extractMap(bundleDir) {
    const bundleNames = readdirSync(bundleDir)
        .filter(name => BUNDLE_PATTERN.test(name))
        .sort();

    const work = mkdtempSync(`${tmpdir()}/ssassets-areaEffect-`);
    const map = {};
    let resolved = 0;
    let unresolved = 0;

    for (const bundle of bundleNames) {
        const outDir = `${work}/${bundle}`;
        mkdirSync(outDir, { recursive: true });
        console.log(`Dumping ${bundle}`);
        const result = spawnSync('dotnet', [
            ASSET_STUDIO,
            `${bundleDir}/${bundle}`,
            '-m', 'dump', '--load-all', '-t', 'all',
            '-f', 'assetName_pathID',
            '-o', outDir, '-r', '--log-level', 'info',
        ], { stdio: 'inherit' });
        if (result.status !== 0) {
            console.error(`areaEffect: failed ${bundle} (exit code ${result.status}, signal ${result.signal})`);
            continue;
        }

        const files = [];
        (function walk(dir) {
            for (const entry of readdirSync(dir, { withFileTypes: true })) {
                const file = `${dir}/${entry.name}`;
                if (entry.isDirectory()) walk(file);
                else if (entry.name.endsWith('.txt')) files.push(file);
            }
        })(outDir);

        const objects = new Map();
        const classNames = new Map();

        for (const file of files) {
            const name = file.slice(file.lastIndexOf('/') + 1);
            const idMatch = name.match(/@(-?\d+)\.txt$/);
            if (!idMatch) continue;
            const pathId = idMatch[1];
            const asset = parseAsset(readFileSync(file, 'utf8'));
            if (!asset.kind) continue;
            objects.set(pathId, asset);
            if (asset.kind === 'MonoScript' && asset.className) classNames.set(pathId, asset.className);
        }

        const entities = new Map();
        const attacks = [];

        for (const asset of objects.values()) {
            if (asset.kind !== 'MonoBehaviour') continue;
            if (!asset.gameObject || !asset.script) continue;
            const className = classNames.get(asset.script);
            if (!className) continue;

            if (className === 'AreaEffectEntity') {
                const copy = parseFlag(asset.text, 'copyBattleDataBeforeAttack');
                if (copy === undefined) continue;
                entities.set(asset.gameObject, {
                    copy,
                    duration: parseFieldNumber(asset.text, 'duration') ?? 1,
                });
            } else if (className === 'AreaEffect_AttackComponent' || className.startsWith('AreaEffect_AttackComponent_')) {
                const ids = [
                    parseNum(asset.text, /hitDamageId = (-?\d+)/),
                    ...parseArrayInts(asset.text, 'hitDamageIdList'),
                ].filter(id => id > 0);
                if (!ids.length) continue;

                attacks.push({
                    gameObject: asset.gameObject,
                    ids,
                    hitDelay: parseFieldNumber(asset.text, 'hitDelay') ?? 0,
                    hitInterval: parseFieldNumber(asset.text, 'hitInterval') ?? 1,
                    hitImmediately: parseFlag(asset.text, 'hitImmediately') ?? true,
                    hasTrigger: (parseFlag(asset.text, 'triggerEnter') ?? false) ||
                        (parseFlag(asset.text, 'triggerStay') ?? false) ||
                        (parseFlag(asset.text, 'triggerExit') ?? false),
                    finishesOnHit: ((parseFlag(asset.text, 'triggerEnter') ?? false) &&
                            (parseFlag(asset.text, 'finish_TriggerEnter') ?? false)) ||
                        ((parseFlag(asset.text, 'triggerStay') ?? false) &&
                            (parseFlag(asset.text, 'finish_TriggerStay') ?? false)) ||
                        ((parseFlag(asset.text, 'triggerExit') ?? false) &&
                            (parseFlag(asset.text, 'finish_TriggerExit') ?? false)),
                });
            }
        }

        const attacksByEntity = new Map();
        for (const attack of attacks) {
            const list = attacksByEntity.get(attack.gameObject);
            if (list) list.push(attack);
            else attacksByEntity.set(attack.gameObject, [attack]);
        }

        for (const [gameObject, entityAttacks] of attacksByEntity) {
            const entity = entities.get(gameObject);
            if (!entity) {
                unresolved += entityAttacks.length;
                continue;
            }
            resolved += entityAttacks.length;

            let hits = 0;
            let persistent = false;

            for (const attack of entityAttacks) {
                const timeHits = getHitCount(entity.duration, attack.hitDelay, attack.hitInterval, attack.hitImmediately);
                hits += timeHits;

                if (!attack.hasTrigger) continue;
                if (attack.finishesOnHit) hits++;
                else persistent = true;
            }

            const oneShot = !persistent && hits <= 1;

            for (const attack of entityAttacks) {
                for (const hitDamageId of attack.ids) {
                    const key = String(hitDamageId);
                    if (!map[key]) {
                        map[key] = { copyBattleDataBeforeAttack: entity.copy, oneShot };
                    } else {
                        map[key].oneShot = map[key].oneShot && oneShot;
                    }
                }
            }
        }
    }

    rmSync(work, { recursive: true, force: true });

    console.log(`areaEffect: ${resolved} attack component(s) matched an AreaEffectEntity, ${unresolved} unresolved`);

    const output = {};
    const ignored = [];

    for (const id of Object.keys(map).sort((a, b) => a - b)) {
        const entry = map[id];
        if (entry.oneShot) ignored.push(id);
        else output[id] = entry.copyBattleDataBeforeAttack;
    }

    return { map: output, ignored };
}

const source = process.argv[2] ?? findStoreDir();

if (!source) {
    console.error('Area effect export failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

const { map, ignored } = extractMap(source);
const existing = existsSync(OUTPUT) ? JSON.parse(readFileSync(OUTPUT, 'utf8')) : {};
const merged = { ...existing, ...map };
for (const id of ignored) delete merged[id];

if (!Object.keys(merged).length) {
    console.error('Area effect export failed: no area effect hitDamage ids found');
    process.exit(1);
}

const sorted = {};

for (const id of Object.keys(merged).sort((a, b) => a - b)) sorted[id] = merged[id];

writeFileSync(OUTPUT, JSON.stringify(sorted, null, 2) + '\n');
console.log(`${Object.keys(sorted).length} areaEffect hitDamage ids`);
