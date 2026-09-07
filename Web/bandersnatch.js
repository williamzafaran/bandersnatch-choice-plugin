/**
 * bandersnatch.js
 * Bandersnatch Interactive Choice Plugin for Jellyfin
 *
 * Injected into Jellyfin's index.html by the C# plugin on startup.
 * Monitors playback and overlays Netflix-accurate interactive choice UI
 * on top of the native Jellyfin player.
 *
 * Inspired by the intro-skipper injection pattern.
 */

(function () {
    'use strict';

    // ================================================================
    // CONFIGURATION CONSTANTS
    // ================================================================

    const PLUGIN_API_BASE = '/BandersnatchChoice';
    const BANDERSNATCH_TITLE_KEYWORD = 'bandersnatch';
    /** Expected duration of the full interactive cut in milliseconds (~5h 12m 14s) */
    const BANDERSNATCH_DURATION_MS = 18734000;
    /** Acceptable ± tolerance in milliseconds when matching duration */
    const DURATION_TOLERANCE_MS = 60000;
    /** Minimum seek delta (seconds) to trigger seek-crossing detection */
    const MIN_SEEK_DELTA_SEC = 2;

    // ================================================================
    // STATE
    // ================================================================

    let pluginConfig = { enabled: true, leadInSeconds: 10, autoDetect: true, manualItemId: null };
    let metadata = null;       // Full bandersnatch-metadata.json
    let isActive = false;      // True when Bandersnatch is detected and playing
    let currentItemId = null;

    // Video element reference
    let video = null;

    // Seek tracking — updated on every timeupdate *before* a seek fires
    let previousTime = 0;
    let isSeeking = false;
    let seekStartTime = 0;

    // Choice overlay state
    let activeChoicePoint = null;   // { id, ...choicePointData }
    let highlightedIndex = 0;

    // Session state (persisted in localStorage)
    let sessionState = null;

    // DOM element references
    let choiceOverlayEl = null;
    let choiceButtonsEl = null;
    let countdownBarEl = null;
    let seekPromptEl = null;

    // MutationObserver for video detection
    let domObserver = null;

    // ================================================================
    // ENTRY POINT
    // ================================================================

    function init() {
        console.log('[Bandersnatch] Plugin loaded — waiting for video element.');

        domObserver = new MutationObserver(onDOMChange);
        domObserver.observe(document.body, { childList: true, subtree: true });

        // Check immediately in case the video element already exists
        onDOMChange();
    }

    // ================================================================
    // VIDEO DETECTION (SPA navigation aware)
    // ================================================================

    function onDOMChange() {
        const videoEl = document.querySelector('video');

        if (videoEl && videoEl !== video) {
            // New video element found
            onVideoAppeared(videoEl);
        } else if (!videoEl && video) {
            // Video element was removed — clean up
            onVideoRemoved();
        }
    }

    async function onVideoAppeared(videoEl) {
        video = videoEl;
        console.log('[Bandersnatch] Video element detected — attempting item ID resolution.');

        // Retry for up to 3 seconds: the src attribute is often set after the element appears
        let itemId = null;
        for (let attempt = 0; attempt < 6; attempt++) {
            await sleep(500);
            itemId = await resolveCurrentItemId(video);
            if (itemId) break;
            console.log(`[Bandersnatch] Item ID not resolved yet (attempt ${attempt + 1}/6)…`);
        }

        if (!itemId) {
            console.warn('[Bandersnatch] Could not resolve item ID after 3s — giving up.');
            return;
        }
        console.log('[Bandersnatch] Item ID resolved:', itemId);

        // Fetch plugin config first (needed for manual override check)
        pluginConfig = await fetchPluginConfig();
        if (!pluginConfig.enabled) {
            console.log('[Bandersnatch] Plugin is disabled in settings.');
            return;
        }

        // Check if this item is Bandersnatch
        const item = await fetchItemDetails(itemId);
        if (!item) {
            console.log('[Bandersnatch] Could not fetch item details.');
            return;
        }

        if (!isBandersnatch(item)) {
            console.log('[Bandersnatch] Item is not Bandersnatch — interactive mode off.');
            return;
        }

        console.log('[Bandersnatch] ✅ Bandersnatch detected! Activating interactive mode.');
        currentItemId = itemId;

        // Load segment metadata
        metadata = await fetchMetadata();
        if (!metadata) {
            console.error('[Bandersnatch] Failed to load metadata — cannot activate.');
            return;
        }

        // Precompute sorted chapter start times for fast seek detection
        buildChapterIndex();

        // Load or create fresh session state
        loadSessionState();

        // Build overlay DOM elements
        createOverlays();

        // Attach video event listeners
        attachVideoListeners();

        isActive = true;
        console.log('[Bandersnatch] Interactive mode active. LeadIn:', pluginConfig.leadInSeconds, 's');
    }

    function onVideoRemoved() {
        console.log('[Bandersnatch] Video removed — deactivating.');
        isActive = false;
        video = null;
        currentItemId = null;
        metadata = null;
        chapterIndex = [];
        removeOverlays();
        activeChoicePoint = null;
    }

    // ================================================================
    // ITEM DETECTION
    // ================================================================

    /**
     * Tries 4 sources in order to find the currently playing Jellyfin item ID.
     * Returns null if all sources fail.
     */
    async function resolveCurrentItemId(videoEl) {
        // Source 1: video.src URL  (works for direct play)
        const src = videoEl?.src || videoEl?.querySelector?.('source')?.src || '';
        if (src && !src.startsWith('blob:')) {
            const m = src.match(/\/Videos\/([a-f0-9]{8,})\//i);
            if (m) { console.log('[Bandersnatch] Item ID from video.src'); return m[1]; }
        }

        // Source 2: page URL hash  (?id=... or &id=...)
        const hashId = (window.location.hash + window.location.search)
            .match(/[?&#]id=([a-f0-9]{32})/i)?.[1];
        if (hashId) { console.log('[Bandersnatch] Item ID from URL hash'); return hashId; }

        // Source 3: window.ApiClient (Jellyfin SPA global)
        try {
            if (window.ApiClient) {
                const sessions = await window.ApiClient.getSessions();
                const mine = sessions?.find(s => s.NowPlayingItem?.Id);
                if (mine?.NowPlayingItem?.Id) {
                    console.log('[Bandersnatch] Item ID from ApiClient.getSessions()');
                    return mine.NowPlayingItem.Id;
                }
            }
        } catch { /* ApiClient might not have getSessions */ }

        // Source 4: /Sessions REST API
        try {
            const headers = {};
            const token = getAuthToken();
            if (token) headers['X-Emby-Authorization'] = `MediaBrowser Token="${token}"`;
            const res = await fetch('/Sessions', { headers });
            if (res.ok) {
                const sessions = await res.json();
                const mine = sessions.find(s => s.NowPlayingItem?.Id);
                if (mine?.NowPlayingItem?.Id) {
                    console.log('[Bandersnatch] Item ID from /Sessions API');
                    return mine.NowPlayingItem.Id;
                }
            }
        } catch { /* network error */ }

        return null;
    }

    function isBandersnatch(item) {
        // Manual override
        if (pluginConfig.manualItemId && item.Id === pluginConfig.manualItemId) {
            console.log('[Bandersnatch] Matched via manual item ID override');
            return true;
        }

        if (!pluginConfig.autoDetect) return false;

        const titleMatch = (item.Name ?? '').toLowerCase().includes(BANDERSNATCH_TITLE_KEYWORD);
        const durationMs = (item.RunTimeTicks ?? 0) / 10000; // ticks → ms
        const durationMatch = Math.abs(durationMs - BANDERSNATCH_DURATION_MS) < DURATION_TOLERANCE_MS;

        console.log(`[Bandersnatch] Auto-detect: title="${item.Name}" titleMatch=${titleMatch} durationMs=${Math.round(durationMs)} durationMatch=${durationMatch}`);
        return titleMatch && durationMatch;
    }

    // ================================================================
    // API CALLS
    // ================================================================

    async function fetchPluginConfig() {
        try {
            const res = await fetch(`${PLUGIN_API_BASE}/Config`);
            return res.ok ? res.json() : {};
        } catch {
            return {};
        }
    }

    async function fetchMetadata() {
        try {
            const res = await fetch(`${PLUGIN_API_BASE}/Metadata`);
            return res.ok ? res.json() : null;
        } catch {
            return null;
        }
    }

    async function fetchItemDetails(itemId) {
        try {
            const token = getAuthToken();
            const headers = {};
            if (token) {
                headers['X-Emby-Authorization'] =
                    `MediaBrowser Token="${token}", Client="BandersnatchChoice"`;
            }
            const res = await fetch(`/Items/${itemId}`, { headers });
            return res.ok ? res.json() : null;
        } catch {
            return null;
        }
    }

    function getAuthToken() {
        // Try Jellyfin's global ApiClient
        try {
            if (window.ApiClient?.accessToken?.()) return window.ApiClient.accessToken();
        } catch { /* ignore */ }

        // Try localStorage fallback
        try {
            const raw = localStorage.getItem('jellyfin_credentials');
            if (raw) {
                return JSON.parse(raw)?.Servers?.[0]?.AccessToken ?? null;
            }
        } catch { /* ignore */ }

        return null;
    }

    // ================================================================
    // CHAPTER INDEX (for fast seek crossing detection)
    // ================================================================

    /** Sorted array of { startMs, segmentId, choicePointId } for all segments */
    let chapterIndex = [];

    function buildChapterIndex() {
        chapterIndex = Object.entries(metadata.segments)
            .map(([id, seg]) => ({
                startMs: seg.startMs,
                segmentId: id,
                choicePointId: seg.choicePointId ?? null
            }))
            .sort((a, b) => a.startMs - b.startMs);
    }

    // ================================================================
    // SESSION STATE
    // ================================================================

    function stateKey() {
        return `bc_state_${currentItemId}`;
    }

    function loadSessionState() {
        try {
            const stored = localStorage.getItem(stateKey());
            if (stored) {
                sessionState = JSON.parse(stored);
                console.log('[Bandersnatch] Resuming session state.');
                return;
            }
        } catch { /* ignore */ }

        sessionState = {
            choicesMade: {},      // { choicePointId: chosenSegmentId }
            visitedSegments: [],  // [segmentId, ...]
            stateFlags: {}        // { flagName: value } for state-dependent choices
        };
        console.log('[Bandersnatch] Fresh session state created.');
    }

    function saveSessionState() {
        try {
            localStorage.setItem(stateKey(), JSON.stringify(sessionState));
        } catch { /* ignore */ }
    }

    function recordChoice(choicePointId, segmentId) {
        sessionState.choicesMade[choicePointId] = segmentId;
        if (!sessionState.visitedSegments.includes(segmentId)) {
            sessionState.visitedSegments.push(segmentId);
        }
        saveSessionState();
    }

    // ================================================================
    // VIDEO EVENT LISTENERS
    // ================================================================

    function attachVideoListeners() {
        video.addEventListener('timeupdate', onTimeUpdate);
        video.addEventListener('seeking', onSeeking);
        video.addEventListener('seeked', onSeeked);
    }

    function onSeeking() {
        isSeeking = true;
        seekStartTime = previousTime; // captured from last timeupdate
    }

    function onSeeked() {
        isSeeking = false;
        if (!isActive) return;

        const currentTime = video.currentTime;
        const delta = Math.abs(currentTime - seekStartTime);

        // Ignore trivially small seeks (buffering adjustments, etc.)
        if (delta < MIN_SEEK_DELTA_SEC) return;

        const seekedForward = currentTime > seekStartTime;

        if (seekedForward) {
            handleForwardSeek(seekStartTime, currentTime);
        } else {
            handleBackwardSeek(seekStartTime, currentTime);
        }
    }

    function onTimeUpdate() {
        if (!isSeeking) {
            previousTime = video.currentTime;
        }
        if (!isActive) return;

        checkChoiceWindow();
    }

    // ================================================================
    // SEEK DETECTION
    // ================================================================

    /**
     * Forward seek: only trigger on skipped CHOICE POINT chapters.
     * Linear chapters crossed while seeking forward are irrelevant — they
     * would have played automatically in sequence anyway.
     */
    function handleForwardSeek(fromSec, toSec) {
        const fromMs = fromSec * 1000;
        const toMs = toSec * 1000;

        // Find the earliest choice-point chapter that was skipped
        let skipped = null;
        for (const chapter of chapterIndex) {
            if (chapter.choicePointId && chapter.startMs > fromMs && chapter.startMs <= toMs) {
                skipped = chapter;
                break; // take the earliest one
            }
        }

        if (!skipped) return;

        const cp = metadata.choicePoints[skipped.choicePointId];
        const goBackToSec = Math.max(0, cp.appearMs / 1000);

        showSeekPrompt(
            'You skipped a choice point. Go back to make your decision?',
            goBackToSec
        );
    }

    /**
     * Backward seek: trigger on ANY chapter marker crossed.
     * Any backward seek across a segment boundary means the user wants to
     * go back to redo something — whether a choice or a scene they missed.
     */
    function handleBackwardSeek(fromSec, toSec) {
        const fromMs = fromSec * 1000;
        const toMs = toSec * 1000;

        // Find the most recent chapter marker crossed (highest startMs that's still between toMs and fromMs)
        let crossed = null;
        for (const chapter of chapterIndex) {
            if (chapter.startMs > toMs && chapter.startMs <= fromMs) {
                if (!crossed || chapter.startMs > crossed.startMs) {
                    crossed = chapter;
                }
            }
        }

        if (!crossed) return;

        // Determine prompt text based on whether the crossed chapter was a choice point
        let message;
        if (crossed.choicePointId) {
            const cp = metadata.choicePoints[crossed.choicePointId];
            message = `Return to "${cp.label}" to pick again?`;
        } else {
            message = 'Jump back to the start of this section?';
        }

        // Go back to the chapter start (or choice appear time if it's a choice point)
        let goBackToSec;
        if (crossed.choicePointId) {
            const cp = metadata.choicePoints[crossed.choicePointId];
            goBackToSec = Math.max(0, cp.appearMs / 1000);
        } else {
            goBackToSec = Math.max(0, crossed.startMs / 1000);
        }

        showSeekPrompt(message, goBackToSec);
    }

    // ================================================================
    // CHOICE WINDOW DETECTION (timeupdate)
    // ================================================================

    function checkChoiceWindow() {
        if (!metadata) return;
        const currentTimeMs = video.currentTime * 1000;

        // Check each choice point
        for (const [cpId, cp] of Object.entries(metadata.choicePoints)) {
            // Skip already-resolved choices
            if (sessionState.choicesMade[cpId]) continue;

            // Skip choices whose state conditions aren't met
            if (!meetsRequiredState(cp.requiredState)) continue;

            const inWindow = currentTimeMs >= cp.appearMs && currentTimeMs < cp.segmentEndMs;
            const pastEnd  = currentTimeMs >= cp.segmentEndMs;

            if (inWindow) {
                if (activeChoicePoint?.id !== cpId) {
                    // Entering this choice window for the first time
                    showChoiceOverlay(cpId, cp);
                } else {
                    // Already showing — update countdown
                    updateCountdown(cp.segmentEndMs - currentTimeMs, cp.segmentEndMs - cp.appearMs);
                }
                return; // Only one active choice at a time
            }

            if (pastEnd && activeChoicePoint?.id === cpId) {
                // Timer expired — commit the currently highlighted choice
                commitChoice();
                return;
            }
        }

        // Not in any choice window — hide overlay if it's showing
        if (activeChoicePoint) {
            hideChoiceOverlay();
        }
    }

    function meetsRequiredState(requiredState) {
        if (!requiredState || Object.keys(requiredState).length === 0) return true;
        for (const [key, value] of Object.entries(requiredState)) {
            if (sessionState.stateFlags[key] !== value) return false;
        }
        return true;
    }

    // ================================================================
    // CHOICE OVERLAY
    // ================================================================

    function showChoiceOverlay(cpId, cp) {
        activeChoicePoint = { id: cpId, ...cp };
        highlightedIndex = 0;

        // Render choice buttons
        choiceButtonsEl.innerHTML = '';
        cp.options.forEach((opt, i) => {
            const btn = document.createElement('button');
            btn.className = 'bc-choice-btn' + (i === 0 ? ' bc-choice-btn--highlighted' : '');
            btn.textContent = opt.text;
            btn.setAttribute('aria-label', opt.text);
            btn.dataset.index = String(i);

            // Hover → highlight (pre-select)
            btn.addEventListener('mouseenter', () => setHighlight(i));
            // Click → highlight only (does NOT commit — commitment happens at segmentEndMs)
            btn.addEventListener('click', () => setHighlight(i));

            choiceButtonsEl.appendChild(btn);
        });

        updateCountdown(cp.segmentEndMs - video.currentTime * 1000, cp.segmentEndMs - cp.appearMs);
        choiceOverlayEl.style.display = 'flex';

        // Keyboard navigation
        document.addEventListener('keydown', onChoiceKeyDown);

        console.log(`[Bandersnatch] Showing choice: "${cp.label}"`);
    }

    function hideChoiceOverlay() {
        choiceOverlayEl.style.display = 'none';
        activeChoicePoint = null;
        document.removeEventListener('keydown', onChoiceKeyDown);
    }

    function setHighlight(index) {
        const buttons = choiceButtonsEl.querySelectorAll('.bc-choice-btn');
        buttons.forEach((btn, i) => {
            btn.classList.toggle('bc-choice-btn--highlighted', i === index);
        });
        highlightedIndex = index;
    }

    function onChoiceKeyDown(e) {
        if (!activeChoicePoint) return;
        const count = activeChoicePoint.options.length;

        if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
            e.preventDefault();
            setHighlight((highlightedIndex + 1) % count);
        } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
            e.preventDefault();
            setHighlight((highlightedIndex - 1 + count) % count);
        }
    }

    function updateCountdown(remainingMs, totalMs) {
        if (!activeChoicePoint) return;
        const fraction = Math.max(0, Math.min(1, remainingMs / totalMs));
        countdownBarEl.style.width = `${fraction * 100}%`;
    }

    /**
     * Commits the currently highlighted choice (or the default if none highlighted).
     * Called when currentTime reaches the choice point's segmentEndMs.
     */
    function commitChoice() {
        if (!activeChoicePoint) return;

        const options = activeChoicePoint.options;
        const selected = options[highlightedIndex]
            ?? options.find(o => o.isDefault)
            ?? options[0];

        const targetSegment = metadata.segments[selected.nextSegmentId];

        if (!targetSegment) {
            console.error('[Bandersnatch] Target segment not found:', selected.nextSegmentId);
            hideChoiceOverlay();
            return;
        }

        console.log(`[Bandersnatch] Committing: "${selected.text}" → ${selected.nextSegmentId} @ ${targetSegment.startMs}ms`);

        recordChoice(activeChoicePoint.id, selected.nextSegmentId);
        hideChoiceOverlay();

        // Seek to the start of the chosen segment
        video.currentTime = targetSegment.startMs / 1000;
    }

    // ================================================================
    // SEEK PROMPT OVERLAY
    // ================================================================

    function showSeekPrompt(message, goBackToSec) {
        // Pause immediately so nothing unseen plays
        video.pause();

        seekPromptEl.querySelector('.bc-seek-prompt__msg').textContent = message;

        const goBackBtn = seekPromptEl.querySelector('.bc-seek-prompt__btn--primary');
        const stayBtn   = seekPromptEl.querySelector('.bc-seek-prompt__btn--secondary');

        // Remove old listeners before attaching new ones
        const newGoBackBtn = goBackBtn.cloneNode(true);
        const newStayBtn   = stayBtn.cloneNode(true);
        goBackBtn.replaceWith(newGoBackBtn);
        stayBtn.replaceWith(newStayBtn);

        newGoBackBtn.addEventListener('click', () => {
            seekPromptEl.style.display = 'none';
            video.currentTime = Math.max(0, goBackToSec);
            video.play();
        });
        newStayBtn.addEventListener('click', () => {
            seekPromptEl.style.display = 'none';
            video.play();
        });

        seekPromptEl.style.display = 'flex';
    }

    // ================================================================
    // DOM CREATION & TEARDOWN
    // ================================================================

    function createOverlays() {
        // ── Choice overlay ───────────────────────────────────────────
        choiceOverlayEl = document.createElement('div');
        choiceOverlayEl.className = 'bc-choice-overlay';
        choiceOverlayEl.style.display = 'none';
        choiceOverlayEl.innerHTML = `
            <div class="bc-choice-panel">
                <div class="bc-choice-buttons" id="bc-buttons"></div>
                <div class="bc-countdown-track">
                    <div class="bc-countdown-bar" id="bc-bar"></div>
                </div>
            </div>
        `;

        choiceButtonsEl = choiceOverlayEl.querySelector('#bc-buttons');
        countdownBarEl  = choiceOverlayEl.querySelector('#bc-bar');

        // ── Seek prompt overlay ──────────────────────────────────────
        seekPromptEl = document.createElement('div');
        seekPromptEl.className = 'bc-seek-overlay';
        seekPromptEl.style.display = 'none';
        seekPromptEl.innerHTML = `
            <div class="bc-seek-card">
                <p class="bc-seek-prompt__msg"></p>
                <div class="bc-seek-actions">
                    <button class="bc-seek-btn bc-seek-prompt__btn--primary">Go Back</button>
                    <button class="bc-seek-btn bc-seek-prompt__btn--secondary">Stay Here</button>
                </div>
            </div>
        `;

        document.body.appendChild(choiceOverlayEl);
        document.body.appendChild(seekPromptEl);
    }

    function removeOverlays() {
        choiceOverlayEl?.remove();
        seekPromptEl?.remove();
        choiceOverlayEl = null;
        seekPromptEl    = null;
        choiceButtonsEl = null;
        countdownBarEl  = null;
    }

    // ================================================================
    // UTILITIES
    // ================================================================

    function sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    // ================================================================
    // BOOTSTRAP
    // ================================================================

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        // DOMContentLoaded already fired (script injected late in body)
        init();
    }

})();
