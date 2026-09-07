/**
 * generate-metadata.js
 * Converts the original SegmentMap.js and choices/en.js assets into
 * the bandersnatch-metadata.json schema used by the Jellyfin plugin.
 *
 * Run from the repo root:
 *   node generate-metadata.js
 */

const fs   = require('fs');
const path = require('path');

// ── Load source files ────────────────────────────────────────────────────────

const assetsDir = path.join(__dirname, 'assets');

// If the assets directory doesn't exist, the existing Web/bandersnatch-metadata.json
// is already complete and up-to-date. This script only needs to run when you have
// the original Netflix SegmentMap.js / choices/en.js available locally.
if (!fs.existsSync(assetsDir)) {
    console.log('ℹ️  assets/ directory not found — skipping regeneration.');
    console.log('   The existing Web/bandersnatch-metadata.json is already complete.');
    process.exit(0);
}

// SegmentMap.js declares a global: SegmentMap = { ... }
let SegmentMap;
{
    const raw = fs.readFileSync(path.join(assetsDir, 'SegmentMap.js'), 'utf8');
    // eslint-disable-next-line no-new-func
    const fn = new Function('SegmentMap', raw + '; return SegmentMap;');
    SegmentMap = fn(undefined);
    if (!SegmentMap) {
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

// ── Quality-label check ───────────────────────────────────────────────────────

/**
 * Returns true if 'text' looks like a real human-readable button label
 * rather than a raw segment ID or internal reference.
 *
 * Segment IDs contain digits (1A, 3Vfs), underscores (8B_Variant2),
 * or start with 'nsg-'. Real labels are English words/phrases.
 */
function isQualityLabel(text, fallbackId) {
    if (!text || text === fallbackId) return false;
    if (/^\d/.test(text))          return false; // STARTS with digit → segment ID (1A, 3Vfs)
    if (text.startsWith('nsg-'))   return false; // internal NSG reference
    if (text.includes('_'))        return false; // 8B_Variant2-style ID
    return true;
}

// ── Build output schema ──────────────────────────────────────────────────────

const out = {
    version: 1,
    detection: {
        titleContains: 'Bandersnatch',
        durationMs: 18734000,
        durationToleranceMs: 60000
    },
    segments:     {},
    choicePoints: {},
    stateFlags:   {}
};

const segments = SegmentMap.segments;

let skippedHubs    = 0;
let skippedNoLabel = 0;
let skippedBadNext = 0;

for (const [segId, seg] of Object.entries(segments)) {
    const nextIds = seg.next ? Object.keys(seg.next) : [];

    // ── Strict choice-point criteria ────────────────────────────────────────
    //
    // Real user-facing choices in Bandersnatch are always binary (2 options)
    // with equal weights (50/50). Hub / transition segments have many options
    // (10-15) with small weights (5-7) and should not show as choice UI.
    //
    // We enforce:
    //   1. Exactly 2 next options
    //   2. Both weights ≥ 25 (equal-weight binary choice)
    //   3. Both target segment IDs exist in the SegmentMap (no nsg-* or Variant refs)
    //   4. At least one option resolves to a quality human-readable label in en.js

    const isCandidate = (() => {
        if (nextIds.length !== 2) { skippedHubs++;    return false; }

        const weights = Object.values(seg.next).map(n =>
            typeof n === 'object' ? (n.weight ?? 0) : (n ?? 0));
        if (weights.some(w => w < 25)) { skippedHubs++; return false; }

        if (!nextIds.every(id => segments[id])) { skippedBadNext++; return false; }

        return true;
    })();

    // Resolve interaction zone for timing
    const zones     = seg.ui?.interactionZones ?? [];
    const appearMs  = zones.length > 0 ? zones[0][0] : null;
    const zoneEndMs = zones.length > 0 ? zones[0][1] : null;

    out.segments[segId] = {
        startMs:       seg.startTimeMs,
        endMs:         seg.endTimeMs,
        choicePointId: null,     // set below if this becomes a choice point
        alternativeOf: []
    };

    if (!isCandidate) continue;

    // Resolve button labels from en.js
    const labels = choiceLabels[segId] ?? {};

    const options = nextIds.map((nextId, i) => {
        // First try exact match on nextId key, then fall back to i-th label by position
        const text = labels[nextId]
            ?? labels[Object.keys(labels)[i]]
            ?? nextId;           // last resort: raw segment ID

        return {
            text,
            nextSegmentId: nextId,
            isDefault:     nextId === seg.defaultNext
        };
    });

    // Require at least one option to have a genuine human-readable label.
    // This filters out segments where label lookup fails entirely.
    const hasQualityLabel = options.some(o => isQualityLabel(o.text, o.nextSegmentId));
    if (!hasQualityLabel) {
        skippedNoLabel++;
        continue;
    }

    // For any option that lacks a quality label, provide a generic fallback
    // rather than showing the raw segment ID on the button.
    options.forEach((o, i) => {
        if (!isQualityLabel(o.text, o.nextSegmentId)) {
            o.text = `Option ${i + 1}`;
        }
    });

    // Put the default option first so arrow-key navigation starts there
    options.sort((a, b) => (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0));

    // Mark the segment as having a choice point
    out.segments[segId].choicePointId = segId;

    out.choicePoints[segId] = {
        label:        labels[segId] ?? segId,   // descriptive label for logging
        appearMs:     appearMs  ?? Math.max(0, seg.endTimeMs - 15000),
        segmentEndMs: zoneEndMs ?? seg.endTimeMs,
        requiredState: {},
        options
    };
}

// ── Write output ─────────────────────────────────────────────────────────────

const outPath = path.join(__dirname, 'Web', 'bandersnatch-metadata.json');
fs.writeFileSync(outPath, JSON.stringify(out, null, 2), 'utf8');

const segCount    = Object.keys(out.segments).length;
const choiceCount = Object.keys(out.choicePoints).length;

console.log(`✅ Generated bandersnatch-metadata.json`);
console.log(`   Segments:            ${segCount}`);
console.log(`   Choice points kept:  ${choiceCount}`);
console.log(`   Skipped (hubs):      ${skippedHubs}`);
console.log(`   Skipped (bad nexts): ${skippedBadNext}`);
console.log(`   Skipped (no label):  ${skippedNoLabel}`);
console.log(`   Output:              ${outPath}`);
