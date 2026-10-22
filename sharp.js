import { existsSync, mkdirSync, readFileSync } from 'fs';
import { dirname } from 'path';
import sharp from 'sharp';

const DATA_URL = 'https://raw.githubusercontent.com/AutumnVN/ss-data/refs/heads/main/character.json';

const data = await fetch(DATA_URL).then((res) => res.json());

for (const charId in data) {
    for (const potCategory in data[charId].potential) {
        for (const pot of data[charId].potential[potCategory]) {
            const vestige = pot.rarity === 'core' ? '0_0' : pot.rarity === 'rare' ? '1' : '2';
            const { id, name, icon, corner } = pot;
            const bgPath = `./image/vestige_${vestige}.webp`;
            const iconPath = `./export/assets/assetbundles/icon/potential/${icon}_A.webp`;
            const cornerMaskPath = corner ? `./export/assets/assetbundles/icon/potential/Potential_${corner}_B.webp` : null;
            const cornerPath = corner ? `./export/assets/assetbundles/icon/potential/Potential_${corner}_A.webp` : null;
            const outPath = `./potential/${id}.webp`;

            mkdirSync(dirname(outPath), { recursive: true });

            const bgBuf = readFileSync(bgPath);
            const bgMeta = await sharp(bgBuf).metadata();
            const bgWidth = bgMeta.width;
            const bgHeight = bgMeta.height;

            const gradSvg = `<?xml version='1.0' encoding='utf-8'?>
                <svg xmlns='http://www.w3.org/2000/svg' width='${bgWidth}' height='${bgHeight}'>
                    <defs>
                        <linearGradient id='g' gradientTransform='rotate(135)'>
                            <stop offset='0%' stop-color='#d8e' />
                            <stop offset='100%' stop-color='#7ff' />
                        </linearGradient>
                    </defs>
                    <rect width='100%' height='100%' fill='url(#g)' />
                </svg>`;
            const baseBuf = Buffer.from(gradSvg);

            const composites = [];

            composites.push({ input: bgBuf, left: 0, top: 0 });

            if (iconPath && existsSync(iconPath)) {
                const iconBuf = readFileSync(iconPath);
                const iconMeta = await sharp(iconBuf).metadata();
                const iconWidth = bgWidth;
                const iconHeight = Math.round((iconMeta.height / iconMeta.width) * iconWidth);
                const iconResized = await sharp(iconBuf).resize(iconWidth, iconHeight).toBuffer();
                composites.push({ input: iconResized, left: 0, top: 0 });
            }

            if (cornerMaskPath && existsSync(cornerMaskPath)) {
                const cornerMaskBuf = readFileSync(cornerMaskPath);
                const cornerMaskMeta = await sharp(cornerMaskBuf).metadata();
                const cornerMaskWidth = bgWidth;
                const cornerMaskHeight = Math.round((cornerMaskMeta.height / cornerMaskMeta.width) * cornerMaskWidth);
                const cornerMaskResized = await sharp(cornerMaskBuf).resize(cornerMaskWidth, cornerMaskHeight).png().toBuffer();
                const color = pot.rarity === 'rare' ? '#97d' : '#d80';
                const rectSvg = `<?xml version='1.0' encoding='utf-8'?>
                    <svg xmlns='http://www.w3.org/2000/svg' width='${cornerMaskWidth}' height='${cornerMaskHeight}' viewBox='0 0 ${cornerMaskWidth} ${cornerMaskHeight}'>
                        <defs>
                            <mask id='m'>
                                <image href='data:image/png;base64,${cornerMaskResized.toString('base64')}' width='${cornerMaskWidth}' height='${cornerMaskHeight}' preserveAspectRatio='xMidYMid slice' />
                            </mask>
                        </defs>
                        <rect width='${cornerMaskWidth}' height='${cornerMaskHeight}' fill='${color}' mask='url(#m)' />
                    </svg>`;
                composites.push({ input: Buffer.from(rectSvg), left: 0, top: 0 });
            }

            if (cornerPath && existsSync(cornerPath)) {
                const cornerBuf = readFileSync(cornerPath);
                const cornerMeta = await sharp(cornerBuf).metadata();
                const cornerWidth = bgWidth;
                const cornerHeight = Math.round((cornerMeta.height / cornerMeta.width) * cornerWidth);
                const cornerResized = await sharp(cornerBuf).resize(cornerWidth, cornerHeight).toBuffer();
                composites.push({ input: cornerResized, left: 0, top: 0 });
            }

            const FONT_POINT_SIZE = 54;
            const FACE_LINE_HEIGHT = 71.604;
            const FACE_ASCENT = 56.376;
            const FACE_DESCENT = -15.228;
            const OUTLINE_WIDTH = 0.559;
            const OUTLINE_SCALE_RATIO_A = 0.9;
            const GRADIENT_SCALE = 10;
            const CHARACTER_SPACING = -0.23;
            const LINE_SPACING = -20.32;

            const baseFontSize = bgWidth * 0.1;
            const maxTextWidth = Math.round(bgWidth * 0.95);
            const upscaleSidePadding = Math.round(bgWidth * 0.06);
            const maxUpscaleWidth = bgWidth - upscaleSidePadding * 2;

            let fontSize = baseFontSize;
            let lines = wrapTextToLines(name, fontSize, maxTextWidth);

            if (lines.length === 1) {
                const textWidth = String(name).length * fontSize * 0.55;
                if (textWidth < maxUpscaleWidth) {
                    const fittedFontSize = (maxUpscaleWidth - 1) / (String(name).length * 0.55);
                    fontSize = Math.min(fittedFontSize, baseFontSize * 4 / 3);
                    lines = wrapTextToLines(name, fontSize, maxTextWidth);
                }
            }

            const strokeWidth = 2 * OUTLINE_WIDTH * OUTLINE_SCALE_RATIO_A * GRADIENT_SCALE * (fontSize / FONT_POINT_SIZE);
            const lineHeight = (FACE_LINE_HEIGHT + LINE_SPACING) * (fontSize / FONT_POINT_SIZE);
            const letterSpacing = CHARACTER_SPACING * (fontSize / FONT_POINT_SIZE);

            const centerOffset = (FACE_ASCENT + FACE_DESCENT) / 2 * (fontSize / FONT_POINT_SIZE);
            const baseCenterOffset = (FACE_ASCENT + FACE_DESCENT) / 2 * (baseFontSize / FONT_POINT_SIZE);
            const blockCenterY = bgHeight - baseFontSize * 0.9 - baseCenterOffset;
            const centeredFirstLineY = blockCenterY + centerOffset - (lines.length - 1) * lineHeight / 2;
            const maxFirstLineY = bgHeight - fontSize * 0.28 - (lines.length - 1) * lineHeight;
            const firstLineY = Math.min(centeredFirstLineY, maxFirstLineY);

            const tspans = lines.map((ln, i) => {
                const dy = i === 0 ? 0 : lineHeight;
                return `<tspan x="50%" dy="${dy}px">${escapeXml(ln)}</tspan>`;
            }).join('');

            const svg = `<?xml version='1.0' encoding='utf-8'?>
                <svg xmlns='http://www.w3.org/2000/svg' width='${bgWidth}' height='${bgHeight}'>
                    <style>
                        .title {
                            font-family: 'MiSans Latin SemiBold';
                            font-size: ${fontSize}px;
                            letter-spacing: ${letterSpacing}px;
                            fill: #568;
                            stroke: #fff;
                            stroke-width: ${strokeWidth}px;
                            stroke-linejoin: round;
                            paint-order: stroke;
                        }
                    </style>
                    <text x='50%' y='${firstLineY}' text-anchor='middle' class='title'>${tspans}</text>
                </svg>`;

            composites.push({ input: Buffer.from(svg), left: 0, top: 0 });

            await sharp(baseBuf).composite(composites).png().toFile(outPath);
            console.log(`Assembled ${outPath}`);
        }
    }
}

function wrapTextToLines(text, fontSizePx, maxWidthPx) {
    const words = String(text).split(/\s+/).filter(Boolean);
    const approxCharWidth = fontSizePx * 0.55;
    const lines = [];
    let cur = '';
    for (const w of words) {
        const test = cur ? cur + ' ' + w : w;
        const testWidth = test.length * approxCharWidth;
        if (testWidth <= maxWidthPx || !cur) {
            cur = test;
        } else {
            lines.push(cur);
            cur = w;
        }
    }
    if (cur) lines.push(cur);
    return lines;
}

function escapeXml(str) {
    return String(str).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[c]));
}
