/**
 * generate-metadata.js
 * Converts the original SegmentMap.js and choices/en.js assets into
 * the bandersnatch-metadata.json schema used by the Jellyfin plugin.
 *
 * Run from the repo root:
 *   node bandersnatch-choice-plugin/generate-metadata.js
 */

const fs = require('fs');
const path = require('path');

// ── Load source files ────────────────────────────────────────────────────────

const assetsDir = path.join(__dirname, 'assets');

// If the assets directory doesn't exist, the existing Web/bandersnatch-metadata.json
// is already complete and up-to-date. This script only needs to run when you have
// the original Netflix SegmentMap.js / choices/en.js available locally.
if (!require('fs').existsSync(assetsDir)) {
    console.log('ℹ️  assets/ directory not found — skipping regeneration.');
    console.log('   The existing Web/bandersnatch-metadata.json is already complete.');
    process.exit(0);
}

// SegmentMap.js declares a global: SegmentMap = { ... }
// We eval it in a sandbox to extract the object.
let SegmentMap;
{
    const raw = fs.readFileSync(path.join(assetsDir, 'SegmentMap.js'), 'utf8');
    // eslint-disable-next-line no-new-func
    const fn = new Function('SegmentMap', raw + '; return SegmentMap;');
    SegmentMap = fn(undefined);
    if (!SegmentMap) {
        // fallback: SegmentMap= at top level
        const sandboxed = {};
        new Function('global', raw).call(sandboxed, sandboxed);
        SegmentMap = sandboxed.SegmentMap;
    }
}

// choices/en.js declares: en = { ... }
let choiceLabels;
{
    const raw = fs.readFileSync(path.join(assetsDir, 'choices', 'en.js'), 'utf8');
    const sandboxed = {};
    new Function('global', raw + '\nthis.en = en;').call(sandboxed);
    choiceLabels = sandboxed.en ?? {};
}

// ── Build output schema ──────────────────────────────────────────────────────

const out = {
    version: 1,
    detection: {
        titleContains: 'Bandersnatch',
        durationMs: 18734000,
        durationToleranceMs: 60000
    },
    segments: {},
    choicePoints: {},
    stateFlags: {}
};

const segments = SegmentMap.segments;

for (const [segId, seg] of Object.entries(segments)) {
    const nextIds = seg.next ? Object.keys(seg.next) : [];
    const isChoicePoint = nextIds.length >= 2;

    // interactionZone: the window where the choice UI is shown
    // It's the first (and usually only) entry in ui.interactionZones
    const zones = seg.ui?.interactionZones ?? [];
    const appearMs  = zones.length > 0 ? zones[0][0] : null;
    const zoneEndMs = zones.length > 0 ? zones[0][1] : null;

    out.segments[segId] = {
        startMs: seg.startTimeMs,
        endMs:   seg.endTimeMs,
        // choicePointId is the same as segId when this segment ends with a choice
        choicePointId: isChoicePoint ? segId : null,
        alternativeOf: []   // filled in a second pass if needed
    };

    if (isChoicePoint) {
        // Resolve choice button labels from en.js
        const labels = choiceLabels[segId] ?? {};

        const options = nextIds.map((nextId, i) => {
            const labelKey   = nextId;
            const buttonText = labels[labelKey]
                ?? labels[Object.keys(labels)[i]]
                ?? nextId;

            return {
                text:          buttonText,
                nextSegmentId: nextId,
                isDefault:     nextId === seg.defaultNext
            };
        });

        // Ensure default is first if found
        options.sort((a, b) => (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0));

        out.choicePoints[segId] = {
            label:       `Choice: ${segId}`,   // human label placeholder
            appearMs:    appearMs  ?? (seg.endTimeMs - 15000),
            segmentEndMs: zoneEndMs ?? seg.endTimeMs,
            requiredState: {},
            options
        };
    }
}

// ── Write output ─────────────────────────────────────────────────────────────

const outPath = path.join(__dirname, 'Web', 'bandersnatch-metadata.json');
fs.writeFileSync(outPath, JSON.stringify(out, null, 2), 'utf8');

const segCount    = Object.keys(out.segments).length;
const choiceCount = Object.keys(out.choicePoints).length;

console.log(`✅ Generated bandersnatch-metadata.json`);
console.log(`   Segments:     ${segCount}`);
console.log(`   ChoicePoints: ${choiceCount}`);
console.log(`   Output:       ${outPath}`);
