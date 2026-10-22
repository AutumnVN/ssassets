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

function parseFP(text, field) {
    const match = text.match(new RegExp(`FP ${field}\\r?\\n\\s*SInt64 _serializedValue = (-?\\d+)`));
    return match ? round(Number(match[1]) / 2 ** 32) : undefined;
}

function round(value) {
    return Math.round(value * 1e7) / 1e7;
}

function getTimerHits(duration, hitDelay, hitInterval, hitImmediately) {
    if (hitInterval <= 0) {
        if (hitDelay > 0) return duration > hitDelay ? 1 : 0;
        return hitImmediately ? 1 : 0;
    }

    const firstHit = hitDelay > 0 ? hitDelay : hitImmediately ? 0 : hitInterval;
    if (duration <= firstHit) return 0;

    const ratio = Math.round((duration - firstHit) / hitInterval * 1e9) / 1e9;

    return Math.max(1, Math.ceil(ratio));
}

function getTriggerHits(duration, attack) {
    if (attack.triggerEnter && attack.finishTriggerEnter) return { hits: 1, ends: true };
    if (attack.triggerStay && attack.finishTriggerExit) return { hits: 1, ends: true };
    if (attack.triggerExit && attack.finishTriggerExit) return { hits: 1, ends: true };

    if (attack.triggerStay) {
        const every = attack.timeTriggerStay;

        return {
            hits: every <= 0
                ? (duration > 0 ? 2 : 0)
                : (duration > every ? Math.ceil(duration / every) : 0),
            ends: false,
        };
    }

    if (attack.triggerEnter || attack.triggerExit) return { hits: 1, ends: false };

    return { hits: 0, ends: false };
}

function getComponentHits(duration, attack) {
    const timer = getTimerHits(duration, attack.hitDelay, attack.hitInterval, attack.hitImmediately);
    const trigger = getTriggerHits(duration, attack);

    if (trigger.ends) return Math.max(timer, trigger.hits);

    return Math.max(timer, trigger.hits, timer >= 1 && trigger.hits >= 1 ? 2 : 0);
}

function getEntityHits(duration, hitInterval, hitImmediately) {
    const ratio = hitInterval > 0 ? Math.round(duration / hitInterval * 1e9) / 1e9 : 0;
    const timer = hitInterval > 0 ? Math.floor(ratio) : 0;

    return (hitImmediately ? 1 : 0) + timer;
}

function getFirstHit(hitDelay, hitInterval, hitImmediately) {
    if (hitDelay > 0) return hitDelay;

    return hitImmediately ? 0 : hitInterval;
}

function addVariant(map, key, variant, copy) {
    let entry = map[key];

    if (!entry) {
        entry = { variants: [] };
        map[key] = entry;
    }

    if (copy !== undefined) entry.copyBattleDataBeforeAttack = entry.copyBattleDataBeforeAttack || copy;

    const known = entry.variants.some(other =>
        other.alias === variant.alias &&
        other.duration === variant.duration &&
        other.hitDelay === variant.hitDelay &&
        other.hitInterval === variant.hitInterval &&
        other.hits === variant.hits
    );

    if (!known) entry.variants.push(variant);

    const best = entry.variants.reduce((a, b) => b.hits > a.hits ? b : a);

    entry.duration = best.duration;
    entry.hitDelay = best.hitDelay;
    entry.hitInterval = best.hitInterval;
    entry.hits = best.hits;
    entry.triggers = best.triggers;
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

    if (first === 'GameObject Base') {
        return { kind: 'GameObject', name: parseString(text, 'm_Name') };
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
        const gameObjectNames = new Map();

        for (const file of files) {
            const name = file.slice(file.lastIndexOf('/') + 1);
            const idMatch = name.match(/@(-?\d+)\.txt$/);
            if (!idMatch) continue;
            const pathId = idMatch[1];
            const asset = parseAsset(readFileSync(file, 'utf8'));
            if (!asset.kind) continue;
            objects.set(pathId, asset);
            if (asset.kind === 'MonoScript' && asset.className) classNames.set(pathId, asset.className);
            if (asset.kind === 'GameObject' && asset.name) gameObjectNames.set(pathId, asset.name);
        }

        const entities = new Map();
        const attacks = [];
        const specials = [];

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
                    duration: parseFP(asset.text, 'duration') ?? 1,
                });
            } else if (className === 'LineAreaWeaponEntity' || className === 'SpikeweedEneity') {
                const hitDamageId = parseNum(asset.text, /\bhitDamageId = (-?\d+)/);
                if (!hitDamageId || hitDamageId <= 0) continue;

                const duration = parseFP(asset.text, 'duration') ?? 1;
                const hitInterval = parseFP(asset.text, 'hitInterval') ?? 1;
                const hitImmediately = parseFlag(asset.text, 'hitImmediately') ?? false;

                specials.push({
                    ids: [hitDamageId],
                    alias: gameObjectNames.get(asset.gameObject),
                    duration,
                    hitDelay: getFirstHit(0, hitInterval, hitImmediately),
                    hitInterval,
                    hits: getEntityHits(duration, hitInterval, hitImmediately),
                });
            } else if (className === 'AreaEffect_AttackComponent' || className.startsWith('AreaEffect_AttackComponent_')) {
                const list = parseArrayInts(asset.text, 'hitDamageIdList');
                const single = parseNum(asset.text, /\bhitDamageId = (-?\d+)/);
                const ids = list.length ? list : single > 0 ? [single] : [];
                if (!ids.length) continue;

                attacks.push({
                    gameObject: asset.gameObject,
                    ids,
                    hitDelay: parseFP(asset.text, 'hitDelay') ?? 0,
                    hitInterval: parseFP(asset.text, 'hitInterval') ?? 1,
                    hitImmediately: parseFlag(asset.text, 'hitImmediately') ?? true,
                    timeTriggerStay: parseFP(asset.text, 'time_TriggerStay') ?? 1,
                    triggerEnter: parseFlag(asset.text, 'triggerEnter') ?? false,
                    triggerStay: parseFlag(asset.text, 'triggerStay') ?? false,
                    triggerExit: parseFlag(asset.text, 'triggerExit') ?? false,
                    finishTriggerEnter: parseFlag(asset.text, 'finish_TriggerEnter') ?? false,
                    finishTriggerExit: parseFlag(asset.text, 'finish_TriggerExit') ?? false,
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

            for (const attack of entityAttacks) {
                const componentHits = getComponentHits(entity.duration, attack);
                const triggers = [
                    attack.triggerEnter ? 'triggerEnter' : null,
                    attack.triggerStay ? 'triggerStay' : null,
                    attack.triggerExit ? 'triggerExit' : null,
                    attack.finishTriggerEnter ? 'finish_TriggerEnter' : null,
                    attack.finishTriggerExit ? 'finish_TriggerExit' : null,
                ].filter(Boolean);

                attack.ids.forEach((hitDamageId, index) => {
                    const key = String(hitDamageId);
                    const idHits = attack.ids.length > 1
                        ? Math.ceil((componentHits - index) / attack.ids.length)
                        : componentHits;

                    addVariant(map, key, {
                        alias: gameObjectNames.get(gameObject),
                        duration: entity.duration,
                        hitDelay: getFirstHit(attack.hitDelay, attack.hitInterval, attack.hitImmediately),
                        hitInterval: attack.hitInterval,
                        hits: idHits,
                        triggers,
                    }, entity.copy);
                });
            }
        }

        for (const special of specials) {
            for (const hitDamageId of special.ids) {
                addVariant(map, String(hitDamageId), {
                    alias: special.alias,
                    duration: special.duration,
                    hitDelay: special.hitDelay,
                    hitInterval: special.hitInterval,
                    hits: special.hits,
                    triggers: [],
                });
            }
        }
    }

    rmSync(work, { recursive: true, force: true });

    console.log(`areaEffect: ${resolved} attack component(s) matched an AreaEffectEntity, ${unresolved} unresolved`);

    const output = {};

    for (const id of Object.keys(map).sort((a, b) => a - b)) {
        output[id] = map[id];
    }

    return { map: output };
}

const source = process.argv[2] ?? findStoreDir();

if (!source) {
    console.error('Area effect export failed: no unpacked store found under ResourceTool/output/Unpack');
    process.exit(1);
}

const { map } = extractMap(source);
const existing = existsSync(OUTPUT) ? JSON.parse(readFileSync(OUTPUT, 'utf8')) : {};
const merged = {};

for (const [id, value] of Object.entries(existing)) {
    merged[id] = typeof value === 'boolean' ? { copyBattleDataBeforeAttack: value, hits: 2 } : value;
}

Object.assign(merged, map);

if (!Object.keys(merged).length) {
    console.error('Area effect export failed: no area effect hitDamage ids found');
    process.exit(1);
}

const sorted = {};

for (const id of Object.keys(merged).sort((a, b) => a - b)) sorted[id] = merged[id];

writeFileSync(OUTPUT, JSON.stringify(sorted, null, 2) + '\n');
console.log(`${Object.keys(sorted).length} areaEffect hitDamage ids`);
