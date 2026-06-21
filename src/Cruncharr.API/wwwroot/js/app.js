        // API-key support. If the server is started with CRUNCHARR_API_KEY set, every
        // /api/* call must carry the key. We attach it from localStorage and, on a 401,
        // prompt for it once and reload. When no key is configured server-side, nothing
        // here changes behavior.
        (function () {
            const KEY = 'cruncharrApiKey';
            const realFetch = window.fetch.bind(window);
            window.fetch = function (input, init) {
                init = init || {};
                const url = typeof input === 'string' ? input : (input && input.url) || '';
                const isApi = url.indexOf('/api/') !== -1;
                if (isApi) {
                    const k = localStorage.getItem(KEY);
                    if (k) {
                        const headers = new Headers(init.headers || (typeof input !== 'string' && input.headers) || {});
                        if (!headers.has('X-Api-Key')) headers.set('X-Api-Key', k);
                        init.headers = headers;
                    }
                }
                return realFetch(input, init).then(function (res) {
                    // Only treat a 401 as an API-key challenge when the server explicitly
                    // says so via "WWW-Authenticate: ApiKey" (set by the backend key gate).
                    // A failed Crunchyroll login also returns 401 but without this header,
                    // and must surface its real error to the caller instead of triggering
                    // a misleading "server requires an API key" prompt.
                    const challenge = res && res.headers ? (res.headers.get('WWW-Authenticate') || '') : '';
                    if (res && res.status === 401 && isApi && /apikey/i.test(challenge)) {
                        const entered = prompt('This Cruncharr server requires an API key. Enter it:');
                        if (entered) { localStorage.setItem(KEY, entered); location.reload(); }
                    }
                    return res;
                });
            };
        })();

        let currentPage = 'downloads';
        let queueData = [];
        let historyData = [];
        let config = {};
        let authStatus = {};
        let calendarWeekOffset = 0;
        let historyViewMode = 'poster';
        let settingsTab = 'general';
        let selectedEpisodes = new Set();
        let addDownloadSearchResults = [];
        let addDownloadSelectedSeries = null;

        let addDownloadSeriesData = null; // Stores EpisodeAndLanguage data from ListSeriesId
        let addDownloadEpisodeList = []; // Stores EpisodeDisplay list from ListSeriesId
        let selectedEpisodeDubs = new Map(); // episodeKey -> Set of selected dub locales
        let historyRichData = []; // Cache for rich history data with episodes
        let historySearchQuery = '';
        let historySearchPopupOpen = false;
        let isQueueGloballyPaused = false;
        let authIntervalId, historyIntervalId;
        let selectBrowseResultTimeout = null;
        let globalSearchDebounce = null; // top-bar search debounce timer
        let globalSearchAbort = null;    // top-bar search in-flight request
        let globalSearchResults = [];    // last top-bar search results
        let allBrowseSeries = [];        // full series list from /series/all (for client-side dub filter)
        let browseDubFilter = '';        // '' = all languages
        let browseRatingFilter = new Set(); // selected rating tier keys / raw codes; empty = all
        let historyFilterText = '';

        // Constants
        const AUTH_NOTIFICATION_THROTTLE_MS = 30000;
        const AUTH_WARNING_THROTTLE_MS = 60000;
        const HISTORY_POLL_INTERVAL_MS = 5000;
        const AUTH_STATUS_TIMEOUT_MS = 5000;
        const CONFIG_LOAD_TIMEOUT_MS = 10000;
        const SSE_MAX_RETRIES = 10;
        const SSE_BASE_RETRY_DELAY_MS = 3000;
        const SSE_MAX_RETRY_DELAY_MS = 60000;
        const FETCH_CONFIG_RETRY_DELAY_MS = 3000;
        const HISTORY_SEARCH_DEBOUNCE_MS = 200;
        const TOAST_DISPLAY_DURATION_MS = 3000;
        const ACTIVE_DROPDOWN_LISTENERS = new Map(); // id -> listener function

        document.addEventListener('DOMContentLoaded', async () => {
            // Start config load in the background — don't block the UI shell
            const configPromise = fetchConfig();
            // Fetch version from backend (also non-blocking)
            fetch('/api/v1/health').then(res => res.ok ? res.json() : null).then(data => {
                if (data && data.version) {
                    const el = document.getElementById('version-info');
                    if (el) el.textContent = 'v' + String(data.version).split('+')[0];
                }
            }).catch(() => {});
            // Restore last visited page or default to downloads
            let savedPage = 'downloads';
            try {
                savedPage = localStorage.getItem('cruncharr_current_page') || 'downloads';
            } catch (e) {
                console.warn('localStorage unavailable:', e);
            }
            const validPages = ['downloads','add-download','calendar','seasons','history','browse','seasonal','account','settings'];
            if (!validPages.includes(savedPage)) savedPage = 'downloads';
            loadPage(savedPage);
            // Mark nav item active for restored page
            document.querySelectorAll('.nav-item').forEach(i => i.classList.remove('active'));
            const escapedPage = (typeof CSS !== 'undefined' && CSS.escape) ? CSS.escape(savedPage) : savedPage.replace(/(["\\])/g, '\\$1');
            const navItem = document.querySelector(`.nav-item[data-page="${escapedPage}"]`);
            if (navItem) navItem.classList.add('active');
            startPolling();
            setupNavigation();
            setupMultiSelectDropdowns();
            updateTopbarProfile();
            // Remove splash once app shell is rendered
            const splash = document.getElementById('loading-splash');
            if (splash) { splash.classList.add('hidden'); setTimeout(() => splash.remove(), 500); }
            // Await config in background — pages that need it will re-render on arrival
            await configPromise;
        });

        function setupMultiSelectDropdowns() {
            // Use event delegation for dynamically created selects
            document.body.addEventListener('mousedown', function(e) {
                const select = e.target.closest('select[multiple]');
                if (!select) return;
                const option = e.target.closest('option');
                if (!option) return;
                e.preventDefault();
                option.selected = !option.selected;
                select.dispatchEvent(new Event('change', { bubbles: true }));
            });
            document.body.addEventListener('mousemove', function(e) {
                if (e.target.closest('select[multiple]')) e.preventDefault();
            });
        }

        function setupNavigation() {
            document.querySelectorAll('.nav-item').forEach(item => {
                // The "More" button has its own onclick (opens the sheet) and no page.
                if (!item.dataset.page) return;
                item.addEventListener('click', (e) => {
                    e.preventDefault();
                    navigateTo(item.dataset.page);
                });
            });
        }

        function navigateTo(page) {
            localStorage.setItem('cruncharr_current_page', page);
            loadPage(page);
            document.querySelectorAll('.nav-item').forEach(i => i.classList.remove('active'));
            const navItem = document.querySelector(`.nav-item[data-page="${page}"]`);
            if (navItem) navItem.classList.add('active');
            // On mobile, secondary pages live behind "More"; keep that cell lit so the
            // bottom bar still shows where you are.
            else {
                const more = document.querySelector('.nav-more');
                if (more) more.classList.add('active');
            }
            closeMoreSheet();
        }

        // Mobile "More" bottom sheet (Seasons / Browse / Seasonal / Account).
        function toggleMoreSheet(event) {
            if (event) event.preventDefault();
            const sheet = document.getElementById('more-sheet');
            if (sheet) sheet.classList.toggle('open');
        }
        function closeMoreSheet() {
            const sheet = document.getElementById('more-sheet');
            if (sheet) sheet.classList.remove('open');
        }

        function loadPage(page) {
            // Keep the top-right profile chip in sync (auth may not have been ready on
            // first load); cheap local call, refreshes on every navigation.
            if (typeof updateTopbarProfile === 'function') updateTopbarProfile();
            // Clear history polling when navigating away from history page
            if (currentPage === 'history' && page !== 'history' && historyIntervalId) {
                clearInterval(historyIntervalId);
                historyIntervalId = null;
            }
            // Clear any pending browse result timeout
            if (selectBrowseResultTimeout) {
                clearTimeout(selectBrowseResultTimeout);
                selectBrowseResultTimeout = null;
            }
            // Close history search popup if open
            if (historySearchPopupOpen) closeHistorySearchPopup();
            currentPage = page;
            const content = document.getElementById('content');
            switch(page) {
                case 'downloads': renderDownloads(content); break;
                case 'add-download': renderAddDownload(content); break;
                case 'calendar': renderCalendar(content); break;
                case 'seasons': renderSeasons(content); break;
                case 'history': renderHistory(content); break;
                case 'browse': renderBrowse(content); break;
                case 'seasonal': renderSeasonal(content); break;
                case 'account': renderAccount(content); break;
                case 'settings': renderSettings(content); break;
                default:
                    console.warn('Unknown page:', page);
                    loadPage('downloads');
                    break;
            }
        }

        // ================== DOWNLOADS ==================
        function renderDownloads(container) {
            const removeFinished = config?.general?.removeFinishedDownload || false;
            const autoDownload = config?.queue?.autoDownload || false;
            
            container.innerHTML = `
                <div class="page-title">Downloads</div>
                <div class="page-subtitle">Manage your download queue</div>
                
                <!-- Queue Stats -->
                <div style="display:grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap:10px; margin-bottom:20px;">
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--accent-blue);" id="stat-total">-</div>
                        <div class="hint">Total</div>
                    </div>
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--accent-orange);" id="stat-active">-</div>
                        <div class="hint">Active</div>
                    </div>
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--accent-yellow);" id="stat-queued">-</div>
                        <div class="hint">Queued</div>
                    </div>
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--accent-green);" id="stat-completed">-</div>
                        <div class="hint">Completed</div>
                    </div>
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--accent-red);" id="stat-failed">-</div>
                        <div class="hint">Failed</div>
                    </div>
                    <div class="card stat-tile">
                        <div class="stat-num" style="color:var(--text-muted);" id="stat-retry">-</div>
                        <div class="hint">Retrying</div>
                    </div>
                </div>
                
                <div style="display:flex; gap:15px; align-items:center; flex-wrap:wrap; margin-bottom:20px;">
                    <label class="toggle-switch">
                        <input type="checkbox" id="toggle-remove-finished" ${removeFinished ? 'checked' : ''} onchange="toggleSetting('removeFinished', this.checked)">
                        <span class="toggle-slider"></span>
                        Remove Finished
                    </label>
                    <label class="toggle-switch">
                        <input type="checkbox" id="toggle-auto-download" ${autoDownload ? 'checked' : ''} onchange="toggleSetting('autoDownload', this.checked)">
                        <span class="toggle-slider"></span>
                        Auto Download
                    </label>
                    <span id="scheduler-status" class="hint" title="Auto-download scheduler" style="display:flex; align-items:center; gap:6px;">
                        <span id="scheduler-dot" style="width:8px; height:8px; border-radius:50%; background:var(--text-muted); display:inline-block;"></span>
                        Scheduler: <span id="scheduler-state">—</span>
                    </span>
                    <button class="btn-icon" id="btn-scheduler-run" onclick="triggerScheduler()" title="Run the auto-download check now">
                        &#9889; Run now
                    </button>

                    <div style="margin-left:auto; display:flex; gap:8px;">
                        <button class="btn-icon" id="btn-global-pause" onclick="toggleGlobalPause()" title="Pause/Resume Queue" style="display:none;">
                            &#10074;&#10074; Pause All
                        </button>
                        <button class="btn-icon" id="btn-retry-failed" onclick="retryFailed()" title="Retry Failed" disabled>
                            &#8635; Retry
                        </button>
                        <button class="btn-icon" id="btn-pause-running" onclick="pauseRunning()" title="Pause Running" disabled>
                            &#10074;&#10074; Pause
                        </button>
                        <button class="btn-icon danger" onclick="clearQueue()" title="Clear Queue">
                            &#128465; Clear
                        </button>
                    </div>
                </div>
                <div id="downloads-list">
                    <div class="loading"><div class="spinner"></div>Loading queue...</div>
                </div>
            `;
            fetchDownloads();
            fetchQueueStats();
            loadSchedulerStatus();
        }

        async function loadSchedulerStatus() {
            const stateEl = document.getElementById('scheduler-state');
            const dotEl = document.getElementById('scheduler-dot');
            if (!stateEl) return;
            try {
                const res = await fetch('/api/v1/scheduler/status');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const d = await res.json();
                const running = d.isRunning ?? d.IsRunning ?? false;
                const lastRun = d.lastRun ?? d.LastRun ?? null;
                let label = running ? 'running…' : 'idle';
                if (!running && lastRun) {
                    const dt = new Date(lastRun);
                    if (!isNaN(dt)) label = `idle (last run ${dt.toLocaleTimeString()})`;
                }
                stateEl.textContent = label;
                if (dotEl) dotEl.style.background = running ? 'var(--accent-green)' : 'var(--text-muted)';
            } catch (e) {
                stateEl.textContent = 'unavailable';
                if (dotEl) dotEl.style.background = 'var(--accent-red)';
            }
        }

        async function triggerScheduler() {
            const btn = document.getElementById('btn-scheduler-run');
            if (btn) { btn.disabled = true; btn.innerHTML = '&#9889; Running…'; }
            try {
                const res = await fetch('/api/v1/scheduler/trigger', { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                if (typeof showToast === 'function') showToast('Auto-download check started', 'success');
            } catch (e) {
                if (typeof showToast === 'function') showToast('Scheduler trigger failed', 'error');
            } finally {
                if (btn) { btn.disabled = false; btn.innerHTML = '&#9889; Run now'; }
                setTimeout(() => { loadSchedulerStatus(); fetchDownloads(); fetchQueueStats(); }, 1500);
            }
        }

        async function fetchQueueStats() {
            try {
                const res = await fetch('/api/v1/queue/stats');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                const statTotal = document.getElementById('stat-total');
                const statActive = document.getElementById('stat-active');
                const statQueued = document.getElementById('stat-queued');
                const statCompleted = document.getElementById('stat-completed');
                const statFailed = document.getElementById('stat-failed');
                const statRetry = document.getElementById('stat-retry');
                if (statTotal) statTotal.textContent = data.total ?? 0;
                if (statActive) statActive.textContent = data.active ?? 0;
                if (statQueued) statQueued.textContent = data.queued ?? 0;
                if (statCompleted) statCompleted.textContent = data.completed ?? 0;
                if (statFailed) statFailed.textContent = data.failed ?? 0;
                if (statRetry) statRetry.textContent = data.waitingForRetry ?? 0;
                
                // Update global pause button
                isQueueGloballyPaused = data.isGloballyPaused || false;
                const pauseBtn = document.getElementById('btn-global-pause');
                if (pauseBtn) {
                    pauseBtn.style.display = 'inline-flex';
                    if (isQueueGloballyPaused) {
                        pauseBtn.innerHTML = '&#9654; Resume All';
                        pauseBtn.title = 'Resume Queue';
                    } else {
                        pauseBtn.innerHTML = '&#10074;&#10074; Pause All';
                        pauseBtn.title = 'Pause Queue';
                    }
                }
            } catch (e) {
                console.error('Failed to fetch queue stats:', e);
            }
        }

        let lastAuthNotification = 0;
        
        async function fetchDownloads() {
            try {
                const res = await fetch('/api/v1/queue');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                updateQueueData(data.items || []);
            } catch (e) {
                console.error('Failed to fetch downloads:', e);
            }
        }
        
        function updateQueueData(items) {
            queueData = items;
            const list = document.getElementById('downloads-list');
            if (!list) return;

                // Check for auth/subscription errors and show notifications
                const authErrors = queueData.filter(i => {
                    const doing = (i.downloadProgress?.doing || '').toLowerCase();
                    return doing.includes('not logged in') || 
                           doing.includes('subscription expired') ||
                           doing.includes('premium content') ||
                           doing.includes('subscription required');
                });
                
                if (authErrors.length > 0) {
                    const now = Date.now();
                    if (now - lastAuthNotification > AUTH_NOTIFICATION_THROTTLE_MS) {
                        lastAuthNotification = now;
                        const error = authErrors[0];
                        const doing = error.downloadProgress?.doing || '';
                        if (doing.includes('Not logged in')) {
                            showToast('You are not logged in. Please go to Account tab and log in.', 'error');
                        } else if (doing.includes('Subscription expired')) {
                            showToast('Your Crunchyroll subscription has expired. Please renew it.', 'error');
                        } else if (doing.includes('Premium content') || doing.includes('subscription required')) {
                            showToast('Premium subscription required for this content.', 'error');
                        }
                    }
                }

                const hasFailed = queueData.some(i => (i.downloadProgress?.state || '').toLowerCase() === 'error');
                const hasActive = queueData.some(i => (i.downloadProgress?.state || '').toLowerCase() === 'downloading');
                const retryBtn = document.getElementById('btn-retry-failed');
                const pauseBtn = document.getElementById('btn-pause-running');
                if (retryBtn) retryBtn.disabled = !hasFailed;
                if (pauseBtn) pauseBtn.disabled = !hasActive;

                if (queueData.length === 0) {
                    list.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-icon">&#128229;</div>
                            <div class="empty-state-title">Queue is empty</div>
                            <div>Add episodes to start downloading</div>
                        </div>`;
                    return;
                }

                list.innerHTML = queueData.map(item => {
                    const state = (item.downloadProgress?.state || 'queued').toLowerCase();
                    const isError = state === 'error';
                    const isDownloading = state === 'downloading';
                    const isPaused = state === 'paused';
                    const isDone = state === 'done';
                    return `
                        <div class="download-item">
                            <div class="download-thumb">
                                ${item.episode?.thumbnailUrl && isSafeUrl(item.episode.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(item.episode.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                            </div>
                            <div class="download-content">
                                <div class="download-header">
                                    <div style="min-width:0;">
                                        <div class="download-title ${item.episode?.highlightAllAvailable ? 'highlight' : ''}">${escapeHtml(item.episode?.title) || 'Unknown'}</div>
                                        <div class="download-info" title="${escapeHtmlAttribute(item.infoText || item.infoTextHover || item.episode?.description || '')}">${escapeHtml(item.infoText || item.episode?.seriesTitle || '')}</div>
                                    </div>
                                    <div class="download-actions">
                                        ${state === 'queued' || state === 'waitingforretry'
                                            ? `<button class="btn-icon primary" onclick="startDownload('${escapeJsString(item.id)}')" title="Start">&#9654;</button>`
                                            : isError
                                                ? `<button class="btn-icon" onclick="retryDownload('${escapeJsString(item.id)}')" title="Retry">&#8635;</button>`
                                                : `<button class="btn-icon" onclick="togglePauseResume('${escapeJsString(item.id)}', ${isDownloading})" title="${isDownloading ? 'Pause' : 'Resume'}">${isDownloading ? '&#10074;&#10074;' : '&#9654;'}</button>`
                                        }
                                        <button class="btn-icon danger" onclick="removeFromQueue('${escapeJsString(item.id)}')" title="Remove">&#128465;</button>
                                    </div>
                                </div>
                                <div class="progress-container">
                                    <div class="progress-bar ${isDone ? 'complete' : ''} ${isError ? 'error' : ''}" style="width: ${isDone ? 100 : (item.downloadProgress?.percent || 0)}%"></div>
                                </div>
                                <div class="download-footer">
                                    <span>${getDoingText(item.downloadProgress)}</span>
                                    <span>${formatETA(item.downloadProgress?.time)}</span>
                                    <span>${formatDownloadSpeed(item.downloadProgress?.downloadSpeedBytes)}</span>
                                </div>
                            </div>
                        </div>
                    `;
                }).join('');
        }

        // ================== ADD DOWNLOAD ==================
        function renderAddDownload(container) {
            container.innerHTML = `
                <div class="page-title">Add Download</div>
                <div class="page-subtitle">Search and add episodes to the queue</div>
                <div style="position:relative; margin-bottom:10px;">
                    <div class="header-search" style="width:100%;">
                        <span>&#128269;</span>
                        <input type="text" id="add-search-input" placeholder="Enter series or episode URL..." oninput="onAddSearchInput(this.value)">
                    </div>
                    <div class="search-popup" id="search-popup"></div>
                </div>
                <div class="season-selector">
                    <button class="header-btn primary" id="add-btn" onclick="addSelectedToQueue()" disabled>Add</button>
                    <label class="checkbox-label">
                        <input type="checkbox" id="add-all-checkbox" onchange="toggleAddAll(this.checked)">
                        All
                    </label>
                    <button class="header-btn" id="music-btn" onclick="showFeaturedMusic()" style="display:none;">
                        <span>&#127925;</span> Music
                    </button>
                    <div style="margin-left:auto;">
                        <select class="form-select mw-200" id="season-dropdown" onchange="onSeasonChange(this.value)">
                            <option>Select Season</option>
                        </select>
                    </div>
                </div>
                <div id="add-episodes-list">
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128270;</div>
                        <div class="empty-state-title">Search for a series</div>
                        <div>Type a series name or URL to find episodes</div>
                    </div>
                </div>
            `;
        }

        let searchDebounce;
        let searchAbortController = null;
        function onAddSearchInput(value) {
            clearTimeout(searchDebounce);
            const popup = document.getElementById('search-popup');
            if (!value.trim()) {
                if (popup) { popup.innerHTML = ''; popup.style.display = 'none'; }
                return;
            }
            searchDebounce = setTimeout(() => doAddSearch(value), 400);
        }

        async function doAddSearch(query) {
            const popup = document.getElementById('search-popup');
            if (!popup) return;
            if (searchAbortController) {
                searchAbortController.abort();
            }
            const controller = new AbortController();
            searchAbortController = controller;
            try {
                const res = await fetch(`/api/v1/series/search?query=${encodeURIComponent(query)}`, {
                    signal: controller.signal
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const results = await res.json();
                addDownloadSearchResults = results || [];
                if (addDownloadSearchResults.length === 0) {
                    popup.innerHTML = '<div style="padding:15px; color:var(--text-muted);">No results found</div>';
                    popup.style.display = 'block';
                    return;
                }
                popup.innerHTML = addDownloadSearchResults.map(s => `
                    <div class="search-result-item" onclick="selectSearchResult('${escapeJsString(s.id)}')">
                        <div class="search-result-poster">
                            ${(s.coverArtUrl || s.thumbnailUrl) && isSafeUrl(s.coverArtUrl || s.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(s.coverArtUrl || s.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                        </div>
                        <div class="search-result-info">
                            <div class="search-result-title">${escapeHtml(s.title)}</div>
                            <div class="search-result-desc">${escapeHtml(s.description || '')}</div>
                        </div>
                    </div>
                `).join('');
                popup.style.display = 'block';
            } catch (e) {
                if (e.name === 'AbortError') return;
                if (!popup) return;
                popup.innerHTML = '<div style="padding:15px; color:var(--accent-red);">Search failed</div>';
                popup.style.display = 'block';
            } finally {
                if (searchAbortController === controller) {
                    searchAbortController = null;
                }
            }
        }

        async function selectSearchResult(seriesId) {
            const searchPopup = document.getElementById('search-popup');
            if (searchPopup) searchPopup.style.display = 'none';
            const series = addDownloadSearchResults.find(s => s.id === seriesId);
            addDownloadSelectedSeries = series;
            if (!series) return;
            const searchInput = document.getElementById('add-search-input');
            if (searchInput) searchInput.value = series.title;
            const musicBtn = document.getElementById('music-btn');
            if (musicBtn) musicBtn.style.display = 'inline-flex';
            // Show loading spinner while fetching episodes
            const listContainer = document.getElementById('add-episodes-list');
            if (listContainer) {
                listContainer.innerHTML = '<div class="loading"><div class="spinner"></div>Loading episodes...</div>';
            }
            try {
                // Use ListSeriesId endpoint to get episodes with dub version info
                const dubLangs = config?.download?.dubLanguages || ['ja-JP'];
                const dubLangParam = dubLangs.map(l => `dubLang=${encodeURIComponent(l)}`).join('&');
                const res = await fetch(`/api/v1/series/${seriesId}/list?${dubLangParam}`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const result = await res.json();
                
                if (!result || !result.list) {
                    showToast('No episodes found for this series', 'error');
                    return;
                }
                
                addDownloadEpisodeList = result.list || [];
                addDownloadSeriesData = result.data || {};
                
                // Extract unique seasons from episodes
                const seasonMap = new Map();
                addDownloadEpisodeList.forEach(ep => {
                    const seasonId = ep.id; // EpisodeDisplay.Id is the seasonId
                    if (seasonId && !seasonMap.has(seasonId)) {
                        seasonMap.set(seasonId, {
                            id: seasonId,
                            title: ep.seasonTitle || `Season ${ep.season}`
                        });
                    }
                });
                const seasons = Array.from(seasonMap.values());
                
                // Initialize default dub selections from config
                selectedEpisodeDubs.clear();
                const defaultDubs = new Set(dubLangs);
                addDownloadEpisodeList.forEach(ep => {
                    const epKey = ep.e; // Episode key like "E1", "SP1", etc.
                    const epData = addDownloadSeriesData[epKey];
                    if (epData && epData.variants) {
                        const availableDubs = new Set(epData.variants.map(v => v.lang?.crLocale || v.item?.audioLocale));
                        // Intersection of config dubs and available dubs
                        const selected = new Set([...defaultDubs].filter(d => availableDubs.has(d)));
                        if (selected.size > 0) {
                            selectedEpisodeDubs.set(epKey, selected);
                        }
                    }
                });
                
                const dropdown = document.getElementById('season-dropdown');
                if (!dropdown) return;
                dropdown.innerHTML = seasons.map(season => `<option value="${escapeHtmlAttribute(season.id)}">${escapeHtml(season.title)}</option>`).join('');
                if (seasons.length > 0) {
                    dropdown.value = seasons[0].id;
                    onSeasonChange(seasons[0].id);
                } else {
                    // No seasons found, show all episodes
                    selectedEpisodes.clear();
                    renderAddEpisodesMultiDub();
                }
            } catch (e) {
                console.error('Failed to load series episodes:', e);
                showToast('Failed to load episodes', 'error');
                const listContainer = document.getElementById('add-episodes-list');
                if (listContainer) {
                    listContainer.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-icon">❌</div>
                            <div class="empty-state-title">Failed to load episodes</div>
                            <div>Please try again</div>
                        </div>
                    `;
                }
            }
        }

        // ===== Top-bar global search (Seerr-style) =====
        function onGlobalSearchInput(value) {
            clearTimeout(globalSearchDebounce);
            const popup = document.getElementById('global-search-popup');
            if (!value.trim()) {
                if (popup) { popup.innerHTML = ''; popup.style.display = 'none'; }
                return;
            }
            globalSearchDebounce = setTimeout(() => doGlobalSearch(value.trim()), 350);
        }

        function onGlobalSearchEnter() {
            const input = document.getElementById('global-search');
            if (input && input.value.trim()) doGlobalSearch(input.value.trim());
        }

        async function doGlobalSearch(query) {
            const popup = document.getElementById('global-search-popup');
            if (!popup) return;
            if (globalSearchAbort) globalSearchAbort.abort();
            const controller = new AbortController();
            globalSearchAbort = controller;
            popup.innerHTML = '<div style="padding:14px; color:var(--text-muted);">Searching…</div>';
            popup.style.display = 'block';
            try {
                const res = await fetch(`/api/v1/series/search?query=${encodeURIComponent(query)}`, { signal: controller.signal });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                globalSearchResults = (await res.json()) || [];
                if (globalSearchResults.length === 0) {
                    popup.innerHTML = '<div style="padding:14px; color:var(--text-muted);">No results found</div>';
                    return;
                }
                popup.innerHTML = globalSearchResults.map(s => `
                    <div class="search-result-item" onclick="selectGlobalResult('${escapeJsString(s.id)}')">
                        <div class="search-result-poster">
                            ${(s.coverArtUrl || s.thumbnailUrl) && isSafeUrl(s.coverArtUrl || s.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(s.coverArtUrl || s.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                        </div>
                        <div class="search-result-info">
                            <div class="search-result-title">${escapeHtml(s.title)}</div>
                            <div class="search-result-desc">${escapeHtml(s.description || '')}</div>
                        </div>
                    </div>
                `).join('');
            } catch (e) {
                if (e.name === 'AbortError') return;
                popup.innerHTML = '<div style="padding:14px; color:var(--accent-red);">Search failed</div>';
            } finally {
                if (globalSearchAbort === controller) globalSearchAbort = null;
            }
        }

        function selectGlobalResult(seriesId) {
            const popup = document.getElementById('global-search-popup');
            if (popup) { popup.style.display = 'none'; popup.innerHTML = ''; }
            const input = document.getElementById('global-search');
            if (input) input.value = '';
            // Reuse the Browse flow: navigate to Add Download and load the series' episodes.
            selectBrowseResult(seriesId);
        }

        // Close the global search dropdown when clicking outside it.
        document.addEventListener('click', (e) => {
            const wrap = document.querySelector('.global-search');
            const popup = document.getElementById('global-search-popup');
            if (wrap && popup && !wrap.contains(e.target)) {
                popup.style.display = 'none';
            }
        });

        async function onSeasonChange(seasonId) {
            if (!seasonId || !addDownloadSelectedSeries) return;
            // Filter episodes by selected season
            const filtered = addDownloadEpisodeList.filter(ep => ep.id === seasonId);
            selectedEpisodes.clear();
            renderAddEpisodesMultiDub(filtered);
        }

        function renderAddEpisodesMultiDub(episodesToRender) {
            const list = document.getElementById('add-episodes-list');
            const allCheckbox = document.getElementById('add-all-checkbox');
            const addBtn = document.getElementById('add-btn');
            if (!list) return;
            const seasonId = document.getElementById('season-dropdown')?.value;
            const episodes = episodesToRender || (seasonId ? addDownloadEpisodeList.filter(ep => ep.id === seasonId) : addDownloadEpisodeList);
            const allChecked = selectedEpisodes.size === episodes.length && episodes.length > 0;
            if (allCheckbox) allCheckbox.checked = allChecked;
            if (addBtn) addBtn.disabled = selectedEpisodes.size === 0;

            if (episodes.length === 0) {
                list.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128270;</div>
                        <div class="empty-state-title">No episodes found</div>
                    </div>`;
                return;
            }

            // Season-level language bar: pick a language once and apply it to every
            // episode that offers it (toggles off if already applied to all).
            const seasonLocaleNames = new Map();
            const seasonLocaleEps = new Map();
            episodes.forEach(ep => {
                const d = addDownloadSeriesData[ep.e];
                if (!d || !d.variants) return;
                d.variants.forEach(v => {
                    const loc = v.lang?.crLocale || v.item?.audioLocale;
                    if (!loc) return;
                    if (!seasonLocaleNames.has(loc)) seasonLocaleNames.set(loc, v.lang?.name || loc);
                    if (!seasonLocaleEps.has(loc)) seasonLocaleEps.set(loc, new Set());
                    seasonLocaleEps.get(loc).add(ep.e);
                });
            });
            let seasonDubBarHtml = '';
            if (seasonLocaleNames.size > 0 && episodes.length > 1) {
                const chips = Array.from(seasonLocaleNames.entries()).map(([loc, name]) => {
                    const eps = Array.from(seasonLocaleEps.get(loc) || []);
                    const allOn = eps.length > 0 && eps.every(k => selectedEpisodeDubs.get(k)?.has(loc));
                    return `<div class="dub-option ${allOn ? 'selected' : ''}" onclick="applySeasonDub('${escapeJsString(loc)}')">${escapeHtml(name)}</div>`;
                }).join('');
                seasonDubBarHtml = `
                    <div class="season-dub-bar">
                        <span class="season-dub-label">Apply language to all episodes</span>
                        <div class="season-dub-chips">${chips}</div>
                    </div>`;
            }

            list.innerHTML = seasonDubBarHtml + episodes.map(ep => {
                const epKey = ep.e;
                const isSelected = selectedEpisodes.has(epKey);
                const epData = addDownloadSeriesData[epKey];
                const selectedDubs = selectedEpisodeDubs.get(epKey) || new Set();
                
                // Build dub language options
                let dubOptionsHtml = '';
                if (epData && epData.variants && epData.variants.length > 0) {
                    const dubMap = new Map();
                    epData.variants.forEach(v => {
                        const locale = v.lang?.crLocale || v.item?.audioLocale || 'unknown';
                        const name = v.lang?.name || locale;
                        const isPremium = v.item?.isPremiumOnly || false;
                        if (!dubMap.has(locale)) {
                            dubMap.set(locale, { name, isPremium });
                        }
                    });
                    
                    dubOptionsHtml = Array.from(dubMap.entries()).map(([locale, info]) => {
                        const isSelected = selectedDubs.has(locale);
                        const premiumClass = info.isPremium ? 'premium' : '';
                        return `
                            <div class="dub-option ${isSelected ? 'selected' : ''} ${premiumClass}" onclick="event.stopPropagation(); toggleDubSelection('${escapeJsString(epKey)}', '${escapeJsString(locale)}')">
                                ${escapeHtml(info.name)}
                            </div>
                        `;
                    }).join('');
                }
                
                return `
                    <div class="episode-multi-dub ${isSelected ? 'selected' : ''}" onclick="toggleEpisodeSelectionMultiDub('${escapeJsString(epKey)}')">
                        <div class="episode-thumb">
                            ${ep.img && isSafeUrl(ep.img) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(ep.img))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                        </div>
                        <div class="episode-info">
                            <div class="episode-title">${escapeHtml(ep.name) || 'Unknown Episode'}</div>
                            <div class="episode-meta">${escapeHtml(ep.seasonTitle || '')}${ep.time ? ' - ' + escapeHtml(ep.time) : ''}</div>
                            ${ep.description ? `<div class="episode-desc" style="font-size:0.85em; color:var(--text-secondary); margin-bottom:6px;">${escapeHtml(ep.description)}</div>` : ''}
                            <div class="dub-section-label">Select Dubs:</div>
                            <div class="dub-selector">
                                ${dubOptionsHtml || '<span style="color:var(--text-muted); font-size:0.8em;">No dubs available</span>'}
                            </div>
                        </div>
                        <div style="display:flex; align-items:flex-start; padding-top:4px;">
                            <input type="checkbox" ${isSelected ? 'checked' : ''} onclick="event.stopPropagation(); toggleEpisodeSelectionMultiDub('${escapeJsString(epKey)}')">
                        </div>
                    </div>
                `;
            }).join('');
        }

        function toggleEpisodeSelectionMultiDub(epKey) {
            if (selectedEpisodes.has(epKey)) {
                selectedEpisodes.delete(epKey);
            } else {
                selectedEpisodes.add(epKey);
                // If no dubs selected for this episode, select all available dubs
                if (!selectedEpisodeDubs.has(epKey)) {
                    const epData = addDownloadSeriesData[epKey];
                    if (epData && epData.variants) {
                        const allDubs = new Set(epData.variants.map(v => v.lang?.crLocale || v.item?.audioLocale).filter(Boolean));
                        if (allDubs.size > 0) {
                            selectedEpisodeDubs.set(epKey, allDubs);
                        }
                    }
                }
            }
            renderAddEpisodesMultiDub();
        }

        function toggleDubSelection(epKey, locale) {
            const selectedDubs = selectedEpisodeDubs.get(epKey) || new Set();
            if (selectedDubs.has(locale)) {
                selectedDubs.delete(locale);
                if (selectedDubs.size === 0) {
                    selectedEpisodeDubs.delete(epKey);
                    selectedEpisodes.delete(epKey);
                } else {
                    selectedEpisodeDubs.set(epKey, selectedDubs);
                }
            } else {
                selectedDubs.add(locale);
                selectedEpisodeDubs.set(epKey, selectedDubs);
                // Auto-select episode if at least one dub is selected
                if (!selectedEpisodes.has(epKey)) {
                    selectedEpisodes.add(epKey);
                }
            }
            renderAddEpisodesMultiDub();
        }

        // Apply (or remove) one dub language across every visible episode that offers it.
        function applySeasonDub(locale) {
            const seasonId = document.getElementById('season-dropdown')?.value;
            const episodes = seasonId
                ? addDownloadEpisodeList.filter(ep => ep.id === seasonId)
                : addDownloadEpisodeList;
            const epsWith = episodes.filter(ep => {
                const d = addDownloadSeriesData[ep.e];
                return d?.variants?.some(v => (v.lang?.crLocale || v.item?.audioLocale) === locale);
            });
            if (epsWith.length === 0) return;
            const allOn = epsWith.every(ep => selectedEpisodeDubs.get(ep.e)?.has(locale));
            epsWith.forEach(ep => {
                const set = selectedEpisodeDubs.get(ep.e) || new Set();
                if (allOn) {
                    set.delete(locale);
                    if (set.size === 0) { selectedEpisodeDubs.delete(ep.e); selectedEpisodes.delete(ep.e); }
                    else selectedEpisodeDubs.set(ep.e, set);
                } else {
                    set.add(locale);
                    selectedEpisodeDubs.set(ep.e, set);
                    selectedEpisodes.add(ep.e);
                }
            });
            renderAddEpisodesMultiDub();
        }

        function toggleAddAll(checked) {
            // Only act on the episodes currently displayed (season filter applied),
            // otherwise hidden episodes from other seasons get queued too
            const currentSeasonId = document.getElementById('season-dropdown')?.value;
            const visibleEpisodes = currentSeasonId
                ? addDownloadEpisodeList.filter(ep => ep.id === currentSeasonId)
                : addDownloadEpisodeList;
            if (checked) {
                visibleEpisodes.forEach(ep => {
                    const epKey = ep.e;
                    selectedEpisodes.add(epKey);
                    // Select all available dubs
                    const epData = addDownloadSeriesData[epKey];
                    if (epData && epData.variants) {
                        const allDubs = new Set(epData.variants.map(v => v.lang?.crLocale || v.item?.audioLocale).filter(Boolean));
                        if (allDubs.size > 0) {
                            selectedEpisodeDubs.set(epKey, allDubs);
                        }
                    }
                });
            } else {
                selectedEpisodes.clear();
                selectedEpisodeDubs.clear();
            }
            renderAddEpisodesMultiDub();
        }

        let isAddingToQueue = false;
        async function addSelectedToQueue() {
            if (isAddingToQueue) return;
            const epKeys = Array.from(selectedEpisodes);
            if (epKeys.length === 0) return;
            
            isAddingToQueue = true;
            try {
                // Build the episodes dictionary for ItemSelectMultiDub
                const episodes = {};
                const selectedDubLangs = new Set();
                
                epKeys.forEach(epKey => {
                    if (addDownloadSeriesData[epKey]) {
                        episodes[epKey] = addDownloadSeriesData[epKey];
                        // Collect selected dubs
                        const dubs = selectedEpisodeDubs.get(epKey);
                        if (dubs) {
                            dubs.forEach(d => selectedDubLangs.add(d));
                        }
                    }
                });
                
                if (Object.keys(episodes).length === 0) {
                    showToast('No valid episodes selected', 'error');
                    return;
                }
                
                // Call ItemSelectMultiDub to construct proper queue items
                const dubLangArray = Array.from(selectedDubLangs);
                if (dubLangArray.length === 0) {
                    dubLangArray.push('ja-JP'); // Default fallback
                }
                
                const res = await fetch('/api/v1/series/item-select-multi-dub', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        episodes: episodes,
                        dubLang: dubLangArray,
                        all: false,
                        e: epKeys
                    })
                });
                
                if (!res.ok) {
                    const err = await res.json().catch(() => ({}));
                    throw new Error(err.message || 'ItemSelectMultiDub failed');
                }
                
                const queueItems = await res.json();
                
                // Add each queue item to the queue
                let added = 0;
                const markAsWatched = config?.crunchyroll?.markAsWatched || false;
                for (const [key, item] of Object.entries(queueItems)) {
                    if (item && item.episodeId) {
                        const queueRes = await fetch('/api/v1/queue', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                episodeId: item.episodeId,
                                title: item.episodeTitle || item.title || '',
                                seriesTitle: item.seriesTitle || addDownloadSelectedSeries?.title || '',
                                seasonNumber: item.season || 1,
                                episodeNumber: item.episodeNumber || 1,
                                locale: item.selectedDubs?.[0] || 'ja-JP',
                                thumbnailUrl: item.image || '',
                                coverArtUrl: addDownloadSelectedSeries?.coverArtUrl || '',
                                selectedDubs: item.selectedDubs || [],
                                selectedSubs: item.downloadSubs || [],
                                hslang: item.hslang || 'none',
                                videoQuality: item.videoQuality || 'best',
                                versions: item.data?.map(v => ({
                                    audioLocale: v.lang?.crLocale || v.lang?.locale || '',
                                    guid: v.mediaId || '',
                                    mediaGuid: v.mediaId || '',
                                    original: v.versions?.some(ver => ver.original) || false,
                                    seasonGuid: v.versions?.[0]?.seasonGuid || ''
                                })) || []
                            })
                        });
                        if (queueRes.ok) {
                            added++;
                            // Mark as watched if enabled
                            if (markAsWatched && item.data?.[0]?.mediaId) {
                                try {
                                    await fetch(`/api/v1/series/episodes/${item.data[0].mediaId}/mark-watched`, { method: 'POST' });
                                } catch (e) {
                                    console.warn('Failed to mark as watched:', e);
                                }
                            }
                        } else {
                            console.warn('Failed to add episode to queue:', key, queueRes.status);
                        }
                    }
                }
                
                showToast(`Added ${added} episode(s) to queue`, 'success');
                selectedEpisodes.clear();
                selectedEpisodeDubs.clear();
                renderAddEpisodesMultiDub();
            } catch (e) {
                console.error('Failed to add to queue:', e);
                showToast('Failed to add to queue: ' + e.message, 'error');
            } finally {
                isAddingToQueue = false;
            }
        }

        async function showFeaturedMusic() {
            if (!addDownloadSelectedSeries) return;
            try {
                const res = await fetch(`/api/v1/music/featured/${addDownloadSelectedSeries.id}`);
                if (!res.ok) throw new Error('Failed to fetch');
                const videos = await res.json();
                
                if (!videos || videos.length === 0) {
                    showToast('No featured music videos found for this series', 'info');
                    return;
                }
                
                const modalTitle = document.getElementById('modal-title');
                const modalBody = document.getElementById('modal-body');
                const modalFooter = document.getElementById('modal-footer');
                const modalEl = document.getElementById('modal');
                if (modalTitle) modalTitle.textContent = 'Featured Music Videos';
                if (modalBody) modalBody.innerHTML = `
                    <div style="max-height: 400px; overflow-y: auto;">
                        ${videos.map(video => {
                            const thumb = video.images?.thumbnail?.[0]?.[0]?.source || video.images?.thumbnail?.[0]?.source;
                            return `
                            <div style="padding: 12px; border-bottom: 1px solid var(--border-color); display: flex; align-items: center; gap: 12px;">
                                <div style="width: 80px; height: 45px; background: var(--bg-tertiary); border-radius: 4px; display: flex; align-items: center; justify-content: center; overflow: hidden;">
                                    ${thumb && isSafeUrl(thumb) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(thumb))}" style="width: 100%; height: 100%; object-fit: cover;" alt="" onerror="this.outerHTML='🎵'">` : '🎵'}
                                </div>
                                <div style="flex: 1;">
                                    <div style="font-weight: 500;">${escapeHtml(video.title || 'Unknown')}</div>
                                    <div class="hint">${escapeHtml(video.episode_type || 'Music Video')}</div>
                                </div>
                                <button class="header-btn" onclick="addMusicVideoToQueue('${escapeJsString(video.id)}', '${escapeJsString(video.title || '')}')">Add</button>
                            </div>
                        `}).join('')}
                    </div>
                `;
                if (modalFooter) modalFooter.innerHTML = `
                    <button class="header-btn" onclick="closeModal()">Close</button>
                `;
                if (modalEl) modalEl.classList.add('active');
            } catch (e) {
                showToast('Failed to load featured music videos', 'error');
            }
        }
        
        async function addMusicVideoToQueue(videoId, title) {
            try {
                const res = await fetch('/api/v1/queue', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        episodeId: videoId,
                        title: title || 'Music Video',
                        seriesTitle: 'Music Video',
                        episodeNumber: 1,
                        seasonNumber: 1,
                        locale: 'ja-JP'
                    })
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Added music video to queue', 'success');
            } catch (e) {
                showToast('Failed to add to queue', 'error');
            }
        }

        // ================== CALENDAR ==================
        function renderCalendar(container) {
            container.innerHTML = `
                <div class="page-title">Calendar</div>
                <div class="page-subtitle">Upcoming episode releases</div>
                <div class="calendar-controls" style="display:flex; justify-content:space-between; align-items:center; margin-bottom:20px;">
                    <button class="header-btn" onclick="changeWeek(-1)">&#9664; Prev</button>
                    <div style="display:flex; gap:10px; align-items:center;">
                        <button class="header-btn" onclick="fetchCalendar(true)">&#128260; Refresh</button>
                        <select class="form-select mw-170" id="calendar-dub-filter" onchange="onCalendarDubFilterChange(this.value)" title="Filter by audio (dub) language">
                            <option value="none">All Languages</option>
                            ${LANG_OPTIONS.map(o=>`<option value="${o.value}">${o.label}</option>`).join('')}
                        </select>
                    </div>
                    <button class="header-btn" onclick="changeWeek(1)">Next &#9654;</button>
                </div>
                <div id="calendar-grid">
                    <div class="loading"><div class="spinner"></div>Loading calendar...</div>
                </div>
            `;
            // Set initial dub filter from config (legacy values like "dubbed"/"subbed" fall back to All)
            const filterSelect = document.getElementById('calendar-dub-filter');
            if (filterSelect && config?.calendar?.dubFilter) {
                const configFilter = config.calendar.dubFilter;
                const option = filterSelect.querySelector(`option[value="${configFilter}"]`);
                if (option) filterSelect.value = configFilter;
            }
            fetchCalendar();
        }

        // Collapse per-language duplicates of the same episode into one entry whose
        // audioLocales lists every dub it ships in (used for the "All Languages" view).
        const CAL_LOCALE_ORDER = ['ja-JP'];
        function consolidateCalendarEpisodes(episodes) {
            const groups = new Map();
            const order = [];
            for (const ep of episodes) {
                const key = `${ep.seriesId || ep.seriesTitle || ep.seasonName || ''}|${ep.episodeNumber || ''}`;
                if (!groups.has(key)) {
                    // Clone so we don't mutate the source; start the locale list
                    const base = Object.assign({}, ep);
                    base.audioLocales = [];
                    groups.set(key, base);
                    order.push(key);
                }
                const g = groups.get(key);
                if (ep.audioLocale && !g.audioLocales.includes(ep.audioLocale)) {
                    g.audioLocales.push(ep.audioLocale);
                }
                // Prefer a thumbnail/airDate from any variant that has one
                if (!g.thumbnailUrl && ep.thumbnailUrl) g.thumbnailUrl = ep.thumbnailUrl;
                if (ep.isInHistory) { g.isInHistory = true; g.showHistoryMark = ep.showHistoryMark; g.historyDownloadState = ep.historyDownloadState; }
            }
            for (const g of groups.values()) {
                g.audioLocales.sort((a, b) => {
                    const ia = CAL_LOCALE_ORDER.indexOf(a), ib = CAL_LOCALE_ORDER.indexOf(b);
                    if (ia !== -1 || ib !== -1) return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
                    return a.localeCompare(b);
                });
            }
            return order.map(k => groups.get(k));
        }

        async function onCalendarDubFilterChange(dubFilter) {
            // Save to config
            try {
                await fetch('/api/v1/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ calendar: { dubFilter: dubFilter } })
                });
                // Update local config
                if (!config.calendar) config.calendar = {};
                config.calendar.dubFilter = dubFilter;
            } catch (e) {
                console.warn('Failed to save calendar dub filter:', e);
            }
            fetchCalendar();
        }

        function changeWeek(offset) {
            calendarWeekOffset += offset;
            fetchCalendar();
        }

        async function fetchCalendar(forceUpdate = false) {
            try {
                // Compute Monday of target week
                // dayOfWeek: 0=Sun, 1=Mon, ..., 6=Sat
                // Days since Monday: Mon=0, Tue=1, ..., Sun=6
                const now = new Date();
                const dayOfWeek = now.getDay();
                const daysSinceMonday = dayOfWeek === 0 ? 6 : dayOfWeek - 1;
                const monday = new Date(now);
                monday.setDate(now.getDate() - daysSinceMonday + (calendarWeekOffset * 7));

                const filterSel = document.getElementById('calendar-dub-filter');
                const dubFilter = filterSel ? filterSel.value : (config?.calendar?.dubFilter || 'none');
                const metaLang = config?.history?.lang || 'en-US';

                // Custom (API-based) calendar: reliable per-episode audio locale, so the
                // dub filter actually works. The backend builds the 7 days ending at the
                // given date, so pass the Sunday of the displayed week.
                const sunday = new Date(monday);
                sunday.setDate(monday.getDate() + 6);
                const sundayStr = `${sunday.getFullYear()}-${String(sunday.getMonth() + 1).padStart(2, '0')}-${String(sunday.getDate()).padStart(2, '0')}`;

                const res = await fetch(`/api/v1/calendar/custom?date=${sundayStr}&language=${encodeURIComponent(metaLang)}&dubFilter=${encodeURIComponent(dubFilter)}${forceUpdate ? '&forceUpdate=true' : ''}`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                const grid = document.getElementById('calendar-grid');
                if (!grid) return;
                if (!data.days || data.days.length === 0) {
                    grid.innerHTML = `
                        <div class="empty-state" style="grid-column:1/-1;">
                            <div class="empty-state-icon">&#128197;</div>
                            <div class="empty-state-title">No calendar data</div>
                        </div>`;
                    return;
                }

                const dayNames = ['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
                // "All Languages" -> collapse the per-language duplicates of the same
                // episode into one card listing every audio locale it ships in. A specific
                // language is already filtered server-side, so render those as-is.
                const isAllLangs = !dubFilter || dubFilter === 'none';
                grid.className = 'calendar-grid';
                grid.innerHTML = data.days.map((day, idx) => {
                    const date = day.date ? new Date(day.date) : null;
                    const dayName = day.dayName || (date && !isNaN(date.getTime()) ? dayNames[date.getDay()] : 'Day');
                    const dateDisplay = date && !isNaN(date.getTime()) ? date.toLocaleDateString() : '';
                    const dayEpisodes = isAllLangs ? consolidateCalendarEpisodes(day.episodes || []) : (day.episodes || []);
                    return `
                        <div class="calendar-day">
                            <div class="calendar-day-header">
                                <div class="calendar-day-date">${dateDisplay}</div>
                                <div class="calendar-day-name">${escapeHtml(dayName)}</div>
                            </div>
                            <div class="calendar-day-content">
                                ${dayEpisodes.length > 0 ? dayEpisodes.map(ep => {
                                    const epDate = ep.airDate ? new Date(ep.airDate) : null;
                                    const timeDisplay = epDate && !isNaN(epDate.getTime()) ? epDate.toLocaleTimeString([], {hour: '2-digit', minute:'2-digit'}) : '';
                                    const historyMark = ep.isInHistory && ep.showHistoryMark
                                        ? `<div class="calendar-history-mark mark-${(ep.historyDownloadState || 'none').toLowerCase()}" title="${
                                            ep.historyDownloadState === 'Downloaded' ? 'Downloaded'
                                            : ep.historyDownloadState === 'PartlyDownloaded' ? 'Partly downloaded'
                                            : ep.historyDownloadState === 'NotDownloaded' ? 'Not downloaded' : ''}"></div>`
                                        : '';
                                    return `
                                    <div class="calendar-episode ${ep.isPremiere ? 'premiere' : ''}">
                                        ${historyMark}
                                        <div class="calendar-episode-time">${timeDisplay}</div>
                                        <div class="calendar-episode-thumb">
                                            ${ep.thumbnailUrl && isSafeUrl(ep.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(ep.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                                            <div class="calendar-episode-number">${escapeHtml(ep.episodeNumber || '')}</div>
                                        </div>
                                        <div class="calendar-episode-title">${escapeHtml(ep.seriesTitle || ep.seasonName || '')}</div>
                                        ${(ep.audioLocales && ep.audioLocales.length > 0)
                                            ? `<div class="calendar-episode-langs">${ep.audioLocales.map(l => `<span class="cal-lang">${escapeHtml(l)}</span>`).join('')}</div>`
                                            : (ep.audioLocale ? `<div class="calendar-episode-locale">${escapeHtml(ep.audioLocale)}</div>` : '')}
                                        ${ep.isPremiumOnly ? '<span class="badge badge-premium">Premium</span>' : ''}
                                        ${ep.hasAired ? `<button class="header-btn primary" style="margin-top:6px; font-size:0.75em; padding:4px 10px;" onclick="addEpisodeToQueue('${escapeJsString(ep.id)}', '${escapeJsString(ep.seriesTitle || ep.seasonName || '')}', '${escapeJsString(ep.episodeNumber || '')}', '${escapeJsString(ep.thumbnailUrl || '')}', '${escapeJsString(ep.audioLocale || '')}')">Download</button>` : ''}
                                    </div>
                                    `;
                                }).join('') : '<div style="color:var(--text-muted); text-align:center; padding:20px 0;">No episodes</div>'}
                            </div>
                        </div>
                    `;
                }).join('');
            } catch (e) {
                const grid = document.getElementById('calendar-grid');
                if (grid) {
                    grid.className = '';
                    grid.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-icon">&#10060;</div>
                            <div class="empty-state-title">Failed to load calendar</div>
                        </div>`;
                }
            }
        }

        async function addEpisodeToQueue(episodeId, seriesTitle = '', episodeNumber = '', thumbnailUrl = '', audioLocale = '') {
            try {
                // Forward the metadata the calendar already has so the queue item shows a
                // real name + thumbnail instead of the "Episode <id>" backend fallback.
                const payload = { episodeId };
                if (seriesTitle) {
                    payload.seriesTitle = seriesTitle;
                    payload.title = episodeNumber ? `${seriesTitle} — E${episodeNumber}` : seriesTitle;
                }
                const epNum = parseInt(episodeNumber, 10);
                if (Number.isFinite(epNum)) payload.episodeNumber = epNum;
                if (thumbnailUrl) payload.thumbnailUrl = thumbnailUrl;
                if (audioLocale) { payload.locale = audioLocale; payload.audioLocale = audioLocale; }
                const res = await fetch('/api/v1/queue', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Added to queue', 'success');
            } catch (e) {
                showToast('Failed to add', 'error');
            }
        }

        // ================== SEASONS ==================
        function renderSeasons(container) {
            container.innerHTML = `
                <div class="page-title">Upcoming Seasons</div>
                <div class="page-subtitle">Upcoming anime episodes and premieres</div>
                <div id="seasons-content">
                    <div class="loading"><div class="spinner"></div>Loading upcoming episodes...</div>
                </div>
            `;
            fetchUpcomingSeasons();
        }

        async function fetchUpcomingSeasons() {
            try {
                const language = (config?.calendar?.language || 'en-us').toLowerCase();
                const res = await fetch(`/api/v1/calendar/upcoming?language=${encodeURIComponent(language)}`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const episodes = await res.json();
                renderUpcomingSeasonsContent(episodes || []);
            } catch (e) {
                const el = document.getElementById('seasons-content');
                if (el) el.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#10060;</div>
                        <div class="empty-state-title">Failed to load upcoming episodes</div>
                    </div>`;
            }
        }

        function renderUpcomingSeasonsContent(episodes) {
            const content = document.getElementById('seasons-content');
            if (!episodes || episodes.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128270;</div>
                        <div class="empty-state-title">No upcoming episodes found</div>
                    </div>`;
                return;
            }

            // Group by series
            const seriesMap = new Map();
            for (const ep of episodes) {
                const seriesId = ep.seriesId || ep.id;
                if (!seriesMap.has(seriesId)) {
                    seriesMap.set(seriesId, {
                        seriesId: seriesId,
                        seriesTitle: ep.seriesTitle || 'Unknown',
                        thumbnailUrl: ep.thumbnailUrl,
                        episodes: []
                    });
                }
                seriesMap.get(seriesId).episodes.push(ep);
            }

            const seriesList = Array.from(seriesMap.values());

            content.innerHTML = `
                <div class="history-poster-grid">
                    ${seriesList.map(series => `
                        <div class="history-poster clickable" onclick="showSeriesEpisodesModal('${escapeJsString(series.seriesId)}', '${escapeJsString(series.seriesTitle)}')">
                            <div class="history-poster-img">
                                ${series.thumbnailUrl && isSafeUrl(series.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(series.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                                ${series.episodes?.some(e => e.isPremiere) ? `<div class="history-poster-badge">Premiere</div>` : ''}
                            </div>
                            <div class="history-poster-info">
                                <div class="history-poster-title" title="${escapeHtmlAttribute(series.seriesTitle)}">${escapeHtml(series.seriesTitle)}</div>
                                <div class="history-poster-meta">${series.episodes?.length || 0} upcoming episode(s)</div>
                                <div class="history-poster-meta" style="color: var(--accent-green);">
                                    Next: ${series.episodes?.length > 0 ? formatDate(series.episodes[0].airDate) : 'Unknown'}
                                </div>
                            </div>
                        </div>
                    `).join('')}
                </div>`;
        }

        // ================== BROWSE ==================
        function renderBrowse(container) {
            container.innerHTML = `
                <div class="browse-header-row" style="display:flex; align-items:center; justify-content:space-between; gap:var(--space-4); flex-wrap:wrap;">
                    <div>
                        <div class="page-title">Browse All Series</div>
                        <div class="page-subtitle">Explore all available series</div>
                    </div>
                    <div id="browse-rating-btns" style="display:flex; flex-wrap:wrap; gap:8px; justify-content:flex-end; align-items:center;"></div>
                </div>
                <div class="browse-filter-bar">
                    <span class="browse-filter-label">Dub language</span>
                    <select class="form-select mw-180" id="browse-dub-filter" onchange="onBrowseDubFilterChange(this.value)">
                        <option value="">All languages</option>
                        ${LANG_OPTIONS.map(o=>`<option value="${escapeHtmlAttribute(o.value)}" ${browseDubFilter===o.value?'selected':''}>${escapeHtml(o.label)}</option>`).join('')}
                    </select>
                    <span class="browse-filter-count" id="browse-count"></span>
                </div>
                <div id="browse-content">
                    <div class="loading"><div class="spinner"></div>Loading series...</div>
                </div>
            `;
            fetchAllSeries();
        }

        async function fetchAllSeries() {
            try {
                const res = await fetch('/api/v1/series/all');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                allBrowseSeries = data || [];
                renderBrowseFiltered();
            } catch (e) {
                const content = document.getElementById('browse-content');
                if (content) content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#10060;</div>
                        <div class="empty-state-title">Failed to load series</div>
                    </div>`;
            }
        }

        function onBrowseDubFilterChange(value) {
            browseDubFilter = value || '';
            renderBrowseFiltered();
        }

        // --- Browse rating filter ---
        // Maturity ratings ordered youngest -> oldest, spanning BOTH the US TV Parental
        // Guidelines (TV-Y..TV-MA) and MPAA film ratings (G..NC-17) — Crunchyroll's catalog
        // mixes them (e.g. PG-13 and R alongside TV-14/TV-MA). Codes not listed sort last.
        const RATING_ORDER = ['TV-Y','TV-Y7','G','TV-G','PG','TV-PG','PG-13','TV-14','TV-MA','R','NC-17','X'];
        const ratingRank = code => { const i = RATING_ORDER.indexOf(code); return i === -1 ? RATING_ORDER.length : i; };
        function seriesMatchesRating(s, selected) {
            const codes = Array.isArray(s.maturityRatings) ? s.maturityRatings : [];
            return codes.some(code => selected.has(String(code).toUpperCase()));
        }
        function onBrowseRatingToggle(code) {
            if (browseRatingFilter.has(code)) browseRatingFilter.delete(code);
            else browseRatingFilter.add(code);
            renderBrowseFiltered();
        }
        function clearBrowseRating() {
            browseRatingFilter.clear();
            renderBrowseFiltered();
        }
        // Show ONLY ratings actually present in the loaded library, ordered youngest -> oldest
        // by ratingRank (covers both TV and MPAA codes). A rating with zero matching series
        // is omitted so it never shows an empty filter.
        function buildRatingButtons() {
            const box = document.getElementById('browse-rating-btns');
            if (!box) return;
            const present = new Set();
            allBrowseSeries.forEach(s => (Array.isArray(s.maturityRatings) ? s.maturityRatings : [])
                .forEach(c => { if (c) present.add(String(c).toUpperCase()); }));
            const codes = [...present].sort((a, b) => ratingRank(a) - ratingRank(b) || a.localeCompare(b));
            const btn = code =>
                `<button class="season-tab ${browseRatingFilter.has(code) ? 'active' : ''}" onclick="onBrowseRatingToggle('${escapeJsString(code)}')">${escapeHtml(code)}</button>`;
            const clearBtn = browseRatingFilter.size
                ? `<button class="season-tab" onclick="clearBrowseRating()">Clear</button>` : '';
            box.innerHTML = codes.map(btn).join('') + clearBtn;
        }

        // Apply the current dub-language + rating filters to the cached series list and render.
        function renderBrowseFiltered() {
            buildRatingButtons();
            let list = allBrowseSeries;
            if (browseDubFilter) {
                list = list.filter(s => Array.isArray(s.audioLocales) && s.audioLocales.includes(browseDubFilter));
            }
            if (browseRatingFilter.size) {
                list = list.filter(s => seriesMatchesRating(s, browseRatingFilter));
            }
            const count = document.getElementById('browse-count');
            if (count) count.textContent = `${list.length} series`;
            renderBrowseContent(list);
        }

        function renderBrowseContent(series) {
            const content = document.getElementById('browse-content');
            if (!content) return;
            if (!series || series.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128270;</div>
                        <div class="empty-state-title">No series found</div>
                    </div>`;
                return;
            }

            content.innerHTML = `
                <div class="history-poster-grid">
                    ${series.map(s => `
                        <div class="history-poster clickable" onclick="selectBrowseResult('${escapeJsString(s.id)}')">
                            <div class="history-poster-img">
                                ${(s.coverArtUrl || s.thumbnailUrl) && isSafeUrl(s.coverArtUrl || s.thumbnailUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(s.coverArtUrl || s.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                            </div>
                            <div class="history-poster-info">
                                <div class="history-poster-title" title="${escapeHtmlAttribute(s.title)}">${escapeHtml(s.title)}</div>
                                <div class="history-poster-meta">${s.seasons?.length || 0} season(s)</div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        }

        async function selectBrowseResult(seriesId) {
            navigateTo('add-download');
            selectBrowseResultTimeout = setTimeout(async () => {
                const listContainer = document.getElementById('add-episodes-list');
                if (listContainer) {
                    listContainer.innerHTML = '<div class="loading"><div class="spinner"></div>Loading episodes...</div>';
                }
                try {
                    const dubLangs = config?.download?.dubLanguages || ['ja-JP'];
                    const dubLangParam = dubLangs.map(l => `dubLang=${encodeURIComponent(l)}`).join('&');
                    const res = await fetch(`/api/v1/series/${seriesId}/list?${dubLangParam}`);
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    const result = await res.json();
                    
                    if (!result || !result.list) {
                        showToast('No episodes found for this series', 'error');
                        return;
                    }
                    
                    addDownloadEpisodeList = result.list || [];
                    addDownloadSeriesData = result.data || {};
                    // onSeasonChange and showFeaturedMusic require a selected series -
                    // without this the episode list never renders when coming from Browse
                    const browseTitle = addDownloadEpisodeList[0]?.seasonTitle || 'Selected Series';
                    addDownloadSelectedSeries = { id: seriesId, title: browseTitle };

                    const searchInput = document.getElementById('add-search-input');
                    if (searchInput) searchInput.value = browseTitle;
                    
                    const musicBtn = document.getElementById('music-btn');
                    if (musicBtn) musicBtn.style.display = 'inline-flex';
                    
                    const seasonMap = new Map();
                    addDownloadEpisodeList.forEach(ep => {
                        const seasonId = ep.id;
                        if (seasonId && !seasonMap.has(seasonId)) {
                            seasonMap.set(seasonId, {
                                id: seasonId,
                                title: ep.seasonTitle || `Season ${ep.season}`
                            });
                        }
                    });
                    const seasons = Array.from(seasonMap.values());
                    
                    selectedEpisodeDubs.clear();
                    const defaultDubs = new Set(dubLangs);
                    addDownloadEpisodeList.forEach(ep => {
                        const epKey = ep.e;
                        const epData = addDownloadSeriesData[epKey];
                        if (epData && epData.variants) {
                            const availableDubs = new Set(epData.variants.map(v => v.lang?.crLocale || v.item?.audioLocale));
                            const selected = new Set([...defaultDubs].filter(d => availableDubs.has(d)));
                            if (selected.size > 0) {
                                selectedEpisodeDubs.set(epKey, selected);
                            }
                        }
                    });
                    
                    const dropdown = document.getElementById('season-dropdown');
                    if (dropdown) {
                        dropdown.innerHTML = seasons.map(season => `<option value="${escapeHtmlAttribute(season.id)}">${escapeHtml(season.title)}</option>`).join('');
                        if (seasons.length > 0) {
                            dropdown.value = seasons[0].id;
                            onSeasonChange(seasons[0].id);
                        } else {
                            selectedEpisodes.clear();
                            renderAddEpisodesMultiDub();
                        }
                    }
                } catch (e) {
                    console.error('Failed to load series:', e);
                    showToast('Failed to load series episodes', 'error');
                    const listContainer = document.getElementById('add-episodes-list');
                    if (listContainer) {
                        listContainer.innerHTML = `
                            <div class="empty-state">
                                <div class="empty-state-icon">❌</div>
                                <div class="empty-state-title">Failed to load episodes</div>
                                <div>Please try again</div>
                            </div>
                        `;
                    }
                }
            }, 100);
        }

        // ================== SEASONAL ==================
        let seasonalSeason = null, seasonalYear = null;
        // Map current month to the anime season (Winter Dec-Feb, Spring Mar-May, etc.).
        function currentAnimeSeason() {
            const m = new Date().getMonth();
            if (m === 11 || m <= 1) return 'winter';
            if (m <= 4) return 'spring';
            if (m <= 7) return 'summer';
            return 'fall';
        }

        function renderSeasonal(container) {
            if (!seasonalSeason) { seasonalSeason = currentAnimeSeason(); seasonalYear = new Date().getFullYear(); }
            const seasons = [['winter','Winter'],['spring','Spring'],['summer','Summer'],['fall','Fall']];
            const thisYear = new Date().getFullYear();
            container.innerHTML = `
                <div class="page-title">Seasonal Anime</div>
                <div class="page-subtitle">The season's lineup from AniList, matched to Crunchyroll</div>
                <div class="season-tabs" id="season-tabs">
                    ${seasons.map(([v,l]) => `<button class="season-tab ${seasonalSeason===v?'active':''}" data-season="${v}" onclick="selectSeasonTab('${v}')">${l}</button>`).join('')}
                    <select class="form-select" id="season-year-select" style="margin-left:auto; width:110px;" onchange="selectSeasonYear(this.value)">
                        ${Array.from({length: 6}, (_, i) => { const y = thisYear + 1 - i; return `<option value="${y}" ${y===seasonalYear?'selected':''}>${y}</option>`; }).join('')}
                    </select>
                </div>
                <div id="seasonal-content"></div>
            `;
            fetchSeasonalSeries();
        }

        function selectSeasonTab(s) {
            seasonalSeason = s;
            document.querySelectorAll('#season-tabs .season-tab').forEach(t => t.classList.toggle('active', t.dataset.season === s));
            fetchSeasonalSeries();
        }
        function selectSeasonYear(y) { seasonalYear = parseInt(y, 10); fetchSeasonalSeries(); }

        async function fetchSeasonalSeries() {
            const content = document.getElementById('seasonal-content');
            if (!content || !seasonalSeason || !seasonalYear) return;
            content.innerHTML = '<div class="loading"><div class="spinner"></div>Loading seasonal anime...</div>';

            try {
                const res = await fetch(`/api/v1/series/seasonal?season=${encodeURIComponent(seasonalSeason)}&year=${encodeURIComponent(seasonalYear)}`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                renderSeasonalContent(data || []);
            } catch (e) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#10060;</div>
                        <div class="empty-state-title">Failed to load seasonal anime</div>
                    </div>`;
            }
        }

        function renderSeasonalContent(series) {
            const content = document.getElementById('seasonal-content');
            if (!content) return;
            if (!series || series.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128270;</div>
                        <div class="empty-state-title">No seasonal anime found</div>
                        <div>Try another season or year</div>
                    </div>`;
                return;
            }

            content.innerHTML = `
                <div class="history-poster-meta" style="margin-bottom:12px;">${series.length} titles</div>
                <div class="history-poster-grid">
                    ${series.map(s => {
                        const clickable = s.id && s.id.length;
                        const img = (s.coverArtUrl || s.thumbnailUrl) && isSafeUrl(s.coverArtUrl || s.thumbnailUrl)
                            ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(s.coverArtUrl || s.thumbnailUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺';
                        const sd = fmtSeasonalDate(s.startDate);
                        const na = fmtNextAir(s);
                        const metaMain = (s.episodeCount ? `${s.episodeCount} ep` : 'On Crunchyroll') + (sd ? ` · ${sd}` : '');
                        return `
                        <div class="history-poster" ${clickable
                            ? `onclick="selectBrowseResult('${escapeJsString(s.id)}')" class="clickable"`
                            : `title="Not on Crunchyroll yet" style="opacity:.55;"`}>
                            <div class="history-poster-img">${img}</div>
                            <div class="history-poster-info">
                                <div class="history-poster-title" title="${escapeHtmlAttribute(s.title)}">${escapeHtml(s.title)}</div>
                                <div class="history-poster-meta">${metaMain}</div>
                                ${na ? `<div class="history-poster-meta" style="color:var(--accent);">${na}</div>` : ''}
                            </div>
                        </div>`;
                    }).join('')}
                </div>`;
        }

        // Seasonal date helpers.
        function fmtSeasonalDate(iso) {
            if (!iso) return '';
            const d = new Date(iso + 'T00:00:00');
            if (isNaN(d.getTime())) return '';
            return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
        }
        function fmtNextAir(s) {
            if (!s.nextEpisodeNumber && !s.nextAirUtc) return '';
            let out = 'Next: ';
            if (s.nextEpisodeNumber) out += 'E' + s.nextEpisodeNumber;
            if (s.nextAirUtc) {
                const d = new Date(s.nextAirUtc);
                if (!isNaN(d.getTime())) {
                    out += (s.nextEpisodeNumber ? ' · ' : '') + d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) +
                        ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                }
            }
            return out;
        }

        function escapeHtml(text) {
            if (text == null) return '';
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        function escapeHtmlAttribute(text) {
            if (text == null) return '';
            return String(text).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        }

        function escapeJsString(text) {
            if (text == null) return '';
            // Escape quotes and angle brackets as \uXXXX so the result is safe both inside a JS
            // string AND inside a double-quoted HTML attribute (e.g. inline onclick): no literal
            // quote to close the attribute, and no closing-script-tag breakout.
            return String(text)
                .replace(/\\/g, '\\\\')
                .replace(/'/g, '\\u0027')
                .replace(/"/g, '\\u0022')
                .replace(/`/g, '\\`')
                .replace(/\$\{/g, '\\${')
                .replace(/</g, '\\u003C')
                .replace(/>/g, '\\u003E')
                .replace(/&/g, '\\u0026')
                .replace(/\n/g, '\\n')
                .replace(/\r/g, '\\r');
        }

        function isSafeUrl(url) {
            if (!url) return false;
            return /^(https?:|\/\/|data:image\/)/i.test(url);
        }

        // Route Crunchyroll images through the server-side caching proxy so each image is
        // fetched from CR once and then served from disk + the browser cache. CR image URLs
        // are content-addressed, so a changed image = a new URL = a fresh fetch. Non-CR or
        // local/relative URLs pass through unchanged.
        function crImg(url) {
            if (!url || typeof url !== 'string') return url;
            if (/^https?:\/\/([^/]+\.)?crunchyroll\.com\//i.test(url)) {
                return '/api/v1/images?url=' + encodeURIComponent(url);
            }
            return url;
        }

        function formatDate(dateStr) {
            if (!dateStr) return 'Unknown';
            const date = new Date(dateStr);
            if (isNaN(date.getTime())) return 'Unknown';
            const now = new Date();
            const diff = date - now;
            const days = Math.floor(diff / (1000 * 60 * 60 * 24));
            
            if (days === 0) return 'Today';
            if (days === 1) return 'Tomorrow';
            if (days > 1 && days < 7) return `In ${days} days`;
            if (days < 0) return 'Aired';
            return date.toLocaleDateString();
        }

        function showSeriesEpisodesModal(seriesId, seriesTitle) {
            const modalTitle = document.getElementById('modal-title');
            if (modalTitle) modalTitle.textContent = seriesTitle;
            
            const modalBody = document.getElementById('modal-body');
            const modalFooter = document.getElementById('modal-footer');
            const modal = document.getElementById('modal');
            if (!modalBody || !modalFooter || !modal) return;
            
            modalBody.innerHTML = `
                <div class="loading"><div class="spinner"></div>Loading episodes...</div>
            `;
            modalFooter.innerHTML = `
                <button class="header-btn" onclick="closeModal()">Close</button>
                <button class="header-btn primary" onclick="searchSeriesById('${escapeJsString(seriesId)}')">Search Series</button>
            `;
            modal.classList.add('active');
            
            // Fetch and show episodes
            (async () => {
                try {
                    const res = await fetch(`/api/v1/series/${seriesId}/episodes`);
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    const episodes = await res.json();
                    const modalBody = document.getElementById('modal-body');
                    if (modalBody) modalBody.innerHTML = `
                        <div style="max-height: 400px; overflow-y: auto;">
                            ${(episodes || []).slice(0, 10).map(ep => `
                                <div style="padding: 10px; border-bottom: 1px solid var(--border-color); display: flex; justify-content: space-between; align-items: center;">
                                    <div>
                                        <div style="font-weight: 500;">Episode ${escapeHtml(ep.episodeNumber || '?')}</div>
                                        <div class="hint">${escapeHtml(ep.title || 'Unknown')}</div>
                                    </div>
                                    <button class="header-btn" onclick="addEpisodeToQueueWithDetails('${escapeJsString(ep.id)}', '${escapeJsString(ep.title || '')}', '${escapeJsString(seriesTitle)}')">Add</button>
                                </div>
                            `).join('')}
                        </div>
                    `;
                } catch (e) {
                    const modalBody = document.getElementById('modal-body');
                    if (modalBody) modalBody.innerHTML = `
                        <div class="empty-state">
                            <div class="empty-state-title">Failed to load episodes</div>
                        </div>
                    `;
                }
            })();
        }

        function performSearch() {
            const searchInput = document.getElementById('add-search-input');
            if (searchInput && searchInput.value.trim()) {
                doAddSearch(searchInput.value.trim());
            }
        }

        function searchSeriesById(seriesId) {
            closeModal();
            navigateTo('add-download');
            setTimeout(() => {
                const searchInput = document.getElementById('add-search-input');
                if (searchInput) {
                    searchInput.value = seriesId;
                    performSearch();
                }
            }, 100);
        }

        async function addEpisodeToQueueWithDetails(episodeId, title, seriesTitle) {
            try {
                const res = await fetch('/api/v1/queue', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        episodeId: episodeId,
                        title: title || 'Unknown',
                        seriesTitle: seriesTitle || 'Unknown'
                    })
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Added to queue', 'success');
            } catch (e) {
                showToast('Failed to add to queue', 'error');
            }
        }

        // ================== HISTORY ==================
        function renderHistory(container) {
            container.innerHTML = `
                <div class="page-title">History</div>
                <div class="page-subtitle">Download history and tracked series</div>
                <div class="history-toolbar">
                    <button class="toolbar-btn" onclick="refreshHistory()">
                        <span class="icon">&#128260;</span>
                        <span>Refresh Filtered</span>
                    </button>
                    <button class="toolbar-btn" onclick="addMissingToQueue()">
                        <span class="icon">&#128229;</span>
                        <span>Add To Queue</span>
                    </button>
                    <div style="position:relative; margin-left:8px;">
                        <div class="header-search w-220" onclick="openHistorySearchPopup()">
                            <span>&#128269;</span>
                            <input type="text" id="history-search-input" placeholder="Filter history..." 
                                   value="${escapeHtmlAttribute(historySearchQuery)}" 
                                   oninput="onHistorySearchInput(this.value)" 
                                   onfocus="openHistorySearchPopup()"
                                   onkeydown="if(event.key==='Escape'){closeHistorySearchPopup();this.blur();}">
                            ${historySearchQuery && !historySearchPopupOpen ? '<div class="search-active-dot"></div>' : ''}
                        </div>
                        <div class="search-popup" id="history-search-popup" style="display:none;"></div>
                    </div>
                    <!-- Download mode toggles hidden - not yet wired to queue logic -->
                    <button class="toolbar-btn" id="btn-sonarr-menu" onclick="openSonarrMenu(event)">
                        <span class="icon">&#127758;</span>
                        <span>Sonarr</span>
                    </button>
                    <div style="margin-left:auto; display:flex; gap:5px;">
                        <button class="toolbar-btn ${historyViewMode === 'poster' ? 'active' : ''}" onclick="setHistoryView('poster')">
                            <span class="icon">&#9645;</span>
                            <span>Poster</span>
                        </button>
                        <button class="toolbar-btn ${historyViewMode === 'table' ? 'active' : ''}" onclick="setHistoryView('table')">
                            <span class="icon">&#9776;</span>
                            <span>Table</span>
                        </button>
                    </div>
                    <button class="toolbar-btn" onclick="toggleSortMenu(event)">
                        <span class="icon">&#8645;</span>
                        <span>Sort</span>
                    </button>
                    <button class="toolbar-btn" onclick="showHistoryMaintenanceMenu(event)">
                        <span class="icon">&#128295;</span>
                        <span>Maintain</span>
                    </button>
                </div>
                <div id="history-content">
                    <div class="loading"><div class="spinner"></div>Loading history...</div>
                </div>
            `;
            fetchHistoryData();
            // Restart history auto-refresh interval if it was cleared
            if (!historyIntervalId) {
                historyIntervalId = setInterval(() => {
                    if (currentPage === 'history') fetchHistoryData();
                }, HISTORY_POLL_INTERVAL_MS);
            }
        }

        async function fetchHistoryData() {
            try {
                const res = await fetch('/api/v1/history/rich');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                historyData = data || [];
                // Same endpoint feeds the series-detail modal - keep its cache fresh
                historyRichData = historyData;
                renderHistoryContent();
                // Auto-match against Sonarr once per session so matches "rope in" without the
                // user manually opening the Sonarr menu (no-op if Sonarr disabled or all matched).
                // Fire-and-forget so history paints immediately.
                maybeAutoMatchSonarr();
            } catch (e) {
                const el = document.getElementById('history-content');
                if (el) el.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#10060;</div>
                        <div class="empty-state-title">Failed to load history</div>
                    </div>`;
            }
        }

        function renderHistoryContent() {
            const content = document.getElementById('history-content');
            if (!content) return;
            const filteredData = historyFilterText 
                ? historyData.filter(item => 
                    (item.seriesTitle || '').toLowerCase().includes(historyFilterText) ||
                    (item.seriesDescription || '').toLowerCase().includes(historyFilterText) ||
                    (item.sonarrSlugTitle || '').toLowerCase().includes(historyFilterText)
                  )
                : historyData;
            
            if (filteredData.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#128218;</div>
                        <div class="empty-state-title">${historyData.length === 0 ? 'No history yet' : 'No matches found'}</div>
                        <div>${historyData.length === 0 ? 'Completed downloads will appear here' : 'Try a different search term'}</div>
                    </div>`;
                return;
            }

            if (historyViewMode === 'poster') {
                content.innerHTML = '<div class="history-poster-grid">' + filteredData.map(item => `
                    <div class="history-poster ${item.sonarrSeriesId ? 'sonarr-matched' : 'sonarr-unmatched'} clickable" onclick="showHistorySeriesDetail('${escapeJsString(item.seriesId)}')">
                        <div class="history-poster-img">
                            ${item.thumbnailImageUrl && isSafeUrl(item.thumbnailImageUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(item.thumbnailImageUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                            ${item.hasNewEpisodes ? `<div class="history-poster-badge">New</div>` : ''}
                        </div>
                        <div class="history-poster-info">
                                <div class="history-poster-title" title="${escapeHtmlAttribute(item.seriesTitle || '')}">${escapeHtml(item.seriesTitle) || 'Unknown'}${item.sonarrSeriesId ? '<span class="sonarr-match-badge">Sonarr</span>' : ''}</div>
                            <div class="history-poster-meta">${escapeHtml(item.sonarrNextAirDate || '')}</div>
                            <div class="history-poster-meta" style="font-size:0.7em; margin-top:4px;">
                                ${item.downloadedEpisodes || 0} / ${item.totalEpisodes || 0} episodes
                            </div>
                        </div>
                    </div>
                `).join('') + '</div>';
            } else {
                content.innerHTML = `
                    <div class="history-table">
                        <table>
                            <thead>
                                <tr>
                                    <th>Series</th>
                                    <th>Status</th>
                                    <th>Sonarr</th>
                                    <th>Progress</th>
                                    <th>New</th>
                                    <th>Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                ${filteredData.map(item => `
                                    <tr class="${item.sonarrSeriesId ? 'sonarr-matched' : 'sonarr-unmatched'} clickable" onclick="showHistorySeriesDetail('${escapeJsString(item.seriesId)}')">
                                        <td>
                                            <strong>${escapeHtml(item.seriesTitle) || 'Unknown'}</strong>
                                            ${item.sonarrSeriesId ? '<span class="sonarr-match-badge">Sonarr</span>' : ''}
                                            <br><small style="color:var(--text-secondary);">${item.seriesDescription ? escapeHtml(item.seriesDescription.substring(0, 80)) + '...' : ''}</small>
                                        </td>
                                        <td>${getHistoryStatusBadge(item)}</td>
                                        <td>${item.sonarrSeriesId ? `✓ ${escapeHtml(item.sonarrSlugTitle) || 'Matched'}` : '—'}</td>
                                        <td>${item.downloadedEpisodes || 0} / ${item.totalEpisodes || 0}</td>
                                        <td>${item.hasNewEpisodes ? '✓' : '—'}</td>
                                        <td>
                                            <button class="btn-icon" onclick="event.stopPropagation(); refreshSeries('${escapeJsString(item.seriesId)}')">&#128260;</button>
                                            <button class="btn-icon" onclick="event.stopPropagation(); downloadSeries('${escapeJsString(item.seriesId)}')">&#9660;</button>
                                            ${item.sonarrSeriesId ? `<button class="btn-icon" onclick="event.stopPropagation(); matchEpisodesForSeries('${escapeJsString(item.seriesId)}')" title="Rematch episodes">&#128260;</button>` : ''}
                                            <button class="btn-icon" onclick="event.stopPropagation(); showSeriesSettingsOverride('${escapeJsString(item.seriesId)}')" title="Settings">&#9881;</button>
                                        </td>
                                    </tr>
                                `).join('')}
                            </tbody>
                        </table>
                    </div>`;
            }
        }

        function setHistoryView(mode) {
            historyViewMode = mode;
            renderHistoryContent();
        }

        // ================== ACCOUNT ==================
        function renderAccount(container) {
            container.innerHTML = `
                <div class="page-title">Account</div>
                <div class="page-subtitle">Crunchyroll account details</div>
                <div class="account-container">
                    <div class="account-avatar" id="account-avatar">
                        <span>&#128100;</span>
                    </div>
                    <div class="account-name" id="account-name">Not logged in</div>
                    <div class="account-subscription" id="account-subscription">-</div>
                    <div style="display:flex; gap:15px; margin-bottom:20px;">
                        <button class="header-btn primary" id="btn-login" onclick="showLoginModal()">Login</button>
                        <button class="header-btn" id="btn-logout" onclick="logout()" style="display:none;">Logout</button>
                    </div>
                    <div id="profile-section" style="width:100%; max-width:400px; display:none;">
                        <div class="card">
                            <div class="card-header">
                                <div class="card-title">Profiles</div>
                            </div>
                            <div id="profile-list"></div>
                        </div>
                    </div>
                </div>
            `;
            fetchAuthStatus();
        }

        async function fetchAuthStatus() {
            try {
                const res = await fetch('/api/v1/auth/status');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                authStatus = await res.json();
                updateTopbarProfile();
                const nameEl = document.getElementById('account-name');
                const subEl = document.getElementById('account-subscription');
                const avatarEl = document.getElementById('account-avatar');
                const loginBtn = document.getElementById('btn-login');
                const logoutBtn = document.getElementById('btn-logout');
                const profileSection = document.getElementById('profile-section');
                const profileList = document.getElementById('profile-list');

                if (authStatus.isAuthenticated) {
                    if (nameEl) nameEl.textContent = authStatus.username || 'User';
                    if (subEl) subEl.textContent = authStatus.hasPremium ? 'Premium' : 'Free';
                    if (avatarEl) { const au = resolveAvatarUrl(authStatus.avatar); avatarEl.innerHTML = au && isSafeUrl(au) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(au))}" alt="" onerror="this.outerHTML='<span>&#128100;</span>'">` : '<span>&#128100;</span>'; }
                    if (loginBtn) loginBtn.style.display = 'none';
                    if (logoutBtn) logoutBtn.style.display = 'inline-block';
                    
                    // Render profiles
                    const profiles = authStatus.multiProfile || [];
                    if (profiles.length > 1 && profileSection && profileList) {
                        profileSection.style.display = 'block';
                        profileList.innerHTML = profiles.map(p => `
                            <div style="display:flex; align-items:center; justify-content:space-between; padding:10px; border-bottom:1px solid var(--border-color); ${p.isSelected ? 'background:rgba(244,117,33,0.1);' : ''}">
                                <div style="display:flex; align-items:center; gap:10px;">
                                    <div style="width:32px; height:32px; border-radius:50%; background:var(--bg-tertiary); display:flex; align-items:center; justify-content:center; font-size:0.8em;">
                                        ${p.profileName ? escapeHtml(p.profileName.charAt(0).toUpperCase()) : '?'}
                                    </div>
                                    <div>
                                        <div style="font-weight:500;">${escapeHtml(p.profileName) || 'Unknown'}</div>
                                        <div style="font-size:0.8em; color:var(--text-secondary);">${escapeHtml(p.username || '')}</div>
                                    </div>
                                </div>
                                ${p.isSelected
                                    ? '<span style="color:var(--accent-orange); font-size:0.85em; font-weight:600;">Active</span>'
                                    : `<button class="btn-icon" onclick="switchProfile('${escapeJsString(p.profileId || '')}', ${p.isPinProtected ? 'true' : 'false'})" ${(!p.canSwitch || !p.profileId) ? 'disabled' : ''}>${p.isPinProtected ? '&#128274; ' : ''}Switch</button>`
                                }
                            </div>
                        `).join('');
                    } else if (profileSection) {
                        profileSection.style.display = 'none';
                    }
                } else {
                    if (nameEl) nameEl.textContent = 'Not logged in';
                    if (subEl) subEl.textContent = 'Anonymous mode';
                    if (avatarEl) avatarEl.innerHTML = '<span>&#128100;</span>';
                    if (loginBtn) loginBtn.style.display = 'inline-block';
                    if (logoutBtn) logoutBtn.style.display = 'none';
                    if (profileSection) profileSection.style.display = 'none';
                }
            } catch (e) { console.error('Auth status failed:', e); }
        }

        async function switchProfile(profileId, isPinProtected) {
            try {
                let pin = null;
                if (isPinProtected) {
                    pin = prompt('This profile is protected. Enter its PIN:');
                    if (pin === null) return; // user cancelled
                    pin = pin.trim();
                    if (!pin) { showToast('PIN is required for this profile', 'error'); return; }
                }
                showToast('Switching profile...', 'info');
                const res = await fetch('/api/v1/auth/profiles/switch', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(pin ? { profileId, pin } : { profileId })
                });
                const data = await res.json().catch(() => ({}));
                if (res.ok && data.success) {
                    showToast('Profile switched successfully', 'success');
                    fetchAuthStatus();
                } else {
                    showToast(data.detail || data.message || `Failed to switch profile (HTTP ${res.status})`, 'error');
                }
            } catch (e) {
                showToast('Profile switch error: ' + e.message, 'error');
            }
        }

        function showLoginModal() {
            const modalTitle = document.getElementById('modal-title');
            const modalBody = document.getElementById('modal-body');
            const modalFooter = document.getElementById('modal-footer');
            const modalEl = document.getElementById('modal');
            if (modalTitle) modalTitle.textContent = 'Login to Crunchyroll';
            if (modalBody) modalBody.innerHTML = `
                <div class="form-group">
                    <label class="form-label">Email</label>
                    <input type="email" class="form-input" id="login-email" placeholder="your@email.com" onkeydown="if(event.key==='Enter'){event.preventDefault();doLogin();}">
                </div>
                <div class="form-group">
                    <label class="form-label">Password</label>
                    <div style="display:flex;gap:8px;">
                        <input type="password" class="form-input" id="login-password" placeholder="Password" style="flex:1;" onkeydown="if(event.key==='Enter'){event.preventDefault();doLogin();}">
                        <button type="button" class="btn-icon" onclick="togglePasswordVisibility()" title="Show/Hide Password">
                            <span id="password-toggle-icon">&#128065;</span>
                        </button>
                    </div>
                </div>
                <div id="login-error" style="color:var(--accent-red);font-size:0.9em;margin-top:10px;display:none;"></div>
            `;
            if (modalFooter) modalFooter.innerHTML = `
                <button class="header-btn" onclick="closeModal()">Cancel</button>
                <button class="header-btn primary" id="login-btn" onclick="doLogin()">Login</button>
            `;
            if (modalEl) modalEl.classList.add('active');
            // Focus email field
            setTimeout(() => document.getElementById('login-email')?.focus(), 100);
        }

        function togglePasswordVisibility() {
            const pwInput = document.getElementById('login-password');
            const icon = document.getElementById('password-toggle-icon');
            if (!pwInput) return;
            if (pwInput.type === 'password') {
                pwInput.type = 'text';
                if (icon) icon.textContent = '🙈';
            } else {
                pwInput.type = 'password';
                if (icon) icon.textContent = '👁';
            }
        }

        function validateEmail(email) {
            return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
        }

        async function doLogin() {
            const emailEl = document.getElementById('login-email');
            const passwordEl = document.getElementById('login-password');
            const loginBtn = document.getElementById('login-btn');
            const errorEl = document.getElementById('login-error');
            
            if (!emailEl || !passwordEl) { showToast('Login form not found', 'error'); return; }
            
            const email = emailEl.value.trim();
            const password = passwordEl.value;
            
            // Validation
            if (!email || !password) {
                if (errorEl) { errorEl.textContent = 'Email and password are required'; errorEl.style.display = 'block'; }
                return;
            }
            if (!validateEmail(email)) {
                if (errorEl) { errorEl.textContent = 'Please enter a valid email address'; errorEl.style.display = 'block'; }
                return;
            }
            
            // Loading state
            if (loginBtn) {
                loginBtn.disabled = true;
                loginBtn.textContent = 'Logging in...';
            }
            if (errorEl) errorEl.style.display = 'none';
            
            try {
                const res = await fetch('/api/v1/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ email, password })
                });
                const data = await res.json();
                if (res.ok && data.success) {
                    showToast('Logged in successfully', 'success');
                    closeModal();
                    // Fetch auth status and auto-select profile if needed
                    await fetchAuthStatus();
                    await autoSelectProfileIfNeeded();
                } else {
                    const msg = data.message || 'Login failed';
                    if (errorEl) { errorEl.textContent = msg; errorEl.style.display = 'block'; }
                    else showToast(msg, 'error');
                }
            } catch (e) {
                const msg = 'Login error: ' + e.message;
                if (errorEl) { errorEl.textContent = msg; errorEl.style.display = 'block'; }
                else showToast(msg, 'error');
            } finally {
                if (loginBtn) {
                    loginBtn.disabled = false;
                    loginBtn.textContent = 'Login';
                }
            }
        }

        async function autoSelectProfileIfNeeded() {
            try {
                const res = await fetch('/api/v1/auth/status');
                if (!res.ok) return;
                const status = await res.json();
                
                if (status.isAuthenticated && status.multiProfile && status.multiProfile.length > 1) {
                    // Find the currently selected profile
                    const selectedProfile = status.multiProfile.find(p => p.isSelected);
                    if (selectedProfile && selectedProfile.profileId) {
                        showToast(`Active profile: ${selectedProfile.profileName || selectedProfile.username || 'Default'}`, 'success');
                        return;
                    }
                    // If no profile is selected, auto-select the first available one
                    const firstAvailable = status.multiProfile.find(p => p.canSwitch && p.profileId);
                    if (firstAvailable) {
                        showToast(`Auto-selecting profile: ${firstAvailable.profileName || firstAvailable.username || 'Default'}`, 'info');
                        await switchProfile(firstAvailable.profileId, firstAvailable.isPinProtected);
                    }
                }
            } catch (e) {
                console.warn('Auto profile selection failed:', e);
            }
        }

        async function logout() {
            try {
                const res = await fetch('/api/v1/auth/logout', { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Logged out', 'success');
                fetchAuthStatus();
            } catch (e) { showToast('Logout failed', 'error'); }
        }

        // ================== SETTINGS ==================
        const LANG_OPTIONS = [
            {value:'ja-JP',label:'Japanese'},{value:'en-US',label:'English'},{value:'de-DE',label:'German'},
            {value:'es-ES',label:'Spanish (Spain)'},{value:'es-419',label:'Spanish (Latin America)'},
            {value:'fr-FR',label:'French'},{value:'it-IT',label:'Italian'},{value:'pt-BR',label:'Portuguese (Brazil)'},
            {value:'pt-PT',label:'Portuguese (Portugal)'},{value:'ru-RU',label:'Russian'},{value:'hi-IN',label:'Hindi'},
            {value:'ar-SA',label:'Arabic'},{value:'zh-CN',label:'Chinese (Simplified)'},{value:'ko-KR',label:'Korean'},
            {value:'pl-PL',label:'Polish'},{value:'tr-TR',label:'Turkish'},{value:'th-TH',label:'Thai'},
            {value:'vi-VN',label:'Vietnamese'},{value:'id-ID',label:'Indonesian'},{value:'ms-MY',label:'Malay'},
            {value:'ta-IN',label:'Tamil'},{value:'te-IN',label:'Telugu'}
        ];

        // Calendar language codes match Crunchyroll simulcast calendar filter keys
        const CALENDAR_LANG_OPTIONS = [
            {value:'en-us', label:'English (US)'},
            {value:'es', label:'Spanish'},
            {value:'es-es', label:'Spanish (Spain)'},
            {value:'pt-br', label:'Portuguese (Brazil)'},
            {value:'pt-pt', label:'Portuguese (Portugal)'},
            {value:'fr', label:'French'},
            {value:'de', label:'German'},
            {value:'it', label:'Italian'},
            {value:'ru', label:'Russian'},
            {value:'ar', label:'Arabic'},
            {value:'hi', label:'Hindi'}
        ];

        const STREAM_ENDPOINT_OPTIONS = [
            {value:'tv/android_tv',label:'Android TV'},
            {value:'android/phone',label:'Android Phone'},
            {value:'android/tablet',label:'Android Tablet'},
            {value:'tv/samsung',label:'Samsung TV'},
            {value:'tv/vidaa',label:'Vidaa TV'},
            {value:'web/firefox',label:'Firefox'},
            {value:'web/chrome',label:'Chrome'},
            {value:'web/edge',label:'Edge'},
            {value:'web/fallback',label:'Web Fallback'},
            {value:'console/switch',label:'Nintendo Switch'},
            {value:'console/ps4',label:'PlayStation 4'},
            {value:'console/ps5',label:'PlayStation 5'},
            {value:'console/xbox_one',label:'Xbox One'}
        ];

        // Device labels only - auth credentials managed server-side
        const STREAM_DEVICE_LABELS = {
            'tv/android_tv': 'Android TV',
            'android/phone': 'Android Phone',
            'android/tablet': 'Android Tablet',
            'tv/samsung': 'Samsung TV',
            'tv/vidaa': 'Vidaa TV',
            'web/firefox': 'Firefox',
            'web/chrome': 'Chrome',
            'web/edge': 'Edge',
            'web/fallback': 'Web',
            'console/switch': 'Nintendo Switch',
            'console/ps4': 'PlayStation 4',
            'console/ps5': 'PlayStation 5',
            'console/xbox_one': 'Xbox One'
        };

        function updateStreamDefaults(endpointNum){
            const useDefault = document.getElementById('setting-stream-use-default' + (endpointNum===2?'-2':''))?.checked ?? true;
            const fields = document.querySelectorAll('.stream-field-' + endpointNum);
            fields.forEach(f => {
                f.style.opacity = useDefault ? '0.5' : '1';
                const input = f.querySelector('input');
                if (input) input.disabled = useDefault;
            });
            
            if (useDefault){
                const endpoint = document.getElementById('setting-stream-endpoint' + (endpointNum===2?'-2':''))?.value || 'tv/android_tv';
                const label = STREAM_DEVICE_LABELS[endpoint] || endpoint;
                const deviceTypeEl = document.getElementById('setting-stream-device-type' + (endpointNum===2?'-2':''));
                if (deviceTypeEl) deviceTypeEl.value = label;
                const deviceNameEl = document.getElementById('setting-stream-device-name' + (endpointNum===2?'-2':''));
                if (deviceNameEl) deviceNameEl.value = label;
                // Auth/User-Agent cleared - server handles defaults when useDefault=true
                const authEl = document.getElementById('setting-stream-auth' + (endpointNum===2?'-2':''));
                if (authEl) authEl.value = '';
                const uaEl = document.getElementById('setting-stream-ua' + (endpointNum===2?'-2':''));
                if (uaEl) uaEl.value = '';
            }
        }

        function getMultiSelect(id) {
            const el = document.getElementById(id);
            return el ? Array.from(el.selectedOptions).map(o => o.value) : [];
        }
        function renderListInput(id, items, placeholder) {
            const vals = items || [];
            return `<div class="list-input" id="${id}">` +
                vals.map((v,i) => `<div class="list-row"><input type="text" class="form-input list-item" value="${escapeHtmlAttribute(v||'')}" placeholder="${escapeHtmlAttribute(placeholder||'')}"><button class="header-btn" onclick="this.parentElement.remove()">Remove</button></div>`).join('') +
                `<button class="header-btn" onclick="const d=document.getElementById('${escapeJsString(id)}');const r=document.createElement('div');r.className='list-row';r.innerHTML='<input type=\\'text\\' class=\\'form-input list-item\\' placeholder=\\'${escapeHtmlAttribute(placeholder||'') || ''}\\'><button class=\\'header-btn\\' onclick=\\'this.parentElement.remove()\\'>Remove</button>';d.insertBefore(r,d.lastElementChild)">Add</button></div>`;
        }
        function getListInput(id) {
            const el = document.getElementById(id);
            return el ? Array.from(el.querySelectorAll('.list-item')).map(i => i.value).filter(v => v) : [];
        }

        function renderSettings(container) {
            container.innerHTML = `
                <div class="page-title">Settings</div>
                <div class="page-subtitle">Configure Cruncharr</div>
                <div class="settings-tabs">
                    <div class="settings-cat">
                        <span class="settings-cat-label" title="Settings that affect how the app itself runs and integrates">Application</span>
                        <div class="settings-cat-tabs">
                            <button class="settings-tab ${settingsTab === 'general' ? 'active' : ''}" onclick="setSettingsTab('general', event)">General</button>
                            <button class="settings-tab ${settingsTab === 'download' ? 'active' : ''}" onclick="setSettingsTab('download', event)">Download</button>
                            <button class="settings-tab ${settingsTab === 'queue' ? 'active' : ''}" onclick="setSettingsTab('queue', event)">Queue</button>
                            <button class="settings-tab ${settingsTab === 'history' ? 'active' : ''}" onclick="setSettingsTab('history', event)">History</button>
                            <button class="settings-tab ${settingsTab === 'sonarr' ? 'active' : ''}" onclick="setSettingsTab('sonarr', event)">Sonarr</button>
                            <button class="settings-tab ${settingsTab === 'notifications' ? 'active' : ''}" onclick="setSettingsTab('notifications', event)">Notifications</button>
                            <button class="settings-tab ${settingsTab === 'proxy' ? 'active' : ''}" onclick="setSettingsTab('proxy', event)">Proxy</button>
                            <button class="settings-tab ${settingsTab === 'flaresolverr' ? 'active' : ''}" onclick="setSettingsTab('flaresolverr', event)">FlareSolverr</button>
                            <button class="settings-tab ${settingsTab === 'appearance' ? 'active' : ''}" onclick="setSettingsTab('appearance', event)">Appearance</button>
                        </div>
                    </div>
                    <div class="settings-cat">
                        <span class="settings-cat-label" title="Settings that affect what is fetched from Crunchyroll and how the downloaded files are built">Crunchyroll</span>
                        <div class="settings-cat-tabs">
                            <button class="settings-tab ${settingsTab === 'crunchyroll' ? 'active' : ''}" onclick="setSettingsTab('crunchyroll', event)">Account &amp; Languages</button>
                            <button class="settings-tab ${settingsTab === 'filename' ? 'active' : ''}" onclick="setSettingsTab('filename', event)">Filename</button>
                            <button class="settings-tab ${settingsTab === 'muxing' ? 'active' : ''}" onclick="setSettingsTab('muxing', event)">Muxing</button>
                            <button class="settings-tab ${settingsTab === 'calendar' ? 'active' : ''}" onclick="setSettingsTab('calendar', event)">Calendar</button>
                        </div>
                    </div>
                </div>
                <div id="settings-content">
                    <div class="loading"><div class="spinner"></div>Loading settings...</div>
                </div>
                <div style="margin-top:20px; display:flex; gap:10px; flex-wrap:wrap; align-items:center;">
                    <span class="setting-desc" id="autosave-hint">Changes are saved automatically</span>
                    <button class="header-btn" onclick="resetCurrentTab()" title="Reset only the settings on this tab to their defaults">Reset Tab to Default</button>
                    <button class="header-btn" onclick="resetAllSettings()" title="Reset every setting to default (keeps you logged in)" style="margin-left:auto; color:var(--accent-orange);">Reset ALL Settings</button>
                </div>
            `;
            fetchConfig().then(() => { renderSettingsTab(); attachSettingsAutoSave(); });
        }

        // Auto-save: any change to a control in the settings panel persists the current
        // tab (debounced). Replaces the old explicit "Save Settings" button. The listener
        // lives on the #settings-content container, which survives tab switches.
        function attachSettingsAutoSave() {
            const el = document.getElementById('settings-content');
            if (!el || el._autosaveBound) return;
            el._autosaveBound = true;
            el.addEventListener('change', () => {
                clearTimeout(window._settingsSaveTimer);
                window._settingsSaveTimer = setTimeout(() => { saveSettings(); }, 250);
            });
        }

        async function loadEncodingPresets() {
            const select = document.getElementById('setting-encode-preset');
            if (!select) return;
            try {
                const res = await fetch('/api/v1/encoding/presets');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const presets = await res.json();
                const currentValue = select.value || (config?.download?.encodingPreset || '');
                select.innerHTML = '<option value="">None</option>' +
                    (presets || []).map(p => `<option value="${escapeHtmlAttribute(p)}" ${p === currentValue ? 'selected' : ''}>${escapeHtml(p)}</option>`).join('');
            } catch (e) {
                console.error('Failed to load encoding presets:', e);
            }
        }

        // ===== Encoding preset editor (ports upstream "Edit Preset" dialog) =====
        const PRESET_RESOLUTIONS = [
            ['3840:2160','4K exact (3840:2160)'],['-2:2160','4K keep AR (-2:2160)'],
            ['3440:1440','UWQHD exact (3440:1440)'],['2560:1440','1440p exact (2560:1440)'],
            ['-2:1440','1440p keep AR (-2:1440)'],['2560:1080','UW FHD exact (2560:1080)'],
            ['2160:1080','2:1 exact (2160:1080)'],['1920:1080','1080p exact (1920:1080)'],
            ['-2:1080','1080p keep AR (-2:1080)'],['1920:800','Cinema exact (1920:800)'],
            ['1600:900','900p exact (1600:900)'],['1366:768','768p exact (1366:768)'],
            ['1280:960','SXGA exact (1280:960)'],['1280:720','720p exact (1280:720)'],
            ['-2:720','720p keep AR (-2:720)'],['1024:576','576p exact (1024:576)'],
            ['-2:576','576p keep AR (-2:576)'],['960:540','540p exact (960:540)'],
            ['-2:540','540p keep AR (-2:540)'],['854:480','480p exact (854:480)'],
            ['-2:480','480p keep AR (-2:480)'],['800:600','SVGA exact (800:600)'],
            ['768:432','432p exact (768:432)'],['-2:432','432p keep AR (-2:432)'],
            ['720:480','NTSC exact (720:480)'],['704:576','PAL exact (704:576)'],
            ['640:360','360p exact (640:360)'],['-2:360','360p keep AR (-2:360)'],
            ['426:240','240p exact (426:240)'],['-2:240','240p keep AR (-2:240)'],
        ];

        async function openPresetEditor() {
            const title = document.getElementById('modal-title');
            const body = document.getElementById('modal-body');
            const footer = document.getElementById('modal-footer');
            const modal = document.getElementById('modal');
            if (!title || !body || !footer || !modal) return;
            title.textContent = 'Encoding Presets';
            const resOpts = PRESET_RESOLUTIONS.map(([v, d]) => `<option value="${escapeHtmlAttribute(v)}">${escapeHtml(d)}</option>`).join('');
            body.innerHTML = `
                <div style="display:flex; gap:18px; flex-wrap:wrap;">
                    <div style="flex:1; min-width:200px;">
                        <div style="font-weight:600; margin-bottom:8px;">Custom Presets</div>
                        <div id="preset-list" style="display:flex; flex-direction:column; gap:6px;"><div style="color:var(--text-muted);">Loading…</div></div>
                    </div>
                    <div style="flex:2; min-width:320px; display:flex; flex-direction:column; gap:6px;">
                        <input type="hidden" id="pe-original">
                        <label class="form-label">Enter Preset Name</label>
                        <input class="form-input" id="pe-name" placeholder="H.265 1080p" oninput="updatePresetCmd()">
                        <label class="form-label mt-6">Enter Codec</label>
                        <input class="form-input" id="pe-codec" placeholder="libx265" oninput="updatePresetCmd()">
                        <div class="setting-desc">Leave empty to provide the encoding options through Additional Parameters only.</div>
                        <label class="form-label mt-6">Select Resolution</label>
                        <select class="form-select" id="pe-res" onchange="updatePresetCmd()">${resOpts}</select>
                        <label class="form-label mt-6">Enter Frame Rate</label>
                        <input class="form-input" id="pe-fps" placeholder="24000/1001" oninput="updatePresetCmd()">
                        <label class="form-label mt-6">Enter CRF (0-51) - (cq, global_quality, qp)</label>
                        <input class="form-input" id="pe-crf" type="number" min="0" max="51" value="28" oninput="updatePresetCmd()">
                        <label class="form-label mt-6">Additional Parameters</label>
                        <textarea class="form-input" id="pe-params" rows="3" placeholder="-map 0" oninput="updatePresetCmd()">-map 0</textarea>
                        <div class="setting-desc">One parameter (or flag + value) per line.</div>
                        <label class="form-label mt-6">Generated FFmpeg Command</label>
                        <div id="pe-cmd" style="font-family:monospace; font-size:0.8em; background:var(--bg-tertiary); border:1px solid var(--surface-border); border-radius:var(--radius-sm); padding:8px; word-break:break-all; color:var(--text-secondary);"></div>
                    </div>
                </div>`;
            footer.innerHTML = `
                <button class="header-btn" type="button" onclick="presetEditorReset()">New</button>
                <button class="header-btn primary" type="button" onclick="savePresetEditor()">Save Preset</button>
                <button class="header-btn" type="button" onclick="closeModal()">Close</button>`;
            modal.classList.add('active');
            document.getElementById('pe-res').value = '1920:1080';
            updatePresetCmd();
            await refreshPresetList();
        }

        function presetEditorReset() {
            ['pe-original','pe-name','pe-codec','pe-fps'].forEach(id => { const el = document.getElementById(id); if (el) el.value = ''; });
            const crf = document.getElementById('pe-crf'); if (crf) crf.value = '28';
            const params = document.getElementById('pe-params'); if (params) params.value = '-map 0';
            const res = document.getElementById('pe-res'); if (res) res.value = '1920:1080';
            updatePresetCmd();
        }

        function updatePresetCmd() {
            const el = document.getElementById('pe-cmd');
            if (!el) return;
            const codec = document.getElementById('pe-codec')?.value.trim();
            const res = document.getElementById('pe-res')?.value;
            const fps = document.getElementById('pe-fps')?.value.trim();
            const crf = document.getElementById('pe-crf')?.value.trim();
            const params = (document.getElementById('pe-params')?.value || '').split('\n').map(s => s.trim()).filter(Boolean);
            let parts = ['ffmpeg', '-i', '"input.mkv"'];
            if (codec) parts.push('-c:v', codec);
            if (crf !== '' && crf !== undefined) parts.push('-crf', crf);
            if (res) parts.push('-vf', `scale=${res}`);
            if (fps) parts.push('-r', fps);
            parts.push(...params);
            parts.push('"output.mkv"');
            el.textContent = parts.join(' ');
        }

        async function refreshPresetList() {
            const list = document.getElementById('preset-list');
            if (!list) return;
            try {
                const res = await fetch('/api/v1/encoding/presets/all');
                const all = await res.json();
                const custom = (all || []).filter(p => !p.builtIn);
                if (custom.length === 0) { list.innerHTML = '<div style="color:var(--text-muted); font-size:0.85em;">No custom presets yet.</div>'; return; }
                list.innerHTML = custom.map(p => `
                    <div style="display:flex; align-items:center; justify-content:space-between; gap:8px; padding:6px 8px; background:var(--bg-tertiary); border-radius:var(--radius-sm);">
                        <span style="cursor:pointer; flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" onclick='loadPresetIntoForm(${JSON.stringify(p)})'>${escapeHtml(p.presetName)}</span>
                        <button class="btn-icon danger" title="Delete" onclick="deletePresetEditor('${escapeJsString(p.presetName)}')">&#128465;</button>
                    </div>`).join('');
            } catch (e) { list.innerHTML = '<div style="color:var(--accent-red); font-size:0.85em;">Failed to load presets</div>'; }
        }

        function loadPresetIntoForm(p) {
            document.getElementById('pe-original').value = p.presetName || '';
            document.getElementById('pe-name').value = p.presetName || '';
            document.getElementById('pe-codec').value = p.codec || '';
            document.getElementById('pe-fps').value = p.frameRate || '';
            document.getElementById('pe-crf').value = (p.crf ?? 28);
            document.getElementById('pe-params').value = (p.additionalParameters || []).join('\n');
            const res = document.getElementById('pe-res');
            if (res) res.value = p.resolution || '1920:1080';
            updatePresetCmd();
        }

        async function savePresetEditor() {
            const name = document.getElementById('pe-name')?.value.trim();
            if (!name) { showToast('Preset name required', 'error'); return; }
            const crf = parseInt(document.getElementById('pe-crf')?.value, 10);
            if (isNaN(crf) || crf < 0 || crf > 51) { showToast('CRF must be 0–51', 'error'); return; }
            const preset = {
                presetName: name,
                codec: document.getElementById('pe-codec')?.value.trim() || '',
                resolution: document.getElementById('pe-res')?.value || '',
                frameRate: document.getElementById('pe-fps')?.value.trim() || '',
                crf: crf,
                additionalParameters: (document.getElementById('pe-params')?.value || '').split('\n').map(s => s.trim()).filter(Boolean)
            };
            try {
                const res = await fetch('/api/v1/encoding/presets', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(preset) });
                if (!res.ok) { const e = await res.json().catch(() => ({})); throw new Error(e.error || `HTTP ${res.status}`); }
                showToast('Preset saved', 'success');
                document.getElementById('pe-original').value = name;
                await refreshPresetList();
                await loadEncodingPresets();
            } catch (e) { showToast('Save failed: ' + e.message, 'error'); }
        }

        async function deletePresetEditor(name) {
            try {
                const res = await fetch('/api/v1/encoding/presets/' + encodeURIComponent(name), { method: 'DELETE' });
                if (!res.ok && res.status !== 204) { const e = await res.json().catch(() => ({})); throw new Error(e.error || `HTTP ${res.status}`); }
                showToast('Preset deleted', 'success');
                if (document.getElementById('pe-original')?.value === name) presetEditorReset();
                await refreshPresetList();
                await loadEncodingPresets();
            } catch (e) { showToast('Delete failed: ' + e.message, 'error'); }
        }

        function setSettingsTab(tab, evt) {
            settingsTab = tab;
            document.querySelectorAll('.settings-tab').forEach(t => t.classList.remove('active'));
            if (evt && evt.target) evt.target.classList.add('active');
            renderSettingsTab();
            populateHwAccels();
        }

        // Populate the Sync HW Accel dropdown with the accelerators ffmpeg supports and
        // the GPU devices actually passed into the container. No-op unless the select is
        // on the current tab.
        async function populateHwAccels() {
            const sel = document.getElementById('setting-sync-hwaccel');
            if (!sel) return;
            const saved = sel.dataset.saved || sel.value || 'none';
            try {
                const res = await fetch('/api/v1/hardware/accelerators');
                if (!res.ok) return;
                const opts = await res.json();
                if (!Array.isArray(opts) || opts.length === 0) return;
                // Keep the saved value selectable even if that GPU is no longer present
                if (saved && saved !== 'none' && !opts.some(o => o.value === saved)) {
                    opts.push({ value: saved, label: saved + ' (saved — not currently available)' });
                }
                sel.innerHTML = opts.map(o =>
                    `<option value="${escapeHtmlAttribute(o.value)}" ${o.value === saved ? 'selected' : ''}>${escapeHtml(o.label)}</option>`
                ).join('');
            } catch (e) { /* keep the single saved option on failure */ }
        }

        // Helper: read stream endpoint property (backend may return PascalCase or camelCase)
        function getStreamProp(endpoint, key, fallback) {
            if (!endpoint) return fallback;
            // Check camelCase first, then PascalCase
            const val = endpoint[key] !== undefined ? endpoint[key] : endpoint[key.charAt(0).toUpperCase() + key.slice(1)];
            return val !== undefined ? val : fallback;
        }

        function renderSettingsTab() {
            const content = document.getElementById('settings-content');
            if (!content) return;
            const dl = config?.download || {};
            const cr = config?.crunchyroll || {};
            const q = config?.queue || {};
            const h = config?.history || {};
            const s = config?.sonarr || {};
            const n = config?.notifications || {};
            const p = config?.proxy || {};
            const f = config?.flareSolverr || {};
            const a = config?.appearance || {};
            const cal = config?.calendar || {};
            const ad = config?.addDownload || {};
            const g = config?.general || {};
            
            switch(settingsTab) {
                case 'general':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">General</span><span class="settings-section-desc">General application settings</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Log Mode</div><div class="setting-desc">Enable verbose logging</div></div><label class="toggle-switch"><input type="checkbox" id="setting-log-mode" ${g.logMode?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Remove Finished Downloads</div><div class="setting-desc">Auto-remove completed items from queue</div></div><label class="toggle-switch"><input type="checkbox" id="setting-remove-finished" ${g.removeFinishedDownload?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Token File Path</div></div><input type="text" class="form-input w-300" id="setting-token-path" value="${escapeHtmlAttribute(g.tokenFilePath||'')}"></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'crunchyroll':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Crunchyroll Settings</span><span class="settings-section-desc">Crunchyroll-specific options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Use Beta API</div><div class="setting-desc">Use Crunchyroll's newer (beta) API endpoints</div></div><label class="toggle-switch"><input type="checkbox" id="setting-use-beta-api" ${cr.useBetaApi?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Mark as Watched</div><div class="setting-desc">Mark downloaded episodes as watched on Crunchyroll</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mark-watched" ${cr.markAsWatched?'checked':''}><span class="toggle-slider"></span></label></div>

                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Dub Languages</span><span class="settings-section-desc">Select languages to download</span></div>
                            <div class="settings-section-body">
                                <select class="form-select" id="setting-dub-langs" multiple style="min-width:180px; min-height:80px;">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${(dl.dubLanguages||['ja-JP']).includes(o.value)?'selected':''}>${o.label}</option>`).join('')}</select>
                                <div class="setting-row mt-10"><div><div class="setting-label">Download Audio Description (AD)</div><div class="setting-desc">Download audio-description tracks for the selected dubs, if available</div></div><label class="toggle-switch"><input type="checkbox" id="setting-dl-ad" ${dl.downloadDescriptionAudio?'checked':''}><span class="toggle-slider"></span></label></div>

                                <div class="setting-row"><div><div class="setting-label">Download Multiple Dubs</div><div class="setting-desc">Download all selected dubs per episode</div></div><label class="toggle-switch"><input type="checkbox" id="setting-download-multi-dub" ${dl.downloadMultipleDubs?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Download First Available Dub</div><div class="setting-desc">Only download the first available dub by priority instead of all selected</div></div><label class="toggle-switch"><input type="checkbox" id="setting-first-dub" ${dl.downloadFirstAvailableDub?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Hardsubs</span><span class="settings-section-desc">Hardsub language settings</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Hardsubs Language</div></div><select class="form-select mw-150" id="setting-hard-sub"><option value="none" ${dl.hardSubLang==='none'?'selected':''}>none</option>${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${dl.hardSubLang===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">No-hardsubs fallback</div><div class="setting-desc">If no hardsubs exist for the chosen language, download the raw (no-hardsub) video instead</div></div><label class="toggle-switch"><input type="checkbox" id="setting-hard-fallback" ${dl.hardSubRawFallback?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Softsubs</span><span class="settings-section-desc">Subtitle download options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Softsubs Languages</div></div></div>
                                <select class="form-select" id="setting-soft-subs" multiple style="min-width:180px; min-height:80px;">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${(dl.softSubs||['en-US']).includes(o.value)?'selected':''}>${o.label}</option>`).join('')}</select>

                                <div class="setting-row"><div><div class="setting-label">Add ScaledBorderAndShadow</div></div><select class="form-select mw-150" id="setting-scaled-border"><option value="DontAdd" ${dl.subsAddScaledBorder==='DontAdd'?'selected':''}>Don't Add</option><option value="ScaledBorderAndShadowYes" ${dl.subsAddScaledBorder==='ScaledBorderAndShadowYes'?'selected':''}>yes</option><option value="ScaledBorderAndShadowNo" ${dl.subsAddScaledBorder==='ScaledBorderAndShadowNo'?'selected':''}>no</option></select></div>

                                <div class="setting-row"><div><div class="setting-label">Signs as forced in mkv</div><div class="setting-desc">Flag Signs subtitles as "forced" in the MKV</div></div><label class="toggle-switch"><input type="checkbox" id="setting-signs-forced" ${dl.signsSubsAsForced?'checked':''}><span class="toggle-slider"></span></label></div>

                                <div class="setting-row"><div><div class="setting-label">CC as hearing impaired in mkv</div><div class="setting-desc">Flag CC subtitles as hearing-impaired (SDH) in the MKV</div></div><label class="toggle-switch"><input type="checkbox" id="setting-cc-hi" ${dl.ccSubsMuxingFlag?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Convert CC VTT to ASS</div><div class="setting-desc">Convert closed-caption WEBVTT subtitles into ASS format</div></div><label class="toggle-switch"><input type="checkbox" id="setting-cc-convert" ${dl.convertVttToAss?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">CC Font</div><div class="setting-desc">Font applied when CC subtitles are converted to ASS</div></div><input type="text" class="form-input w-200" id="setting-cc-font" value="${escapeHtmlAttribute(dl.ccSubsFont||'Trebuchet MS')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Include Signs Subtitles</div><div class="setting-desc">Download signs/forced-only subtitle tracks</div></div><label class="toggle-switch"><input type="checkbox" id="setting-include-signs" ${dl.includeSignsSubs?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Include CC Subtitles</div><div class="setting-desc">Download Closed Caption tracks</div></div><label class="toggle-switch"><input type="checkbox" id="setting-include-cc" ${dl.includeCcSubs?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Download Duplicate Subs</div><div class="setting-desc">Allow the same subtitle to be downloaded per dub</div></div><label class="toggle-switch"><input type="checkbox" id="setting-subs-dup" ${dl.subsDownloadDuplicate?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Fix CCC Subtitles</div><div class="setting-desc">Clean up Closed Caption Converter output</div></div><label class="toggle-switch"><input type="checkbox" id="setting-fix-ccc" ${dl.fixCccSubtitles?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Skip Subtitles</div><div class="setting-desc">Do not download any subtitles</div></div><label class="toggle-switch"><input type="checkbox" id="setting-skip-subs" ${dl.skipSubs?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">CC Tag</div></div><input type="text" class="form-input w-120" id="setting-cc-tag" value="${escapeHtmlAttribute(dl.ccTag||'CC')}"></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'download':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Download Settings</span><span class="settings-section-desc">Adjust download behavior</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Output Directory</div><div class="setting-desc">Where downloads are saved</div></div><input type="text" class="form-input w-250" id="setting-output-dir" value="${escapeHtmlAttribute(dl.outputDirectory||'/downloads')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Temp Directory</div><div class="setting-desc">Working dir for downloads + muxing/transcoding when "Use Temp Folder" is on. Point at a RAM disk (tmpfs) to keep that I/O off your SSD.</div></div><input type="text" class="form-input w-250" id="setting-temp-dir" value="${escapeHtmlAttribute(dl.tempDirectory||'/tmp/cruncharr')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Use Temp Folder</div><div class="setting-desc">Download, mux and encode in the Temp Directory, then move the finished file to the output folder. Off = work directly in the output folder.</div></div><label class="toggle-switch"><input type="checkbox" id="setting-use-temp" ${dl.useTempFolder?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Part Size</div><div class="setting-desc">Download chunk size in MB (larger = fewer requests, more memory)</div></div><input type="number" class="form-input w-80" id="setting-part-size" value="${escapeHtmlAttribute(dl.partSize ?? 10)}" min="1"></div>
                                <div class="setting-row"><div><div class="setting-label">Download Delay (seconds)</div><div class="setting-desc">Delay before each download</div></div><input type="number" class="form-input w-100" id="setting-download-delay" value="${escapeHtmlAttribute(dl.downloadDelaySeconds||0)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Delay Between Dubs</div><div class="setting-desc">Apply the delay per dub instead of per episode</div></div><label class="toggle-switch"><input type="checkbox" id="setting-download-delay-dub-based" ${dl.downloadDelayUseDubBased?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Cooldown Between Downloads (seconds)</div><div class="setting-desc">Delay before starting next download</div></div><input type="number" class="form-input w-100" id="setting-cooldown" value="${escapeHtmlAttribute(dl.cooldownDelaySeconds||0)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Simultaneous Downloads</div></div><input type="number" class="form-input w-80" id="setting-concurrent" value="${escapeHtmlAttribute(dl.simultaneousDownloads||2)}" min="1" max="10"></div>
                                <div class="setting-row"><div><div class="setting-label">Simultaneous Processing Jobs</div></div><input type="number" class="form-input w-80" id="setting-proc-jobs" value="${escapeHtmlAttribute(dl.simultaneousProcessingJobs||2)}" min="1"></div>
                                <div class="setting-row"><div><div class="setting-label">Download Speed Limit (KB/s)</div><div class="setting-desc">0 = unlimited</div></div><input type="number" class="form-input w-120" id="setting-speed-limit" value="${escapeHtmlAttribute(dl.downloadSpeedLimit||0)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Show Speed in Bits (Mbps)</div><div class="setting-desc">Display transfer speeds as Mbps instead of MB/s</div></div><label class="toggle-switch"><input type="checkbox" id="setting-speed-bits" ${dl.downloadSpeedInBits?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Retry Attempts</div></div><input type="number" class="form-input w-80" id="setting-retry" value="${escapeHtmlAttribute(dl.retryAttempts||5)}" min="1"></div>
                                <div class="setting-row"><div><div class="setting-label">Retry Delay (seconds)</div></div><input type="number" class="form-input w-80" id="setting-retry-delay" value="${escapeHtmlAttribute(dl.retryDelay||5)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Playback Rate Limit Retry Delay (seconds)</div></div><input type="number" class="form-input w-100" id="setting-rate-limit-delay" value="${escapeHtmlAttribute(dl.playbackRateLimitRetryDelaySeconds||30)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Retry Max Delay (seconds)</div></div><input type="number" class="form-input w-100" id="setting-retry-max-delay" value="${escapeHtmlAttribute(dl.retryMaxDelaySeconds ?? 300)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Use New Download Method</div><div class="setting-desc">Updated download handling; may improve performance and stability</div></div><label class="toggle-switch"><input type="checkbox" id="setting-new-method" ${dl.downloadMethodeNew?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Replace Existing Files</div><div class="setting-desc">Overwrite an existing output file instead of creating a numbered copy</div></div><label class="toggle-switch"><input type="checkbox" id="setting-replace-existing" ${dl.replaceExistingFiles?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Stream Endpoints</span><span class="settings-section-desc">Stream endpoint configuration</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Primary Endpoint</div></div><select class="form-select mw-150" id="setting-stream-endpoint" onchange="updateStreamDefaults(1)">${STREAM_ENDPOINT_OPTIONS.map(o=>`<option value="${o.value}" ${(getStreamProp(cr.streamEndpoint,'endpoint','tv/android_tv'))===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Primary Use Default</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-use-default" ${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'checked':''} onchange="updateStreamDefaults(1)"><span class="toggle-slider"></span></label></div>
                                <div class="setting-row stream-field-1" style="opacity:${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Primary Auth</div></div><input type="text" class="form-input w-200" id="setting-stream-auth" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpoint,'authorization','')):(getStreamProp(cr.streamEndpoint,'authorization','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpoint,'authorization','Authorization'))}" ${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-1" style="opacity:${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Primary User-Agent</div></div><input type="text" class="form-input w-250" id="setting-stream-ua" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpoint,'userAgent','')):(getStreamProp(cr.streamEndpoint,'userAgent','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpoint,'userAgent','User-Agent'))}" ${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-1" style="opacity:${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Primary Device Type</div></div><input type="text" class="form-input w-200" id="setting-stream-device-type" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpoint,'deviceType','')):(getStreamProp(cr.streamEndpoint,'deviceType','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpoint,'deviceType','Device Type'))}" ${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-1" style="opacity:${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Primary Device Name</div></div><input type="text" class="form-input w-200" id="setting-stream-device-name" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpoint,'deviceName','')):(getStreamProp(cr.streamEndpoint,'deviceName','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpoint,'deviceName','Device Name'))}" ${getStreamProp(cr.streamEndpoint,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row"><div><div class="setting-label">Primary Video</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-video" ${getStreamProp(cr.streamEndpoint,'video',true)!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Primary Audio</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-audio" ${getStreamProp(cr.streamEndpoint,'audio',true)!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Secondary Endpoint</div></div><select class="form-select mw-150" id="setting-stream-endpoint-2" onchange="updateStreamDefaults(2)"><option value="" ${!getStreamProp(cr.streamEndpointSecondary,'endpoint','')?'selected':''}>None</option>${STREAM_ENDPOINT_OPTIONS.map(o=>`<option value="${o.value}" ${(getStreamProp(cr.streamEndpointSecondary,'endpoint',''))===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Secondary Use Default</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-use-default-2" ${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'checked':''} onchange="updateStreamDefaults(2)"><span class="toggle-slider"></span></label></div>
                                <div class="setting-row stream-field-2" style="opacity:${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Secondary Auth</div></div><input type="text" class="form-input w-200" id="setting-stream-auth-2" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpointSecondary,'authorization','')):(getStreamProp(cr.streamEndpointSecondary,'authorization','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpointSecondary,'authorization','Authorization'))}" ${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-2" style="opacity:${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Secondary User-Agent</div></div><input type="text" class="form-input w-250" id="setting-stream-ua-2" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpointSecondary,'userAgent','')):(getStreamProp(cr.streamEndpointSecondary,'userAgent','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpointSecondary,'userAgent','User-Agent'))}" ${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-2" style="opacity:${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Secondary Device Type</div></div><input type="text" class="form-input w-200" id="setting-stream-device-type-2" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpointSecondary,'deviceType','')):(getStreamProp(cr.streamEndpointSecondary,'deviceType','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpointSecondary,'deviceType','Device Type'))}" ${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row stream-field-2" style="opacity:${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'0.5':'1'}"><div><div class="setting-label">Secondary Device Name</div></div><input type="text" class="form-input w-200" id="setting-stream-device-name-2" value="${escapeHtmlAttribute(getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?(getStreamProp(cr.defaultStreamEndpointSecondary,'deviceName','')):(getStreamProp(cr.streamEndpointSecondary,'deviceName','')))}" placeholder="${escapeHtmlAttribute(getStreamProp(cr.defaultStreamEndpointSecondary,'deviceName','Device Name'))}" ${getStreamProp(cr.streamEndpointSecondary,'useDefault',true)!==false?'disabled':''}></div>
                                <div class="setting-row"><div><div class="setting-label">Secondary Video</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-video-2" ${getStreamProp(cr.streamEndpointSecondary,'video',true)!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Secondary Audio</div></div><label class="toggle-switch"><input type="checkbox" id="setting-stream-audio-2" ${getStreamProp(cr.streamEndpointSecondary,'audio',true)!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Video / Audio</span><span class="settings-section-desc">Media download options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Download Video</div></div><label class="toggle-switch"><input type="checkbox" id="setting-dl-video" ${!dl.noVideo?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Download Video for every dub</div><div class="setting-desc">Re-download the video for each dub instead of reusing one video file</div></div><label class="toggle-switch"><input type="checkbox" id="setting-dl-video-every" ${!dl.dlVideoOnce?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Keep files separate</div><div class="setting-desc">Create a separate output file per dub language instead of one multi-audio file</div></div><label class="toggle-switch"><input type="checkbox" id="setting-keep-separate" ${dl.keepDubsSeparate?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Video Quality</div></div><select class="form-select mw-150" id="setting-quality-video"><option value="best" ${dl.qualityVideo==='best'?'selected':''}>Best Available</option><option value="1080" ${dl.qualityVideo==='1080'?'selected':''}>1080</option><option value="720" ${dl.qualityVideo==='720'?'selected':''}>720</option><option value="480" ${dl.qualityVideo==='480'?'selected':''}>480</option><option value="360" ${dl.qualityVideo==='360'?'selected':''}>360</option><option value="240" ${dl.qualityVideo==='240'?'selected':''}>240</option><option value="worst" ${dl.qualityVideo==='worst'?'selected':''}>Worst</option></select></div>
                                <div class="setting-row"><div><div class="setting-label">Download Audio</div></div><label class="toggle-switch"><input type="checkbox" id="setting-dl-audio" ${!dl.noAudio?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Audio Quality</div></div><select class="form-select mw-150" id="setting-quality-audio"><option value="best" ${dl.qualityAudio==='best'?'selected':''}>Best Available</option><option value="192kB/s" ${dl.qualityAudio==='192kB/s'?'selected':''}>192kB/s</option><option value="128kB/s" ${dl.qualityAudio==='128kB/s'?'selected':''}>128kB/s</option><option value="96kB/s" ${dl.qualityAudio==='96kB/s'?'selected':''}>96kB/s</option><option value="64kB/s" ${dl.qualityAudio==='64kB/s'?'selected':''}>64kB/s</option><option value="worst" ${dl.qualityAudio==='worst'?'selected':''}>Worst</option></select></div>
                                <div class="setting-row"><div><div class="setting-label">Chapters</div><div class="setting-desc">Include chapter markers (intro/outro) in the file</div></div><label class="toggle-switch"><input type="checkbox" id="setting-chapters" ${dl.includeChapters?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'filename':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Filename Settings</span><span class="settings-section-desc">Configure output filenames</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Leading zeros for seasons/episodes</div><div class="setting-desc">Pad season/episode numbers to this many digits (2 → E01)</div></div><input type="number" class="form-input w-80" id="setting-leading-zeros" value="${escapeHtmlAttribute(dl.leadingNumbers ?? 2)}" min="1"></div>
                                <div class="setting-row"><div><div class="setting-label">Filename Whitespace Substitute</div><div class="setting-desc">Replace spaces in filenames with this character (blank = keep spaces)</div></div><input type="text" class="form-input w-100" id="setting-whitespace-sub" value="${escapeHtmlAttribute(dl.filenameWhitespaceSubstitute||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Filename Template (\${var} syntax)</div><div class="setting-desc">Variables: \${seriesTitle}, \${episodeTitle}, \${season}, \${episode}, \${seasonTitle}, \${height}, \${width}, \${quality}, \${dubs}, \${audioLang}, \${seriesId}, \${seasonId}, \${episodeId}</div></div><input type="text" class="form-input w-400" id="setting-filename" value="${escapeHtmlAttribute(dl.filename||'\\${seriesTitle} - S\\${season}E\\${episode} [\\${height}p]')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Filename Template ({var:00} syntax)</div><div class="setting-desc">Advanced: {seriesTitle}, {season:00}, {episode:00}, {height}, {width}, {episodeTitle}, {seasonTitle}</div></div><input type="text" class="form-input w-400" id="setting-filename-template" value="${escapeHtmlAttribute(dl.filenameTemplate||'')}" placeholder="Leave empty to use default above"></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'muxing':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Muxing Settings</span><span class="settings-section-desc">Output and muxing options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Skip Muxing</div></div><label class="toggle-switch"><input type="checkbox" id="setting-skip-mux" ${dl.skipMuxing?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">MP4 output</div><div class="setting-desc">Output MP4 instead of MKV — not recommended (no soft subs/fonts)</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mux-mp4" ${dl.muxMp4?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">MP3 output</div><div class="setting-desc">Output MP3 instead of MKV/MP4 when only audio was downloaded</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mux-mp3" ${dl.muxAudioOnlyToMp3?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Keep Subtitles separate</div><div class="setting-desc">Save subtitles as external files instead of muxing them into the video</div></div><label class="toggle-switch"><input type="checkbox" id="setting-skip-sub-mux" ${dl.skipSubMux?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Default Video</div><div class="setting-desc">Language flagged as the default video track in the muxed file</div></div><select class="form-select mw-150" id="setting-default-video"><option value="none" ${(dl.defaultVideo==='none'||!dl.defaultVideo)?'selected':''}>none</option>${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${dl.defaultVideo===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Default Audio</div><div class="setting-desc">Language flagged as the default audio track in the muxed file</div></div><select class="form-select mw-150" id="setting-default-audio">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${(dl.defaultAudio||dl.muxDefaultDub)===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Default Subtitle</div><div class="setting-desc">Language flagged as the default subtitle track in the muxed file</div></div><select class="form-select mw-150" id="setting-default-sub">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${(dl.defaultSub||dl.muxDefaultSub)===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Default Subtitle Signs</div><div class="setting-desc">Use the Signs/Songs subtitle as the default track instead</div></div><label class="toggle-switch"><input type="checkbox" id="setting-default-sub-signs" ${dl.muxDefaultSubSigns?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Force Default Subtitle Display</div><div class="setting-desc">Marks the default subtitle as forced so it shows automatically during playback</div></div><label class="toggle-switch"><input type="checkbox" id="setting-default-sub-forced" ${dl.muxDefaultSubForcedDisplay?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Include Fonts</div><div class="setting-desc">Embed subtitle fonts into the MKV</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mux-fonts" ${dl.muxFonts?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Include Typesetting Fonts</div><div class="setting-desc">Also embed fonts used for on-screen typesetting/signs</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mux-ts-fonts" ${dl.muxTypesettingFonts?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Include episode thumbnail</div><div class="setting-desc">Embed the episode thumbnail into the MKV as cover art</div></div><label class="toggle-switch"><input type="checkbox" id="setting-mux-cover" ${dl.muxCover?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">File title</div><div class="setting-desc">Internal title metadata stored in the file (not the filename)</div></div><input type="text" class="form-input w-300" id="setting-video-title" value="${escapeHtmlAttribute(dl.videoTitle||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Include Episode description</div></div><label class="toggle-switch"><input type="checkbox" id="setting-include-desc" ${dl.includeVideoDescription?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Episode description Language</div></div><select class="form-select mw-150" id="setting-desc-lang">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${dl.descriptionLang===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Sync Dub Timings</div><div class="setting-desc">Align alternate dub audio to the video (only works for episodes that differ by intro length)</div></div><label class="toggle-switch"><input type="checkbox" id="setting-sync-timing" ${dl.syncTiming?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Sync Full Quality Fallback</div><div class="setting-desc">Use full quality if sync fails</div></div><label class="toggle-switch"><input type="checkbox" id="setting-sync-fallback" ${dl.syncTimingFullQualityFallback?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Sync HW Accel</div><div class="setting-desc">Hardware acceleration for sync (GPUs the container can access)</div></div><select class="form-select mw-240" id="setting-sync-hwaccel" data-saved="${escapeHtmlAttribute(dl.syncHwAccel||'none')}"><option value="${escapeHtmlAttribute(dl.syncHwAccel||'none')}">${escapeHtml(dl.syncHwAccel||'none')}</option></select></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Additional Options</span><span class="settings-section-desc">Extra muxing options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Additional MKVMerge Options</div></div></div>
                                ${renderListInput('setting-mkvmerge', dl.mkvmergeOptions, '--option')}
                                <div class="setting-row mt-10"><div><div class="setting-label">Additional FFMpeg Options</div></div></div>
                                ${renderListInput('setting-ffmpeg', dl.ffmpegOptions, '-option')}
                                <div class="setting-row mt-10"><div><div class="setting-label">Encoding: Enable</div></div><label class="toggle-switch"><input type="checkbox" id="setting-encode" ${dl.encodeEnabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Encoding Preset</div></div><div style="display:flex; gap:8px; align-items:center;"><select class="form-select mw-200" id="setting-encode-preset" onfocus="loadEncodingPresets()"><option value="">${dl.encodingPreset || 'None'}</option></select><button class="header-btn" type="button" onclick="openPresetEditor()">Manage Presets</button></div></div>
                            </div>
                        </div>
                    `;
                    // Load encoding presets after rendering so the select is populated
                    setTimeout(() => loadEncodingPresets(), 0);
                    break;
                case 'queue':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Queue Settings</span><span class="settings-section-desc">Queue behavior options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Persist Queue</div><div class="setting-desc">Save queue on exit</div></div><label class="toggle-switch"><input type="checkbox" id="setting-persist-queue" ${q.persistQueue?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Auto Download</div><div class="setting-desc">Start downloads automatically</div></div><label class="toggle-switch"><input type="checkbox" id="setting-auto-download" ${q.autoDownload?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Allow Early Start</div><div class="setting-desc">Start next download early</div></div><label class="toggle-switch"><input type="checkbox" id="setting-queue-early-start" ${dl.downloadAllowEarlyStart?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Skip Missing Languages</div><div class="setting-desc">Only queue if all selected languages available</div></div><label class="toggle-switch"><input type="checkbox" id="setting-queue-skip-missing" ${dl.downloadOnlyWithAllSelectedDubSub?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Simultaneous Processing Jobs</div></div><input type="number" class="form-input w-80" id="setting-queue-proc-jobs" value="${escapeHtmlAttribute(q.simultaneousProcessingJobs||2)}" min="1"></div>
                                <div class="setting-row"><div><div class="setting-label">Queue File Path</div></div><input type="text" class="form-input w-250" id="setting-queue-path" value="${escapeHtmlAttribute(q.queueFilePath||'Cruncharr/queue.json')}"></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'history':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">History Settings</span><span class="settings-section-desc">History tracking options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-history-enabled" ${h.enabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Include CR Artists</div></div><label class="toggle-switch"><input type="checkbox" id="setting-cr-artists" ${h.includeCrArtists?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Remove Missing Episodes</div></div><label class="toggle-switch"><input type="checkbox" id="setting-remove-missing" ${h.removeMissingEpisodes?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Check Partial Downloads</div><div class="setting-desc">Track missing dubs/subs on downloaded episodes</div></div><label class="toggle-switch"><input type="checkbox" id="setting-history-partial" ${h.checkPartialDownloads!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">History Language</div></div><select class="form-select mw-150" id="setting-history-lang">${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${h.lang===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Auto Refresh Interval (minutes)</div></div><input type="number" class="form-input w-100" id="setting-history-interval" value="${escapeHtmlAttribute(h.autoRefreshIntervalMinutes||0)}" min="0"></div>
                                <div class="setting-row"><div><div class="setting-label">Auto Refresh Mode</div></div><select class="form-select mw-150" id="setting-history-mode"><option value="0" ${h.autoRefreshMode===0?'selected':''}>Default All</option><option value="1" ${h.autoRefreshMode===1?'selected':''}>Default Active</option><option value="50" ${h.autoRefreshMode===50?'selected':''}>Fast New Releases</option></select></div>

                            </div>
                        </div>
                    `;
                    break;
                case 'sonarr':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Sonarr Settings</span><span class="settings-section-desc">Sonarr integration</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-sonarr-enabled" ${s.enabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Host</div></div><input type="text" class="form-input w-200" id="setting-sonarr-host" value="${escapeHtmlAttribute(s.host||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Port</div></div><input type="number" class="form-input w-100" id="setting-sonarr-port" value="${escapeHtmlAttribute(s.port||0)}"></div>
                                <div class="setting-row"><div><div class="setting-label">API Key</div></div><input type="text" class="form-input w-250" id="setting-sonarr-apikey" value="${escapeHtmlAttribute(s.apiKey||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Use SSL</div></div><label class="toggle-switch"><input type="checkbox" id="setting-sonarr-ssl" ${s.useSsl?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">URL Base</div></div><input type="text" class="form-input w-200" id="setting-sonarr-urlbase" value="${escapeHtmlAttribute(s.urlBase||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Use Sonarr Numbering</div><div class="setting-desc">Use Sonarr's season/episode numbers in filenames</div></div><label class="toggle-switch"><input type="checkbox" id="setting-sonarr-numbering" ${s.useSonarrNumbering?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Connection</div><div class="setting-desc">Verify host, port and API key against your Sonarr server</div></div><button class="header-btn" id="sonarr-test-btn" onclick="testSonarrConnection()">Test Connection</button></div>
                                <div class="setting-row" id="sonarr-test-result" style="display:none;"><div id="sonarr-test-msg" style="font-size:0.85em;"></div></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'notifications':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Webhook Configuration</span><span class="settings-section-desc">Configure webhook notifications</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Webhook URL</div></div><input type="text" class="form-input w-300" id="setting-webhook-url" value="${escapeHtmlAttribute(n.webhookUrl||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Webhook Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-webhook-enabled" ${n.webhookEnabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Webhook Method</div></div><select class="form-select mw-100" id="setting-webhook-method"><option value="POST" ${n.webhookMethod==='POST'?'selected':''}>POST</option><option value="GET" ${n.webhookMethod==='GET'?'selected':''}>GET</option></select></div>
                                <div class="setting-row"><div><div class="setting-label">Webhook Content-Type</div></div><input type="text" class="form-input w-200" id="setting-webhook-ct" value="${escapeHtmlAttribute(n.webhookContentType||'application/json')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Webhook Headers</div><div class="setting-desc">One header per line (Key: Value)</div></div></div>
                                <textarea class="form-input w-300" id="setting-webhook-headers" rows="3">${escapeHtml(n.webhookHeaders?Object.entries(n.webhookHeaders).map(([k,v])=>k+': '+v).join('\n'):'')}</textarea>
                                <div class="setting-row"><div><div class="setting-label">Webhook Body Template</div></div><textarea class="form-input w-300" id="setting-webhook-body" rows="3">${escapeHtml(n.webhookBodyTemplate||'')}</textarea></div>
                                <div class="mt-10">
                                    <button class="header-btn" onclick="testWebhook()">Test Webhook</button>
                                </div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Notification Events</span><span class="settings-section-desc">Select events to trigger webhook</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Queue Finished</div></div><label class="toggle-switch"><input type="checkbox" id="setting-notify-queue-finished" ${n.notifyQueueFinished?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Download Finished</div></div><label class="toggle-switch"><input type="checkbox" id="setting-notify-download-finished" ${n.notifyDownloadFinished?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Download Failed</div></div><label class="toggle-switch"><input type="checkbox" id="setting-notify-download-failed" ${n.notifyDownloadFailed?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Update Available</div></div><label class="toggle-switch"><input type="checkbox" id="setting-notify-update-available" ${n.notifyUpdateAvailable?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'proxy':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Proxy Settings</span><span class="settings-section-desc">Configure proxy connection</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-proxy-enabled" ${p.enabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Proxy All Traffic</div><div class="setting-desc">Off = only Crunchyroll traffic uses the proxy</div></div><label class="toggle-switch"><input type="checkbox" id="setting-proxy-all-traffic" ${p.allTraffic!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">SOCKS Proxy</div></div><label class="toggle-switch"><input type="checkbox" id="setting-proxy-socks" ${p.socks?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Host</div></div><input type="text" class="form-input w-200" id="setting-proxy-host" value="${escapeHtmlAttribute(p.host||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Port</div></div><input type="number" class="form-input w-100" id="setting-proxy-port" value="${escapeHtmlAttribute(p.port||0)}"></div>
                                <div class="setting-row"><div><div class="setting-label">Username</div></div><input type="text" class="form-input w-200" id="setting-proxy-user" value="${escapeHtmlAttribute(p.username||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Password</div></div><input type="password" class="form-input w-200" id="setting-proxy-pass" value="${escapeHtmlAttribute(p.password||'')}"></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'flaresolverr':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">FlareSolverr Settings</span><span class="settings-section-desc">Cloudflare bypass configuration</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-flare-enabled" ${f.enabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Host</div></div><input type="text" class="form-input w-200" id="setting-flare-host" value="${escapeHtmlAttribute(f.host||'localhost')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Port</div></div><input type="number" class="form-input w-100" id="setting-flare-port" value="${escapeHtmlAttribute(f.port||0)}"></div>
                                <div class="setting-row"><div><div class="setting-label">Use SSL</div></div><label class="toggle-switch"><input type="checkbox" id="setting-flare-ssl" ${f.useSsl?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">MITM Enabled</div></div><label class="toggle-switch"><input type="checkbox" id="setting-flare-mitm" ${f.mitmEnabled?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">MITM Host</div></div><input type="text" class="form-input w-200" id="setting-flare-mitm-host" value="${escapeHtmlAttribute(f.mitmHost||'localhost')}"></div>
                                <div class="setting-row"><div><div class="setting-label">MITM Port</div></div><input type="number" class="form-input w-100" id="setting-flare-mitm-port" value="${escapeHtmlAttribute(f.mitmPort||8080)}"></div>
                                <div class="setting-row"><div><div class="setting-label">MITM Use SSL</div></div><label class="toggle-switch"><input type="checkbox" id="setting-flare-mitm-ssl" ${f.mitmUseSsl?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'calendar':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Calendar Settings</span><span class="settings-section-desc">Calendar display options</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Calendar Language</div></div><select class="form-select mw-150" id="setting-cal-lang">${CALENDAR_LANG_OPTIONS.map(o=>`<option value="${o.value}" ${(cal.language||'').toLowerCase()===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Dub Filter</div><div class="setting-desc">Only show episodes with this audio language</div></div><select class="form-select mw-170" id="setting-cal-dub-filter"><option value="none" ${(!cal.dubFilter||cal.dubFilter==='none'||cal.dubFilter==='dubbed'||cal.dubFilter==='subbed')?'selected':''}>All Languages</option>${LANG_OPTIONS.map(o=>`<option value="${o.value}" ${cal.dubFilter===o.value?'selected':''}>${o.label}</option>`).join('')}</select></div>
                                <div class="setting-row"><div><div class="setting-label">Hide Dubs</div></div><label class="toggle-switch"><input type="checkbox" id="setting-cal-hide-dubs" ${cal.hideDubs?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Show History Marks</div><div class="setting-desc">Mark calendar episodes that are in your history</div></div><label class="toggle-switch"><input type="checkbox" id="setting-cal-history-mark" ${cal.showHistoryMark!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Add Download Settings</span><span class="settings-section-desc">Add Download tab behavior</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Add Search Results to History</div></div><label class="toggle-switch"><input type="checkbox" id="setting-ad-search-history" ${ad.searchAddToHistory!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Single Episode Instant Add</div><div class="setting-desc">Add single-episode URLs to the queue immediately</div></div><label class="toggle-switch"><input type="checkbox" id="setting-ad-instant-add" ${ad.singleEpisodeInstantAdd!==false?'checked':''}><span class="toggle-slider"></span></label></div>
                                <div class="setting-row"><div><div class="setting-label">Default to Search</div><div class="setting-desc">Search by title instead of expecting a URL</div></div><label class="toggle-switch"><input type="checkbox" id="setting-ad-default-search" ${ad.defaultSearchEnabled?'checked':''}><span class="toggle-slider"></span></label></div>
                            </div>
                        </div>
                    `;
                    break;
                case 'appearance':
                    content.innerHTML = `
                        <div class="settings-section">
                            <div class="settings-section-header"><span class="settings-section-title">Appearance Settings</span><span class="settings-section-desc">Customize the UI</span></div>
                            <div class="settings-section-body">
                                <div class="setting-row"><div><div class="setting-label">Theme</div></div><select class="form-select mw-150" id="setting-theme" onchange="if(config){config.appearance=config.appearance||{};config.appearance.theme=this.value;applyTheme();}"><option value="System" ${a.theme==='System'?'selected':''}>System</option><option value="Dark" ${a.theme==='Dark'?'selected':''}>Dark</option><option value="Light" ${a.theme==='Light'?'selected':''}>Light</option><option value="Cinematic" ${a.theme==='Cinematic'?'selected':''}>Cinematic</option><option value="AMOLED" ${a.theme==='AMOLED'?'selected':''}>AMOLED</option><option value="Seerr" ${a.theme==='Seerr'||a.theme==='Nebula'?'selected':''}>Seerr</option><option value="Sonarr" ${a.theme==='Sonarr'?'selected':''}>Sonarr</option></select></div>
                                <div class="setting-row"><div><div class="setting-label">Accent Color</div></div><input type="color" class="form-input w-80" id="setting-accent" value="${escapeHtmlAttribute(a.accentColor||'#F47521')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Background Image Path</div></div><input type="text" class="form-input w-300" id="setting-bg-path" value="${escapeHtmlAttribute(a.backgroundImagePath||'')}"></div>
                                <div class="setting-row"><div><div class="setting-label">Background Opacity</div></div><input type="number" class="form-input w-100" id="setting-bg-opacity" value="${escapeHtmlAttribute(a.backgroundImageOpacity ?? 0.5)}" min="0" max="1" step="0.1"></div>
                                <div class="setting-row"><div><div class="setting-label">Background Blur Radius</div></div><input type="number" class="form-input w-100" id="setting-bg-blur" value="${escapeHtmlAttribute(a.backgroundImageBlurRadius ?? 10)}" min="0"></div>
                            </div>
                        </div>
                    `;
                    break;
            }
        }

        // ================== API / ACTIONS ==================
        let fetchConfigRetryCount = 0;
        const MAX_FETCH_CONFIG_RETRIES = 5;
        
        async function fetchConfig() {
            try {
                const controller = new AbortController();
                const timeoutId = setTimeout(() => controller.abort(), 10000);
                const res = await fetch('/api/v1/config', { signal: controller.signal });
                clearTimeout(timeoutId);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                config = await res.json();
                applyTheme();
                updateConnectionStatus(true);
                fetchConfigRetryCount = 0;
            } catch (e) {
                console.error('Failed to load config:', e);
                updateConnectionStatus(false);
                fetchConfigRetryCount++;
                if (fetchConfigRetryCount <= MAX_FETCH_CONFIG_RETRIES) {
                    showToast('Failed to load settings. Retrying...', 'error');
                }
                if (fetchConfigRetryCount < MAX_FETCH_CONFIG_RETRIES) {
                    setTimeout(fetchConfig, FETCH_CONFIG_RETRY_DELAY_MS);
                }
            }
        }

        // Named themes -> data-theme attribute. "Dark" is the default :root (no attr).
        const THEME_ATTR = { 'Light': 'light', 'AMOLED': 'amoled', 'Cinematic': 'cinematic', 'Nebula': 'nebula', 'Seerr': 'nebula', 'Sonarr': 'sonarr' };
        function applyTheme() {
            let theme = config?.appearance?.theme || 'System';
            if (theme === 'System') {
                theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'Dark' : 'Light';
            }
            const attr = THEME_ATTR[theme];
            if (attr) {
                document.documentElement.setAttribute('data-theme', attr);
            } else {
                document.documentElement.removeAttribute('data-theme'); // Dark = default
            }

            // Apply a genuinely custom accent color. The picker defaults to #F47521
            // (Crunchyroll orange); treat that (and empty) as "no custom accent" so it does
            // NOT override a theme that defines its own accent (e.g. Seerr/Nebula's indigo).
            // Always clear first so switching back to a theme restores its accent.
            const accentColor = config?.appearance?.accentColor;
            document.documentElement.style.removeProperty('--accent');
            if (accentColor && accentColor.toUpperCase() !== '#F47521') {
                document.documentElement.style.setProperty('--accent', accentColor);
            }
        }
        
        // Listen for system theme changes
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            if (config?.appearance?.theme === 'System') applyTheme();
        });

        // Helper: safely read a form field value only if the element exists
        function getFieldValue(id, parser) {
            const el = document.getElementById(id);
            if (!el) return undefined;
            if (parser === 'bool') return el.checked;
            if (parser === 'int') {
                const n = parseInt(el.value);
                return isNaN(n) ? undefined : n;
            }
            if (parser === 'float') {
                const n = parseFloat(el.value);
                return isNaN(n) ? undefined : n;
            }
            return el.value;
        }

        async function saveSettings() {
            let newConfig = {};
            
            switch(settingsTab) {
                case 'general': {
                    const g = {};
                    const logMode = getFieldValue('setting-log-mode', 'bool');
                    if (logMode !== undefined) g.logMode = logMode;
                    const removeFinished = getFieldValue('setting-remove-finished', 'bool');
                    if (removeFinished !== undefined) g.removeFinishedDownload = removeFinished;
                    const tokenPath = getFieldValue('setting-token-path');
                    if (tokenPath !== undefined) g.tokenFilePath = tokenPath;
                    if (Object.keys(g).length) newConfig.general = g;
                    break;
                }
                    
                case 'crunchyroll': {
                    const cr = {};
                    const useBeta = getFieldValue('setting-use-beta-api', 'bool');
                    if (useBeta !== undefined) cr.useBetaApi = useBeta;
                    const markWatched = getFieldValue('setting-mark-watched', 'bool');
                    if (markWatched !== undefined) cr.markAsWatched = markWatched;
                    
                    const dl = {};
                    const dubLangs = getMultiSelect('setting-dub-langs');
                    dl.dubLanguages = dubLangs;
                    const dlAd = getFieldValue('setting-dl-ad', 'bool');
                    if (dlAd !== undefined) dl.downloadDescriptionAudio = dlAd;
                    const dlMulti = getFieldValue('setting-download-multi-dub', 'bool');
                    if (dlMulti !== undefined) dl.downloadMultipleDubs = dlMulti;
                    const firstDub = getFieldValue('setting-first-dub', 'bool');
                    if (firstDub !== undefined) dl.downloadFirstAvailableDub = firstDub;
                    const hardSub = getFieldValue('setting-hard-sub');
                    if (hardSub !== undefined && hardSub !== '') dl.hardSubLang = hardSub;
                    const hardFallback = getFieldValue('setting-hard-fallback', 'bool');
                    if (hardFallback !== undefined) dl.hardSubRawFallback = hardFallback;
                    const softSubs = getMultiSelect('setting-soft-subs');
                    dl.softSubs = softSubs;
                    const scaledBorder = getFieldValue('setting-scaled-border');
                    if (scaledBorder !== undefined) dl.subsAddScaledBorder = scaledBorder;
                    const signsForced = getFieldValue('setting-signs-forced', 'bool');
                    if (signsForced !== undefined) dl.signsSubsAsForced = signsForced;
                    const ccHi = getFieldValue('setting-cc-hi', 'bool');
                    if (ccHi !== undefined) dl.ccSubsMuxingFlag = ccHi;
                    const ccConvert = getFieldValue('setting-cc-convert', 'bool');
                    if (ccConvert !== undefined) dl.convertVttToAss = ccConvert;
                    const ccFont = getFieldValue('setting-cc-font');
                    if (ccFont !== undefined) dl.ccSubsFont = ccFont;
                    const includeSigns = getFieldValue('setting-include-signs', 'bool');
                    if (includeSigns !== undefined) dl.includeSignsSubs = includeSigns;
                    const includeCc = getFieldValue('setting-include-cc', 'bool');
                    if (includeCc !== undefined) dl.includeCcSubs = includeCc;
                    const subsDup = getFieldValue('setting-subs-dup', 'bool');
                    if (subsDup !== undefined) dl.subsDownloadDuplicate = subsDup;
                    const fixCcc = getFieldValue('setting-fix-ccc', 'bool');
                    if (fixCcc !== undefined) dl.fixCccSubtitles = fixCcc;
                    const skipSubs = getFieldValue('setting-skip-subs', 'bool');
                    if (skipSubs !== undefined) dl.skipSubs = skipSubs;
                    const ccTag = getFieldValue('setting-cc-tag');
                    if (ccTag !== undefined && ccTag !== '') dl.ccTag = ccTag;

                    if (Object.keys(cr).length) newConfig.crunchyroll = cr;
                    if (Object.keys(dl).length) newConfig.download = dl;
                    break;
                }
                    
                case 'download': {
                    const dl = {};
                    const outDir = getFieldValue('setting-output-dir');
                    if (outDir !== undefined) dl.outputDirectory = outDir;
                    const tempDir = getFieldValue('setting-temp-dir');
                    if (tempDir !== undefined) dl.tempDirectory = tempDir;
                    const useTemp = getFieldValue('setting-use-temp', 'bool');
                    if (useTemp !== undefined) dl.useTempFolder = useTemp;
                    const partSize = getFieldValue('setting-part-size', 'int');
                    if (partSize !== undefined) dl.partSize = partSize;
                    const downloadDelay = getFieldValue('setting-download-delay', 'int');
                    if (downloadDelay !== undefined) dl.downloadDelaySeconds = downloadDelay;
                    const delayDubBased = getFieldValue('setting-download-delay-dub-based', 'bool');
                    if (delayDubBased !== undefined) dl.downloadDelayUseDubBased = delayDubBased;
                    const cooldown = getFieldValue('setting-cooldown', 'int');
                    if (cooldown !== undefined) dl.cooldownDelaySeconds = cooldown;
                    const concurrent = getFieldValue('setting-concurrent', 'int');
                    if (concurrent !== undefined) dl.simultaneousDownloads = concurrent;
                    const procJobs = getFieldValue('setting-proc-jobs', 'int');
                    if (procJobs !== undefined) dl.simultaneousProcessingJobs = procJobs;
                    const speedLimit = getFieldValue('setting-speed-limit', 'int');
                    if (speedLimit !== undefined) dl.downloadSpeedLimit = speedLimit;
                    const speedBits = getFieldValue('setting-speed-bits', 'bool');
                    if (speedBits !== undefined) dl.downloadSpeedInBits = speedBits;
                    const retry = getFieldValue('setting-retry', 'int');
                    if (retry !== undefined) dl.retryAttempts = retry;
                    const retryDelay = getFieldValue('setting-retry-delay', 'int');
                    if (retryDelay !== undefined) dl.retryDelay = retryDelay;
                    const rateLimitDelay = getFieldValue('setting-rate-limit-delay', 'int');
                    if (rateLimitDelay !== undefined) dl.playbackRateLimitRetryDelaySeconds = rateLimitDelay;
                    const retryMax = getFieldValue('setting-retry-max-delay', 'int');
                    if (retryMax !== undefined) dl.retryMaxDelaySeconds = retryMax;
                    const newMethod = getFieldValue('setting-new-method', 'bool');
                    if (newMethod !== undefined) dl.downloadMethodeNew = newMethod;
                    const replaceExisting = getFieldValue('setting-replace-existing', 'bool');
                    if (replaceExisting !== undefined) dl.replaceExistingFiles = replaceExisting;
                    // (Allow Early Start / Skip Missing Languages live on the Queue tab as
                    // setting-queue-early-start / setting-queue-skip-missing.)
                    const defaultVideo = getFieldValue('setting-default-video');
                    if (defaultVideo !== undefined) dl.defaultVideo = defaultVideo;
                    
                    if (Object.keys(dl).length) newConfig.download = dl;
                    
                    // Stream endpoints (rendered in Download tab, stored in crunchyroll section)
                    const crStream = {};
                    const ep1 = getFieldValue('setting-stream-endpoint');
                    if (ep1 !== undefined) crStream.endpoint = ep1;
                    const useDef1 = getFieldValue('setting-stream-use-default', 'bool');
                    if (useDef1 !== undefined) crStream.useDefault = useDef1;
                    // When "Use Default" is on, the disabled inputs just echo server defaults -
                    // persisting them (especially the sanitized empty authorization) would wipe
                    // any custom values stored server-side
                    if (useDef1 === false) {
                        const auth1 = getFieldValue('setting-stream-auth');
                        if (auth1) crStream.authorization = auth1;
                        const ua1 = getFieldValue('setting-stream-ua');
                        if (ua1 !== undefined) crStream.userAgent = ua1;
                        const dt1 = getFieldValue('setting-stream-device-type');
                        if (dt1 !== undefined) crStream.deviceType = dt1;
                        const dn1 = getFieldValue('setting-stream-device-name');
                        if (dn1 !== undefined) crStream.deviceName = dn1;
                    }
                    const vid1 = getFieldValue('setting-stream-video', 'bool');
                    if (vid1 !== undefined) crStream.video = vid1;
                    const aud1 = getFieldValue('setting-stream-audio', 'bool');
                    if (aud1 !== undefined) crStream.audio = aud1;
                    
                    const crStreamSecondary = {};
                    const ep2 = getFieldValue('setting-stream-endpoint-2');
                    if (ep2 !== undefined) crStreamSecondary.endpoint = ep2;
                    const useDef2 = getFieldValue('setting-stream-use-default-2', 'bool');
                    if (useDef2 !== undefined) crStreamSecondary.useDefault = useDef2;
                    if (useDef2 === false) {
                        const auth2 = getFieldValue('setting-stream-auth-2');
                        if (auth2) crStreamSecondary.authorization = auth2;
                        const ua2 = getFieldValue('setting-stream-ua-2');
                        if (ua2 !== undefined) crStreamSecondary.userAgent = ua2;
                        const dt2 = getFieldValue('setting-stream-device-type-2');
                        if (dt2 !== undefined) crStreamSecondary.deviceType = dt2;
                        const dn2 = getFieldValue('setting-stream-device-name-2');
                        if (dn2 !== undefined) crStreamSecondary.deviceName = dn2;
                    }
                    const vid2 = getFieldValue('setting-stream-video-2', 'bool');
                    if (vid2 !== undefined) crStreamSecondary.video = vid2;
                    const aud2 = getFieldValue('setting-stream-audio-2', 'bool');
                    if (aud2 !== undefined) crStreamSecondary.audio = aud2;
                    
                    const crUpdate = {};
                    if (Object.keys(crStream).length) crUpdate.streamEndpoint = crStream;
                    if (Object.keys(crStreamSecondary).length) crUpdate.streamEndpointSecondary = crStreamSecondary;
                    if (Object.keys(crUpdate).length) newConfig.crunchyroll = crUpdate;
                    break;
                }
                    
                case 'filename': {
                    const dl = {};
                    const filename = getFieldValue('setting-filename');
                    if (filename !== undefined) dl.filename = filename;
                    const filenameTemplate = getFieldValue('setting-filename-template');
                    if (filenameTemplate !== undefined) dl.filenameTemplate = filenameTemplate;
                    const whitespaceSub = getFieldValue('setting-whitespace-sub');
                    if (whitespaceSub !== undefined) dl.filenameWhitespaceSubstitute = whitespaceSub;
                    const leadingNums = getFieldValue('setting-leading-zeros', 'int');
                    if (leadingNums !== undefined) dl.leadingNumbers = leadingNums;
                    if (Object.keys(dl).length) newConfig.download = dl;
                    break;
                }
                    
                case 'muxing': {
                    const dl = {};
                    const qualityVideo = getFieldValue('setting-quality-video');
                    if (qualityVideo !== undefined) dl.qualityVideo = qualityVideo;
                    const qualityAudio = getFieldValue('setting-quality-audio');
                    if (qualityAudio !== undefined) dl.qualityAudio = qualityAudio;
                    const defaultVideo = getFieldValue('setting-default-video');
                    if (defaultVideo !== undefined) dl.defaultVideo = defaultVideo;
                    const defaultAudio = getFieldValue('setting-default-audio');
                    if (defaultAudio !== undefined) dl.defaultAudio = defaultAudio;
                    const dlVideoEvery = getFieldValue('setting-dl-video-every', 'bool');
                    if (dlVideoEvery !== undefined) dl.dlVideoOnce = !dlVideoEvery;
                    const keepSeparate = getFieldValue('setting-keep-separate', 'bool');
                    if (keepSeparate !== undefined) dl.keepDubsSeparate = keepSeparate;
                    const defaultSub = getFieldValue('setting-default-sub');
                    if (defaultSub !== undefined) dl.defaultSub = defaultSub;
                    const skipMux = getFieldValue('setting-skip-mux', 'bool');
                    if (skipMux !== undefined) dl.skipMuxing = skipMux;
                    const muxMp4 = getFieldValue('setting-mux-mp4', 'bool');
                    if (muxMp4 !== undefined) dl.muxMp4 = muxMp4;
                    const muxMp3 = getFieldValue('setting-mux-mp3', 'bool');
                    if (muxMp3 !== undefined) dl.muxAudioOnlyToMp3 = muxMp3;
                    const skipSubMux = getFieldValue('setting-skip-sub-mux', 'bool');
                    if (skipSubMux !== undefined) dl.skipSubMux = skipSubMux;
                    const muxFonts = getFieldValue('setting-mux-fonts', 'bool');
                    if (muxFonts !== undefined) dl.muxFonts = muxFonts;
                    const muxTsFonts = getFieldValue('setting-mux-ts-fonts', 'bool');
                    if (muxTsFonts !== undefined) dl.muxTypesettingFonts = muxTsFonts;
                    const muxCover = getFieldValue('setting-mux-cover', 'bool');
                    if (muxCover !== undefined) dl.muxCover = muxCover;
                    const videoTitle = getFieldValue('setting-video-title');
                    if (videoTitle !== undefined) dl.videoTitle = videoTitle;
                    const includeDesc = getFieldValue('setting-include-desc', 'bool');
                    if (includeDesc !== undefined) dl.includeVideoDescription = includeDesc;
                    const descLang = getFieldValue('setting-desc-lang');
                    if (descLang !== undefined) dl.descriptionLang = descLang;
                    const muxDefaultSubSigns = getFieldValue('setting-default-sub-signs', 'bool');
                    if (muxDefaultSubSigns !== undefined) dl.muxDefaultSubSigns = muxDefaultSubSigns;
                    const muxDefaultSubForced = getFieldValue('setting-default-sub-forced', 'bool');
                    if (muxDefaultSubForced !== undefined) dl.muxDefaultSubForcedDisplay = muxDefaultSubForced;
                    const syncTiming = getFieldValue('setting-sync-timing', 'bool');
                    if (syncTiming !== undefined) dl.syncTiming = syncTiming;
                    const syncFallback = getFieldValue('setting-sync-fallback', 'bool');
                    if (syncFallback !== undefined) dl.syncTimingFullQualityFallback = syncFallback;
                    const syncHwAccel = getFieldValue('setting-sync-hwaccel');
                    if (syncHwAccel !== undefined) dl.syncHwAccel = syncHwAccel;
                    // Always send the lists when the inputs are present so removing all entries works
                    if (document.getElementById('setting-mkvmerge')) dl.mkvmergeOptions = getListInput('setting-mkvmerge');
                    if (document.getElementById('setting-ffmpeg')) dl.ffmpegOptions = getListInput('setting-ffmpeg');
                    const encode = getFieldValue('setting-encode', 'bool');
                    if (encode !== undefined) dl.encodeEnabled = encode;
                    const encodePreset = getFieldValue('setting-encode-preset');
                    if (encodePreset !== undefined) dl.encodingPreset = encodePreset;
                    const noVideo = getFieldValue('setting-dl-video', 'bool');
                    if (noVideo !== undefined) dl.noVideo = !noVideo;
                    const noAudio = getFieldValue('setting-dl-audio', 'bool');
                    if (noAudio !== undefined) dl.noAudio = !noAudio;
                    const chapters = getFieldValue('setting-chapters', 'bool');
                    if (chapters !== undefined) dl.includeChapters = chapters;
                    if (Object.keys(dl).length) newConfig.download = dl;
                    break;
                }
                    
                case 'queue': {
                    const q = {};
                    const persist = getFieldValue('setting-persist-queue', 'bool');
                    if (persist !== undefined) q.persistQueue = persist;
                    const autoDl = getFieldValue('setting-auto-download', 'bool');
                    if (autoDl !== undefined) q.autoDownload = autoDl;
                    const procJobs = getFieldValue('setting-queue-proc-jobs', 'int');
                    if (procJobs !== undefined) q.simultaneousProcessingJobs = procJobs;
                    const queuePath = getFieldValue('setting-queue-path');
                    if (queuePath !== undefined) q.queueFilePath = queuePath;
                    if (Object.keys(q).length) newConfig.queue = q;
                    // These two belong to the download section of the backend config,
                    // even though they're shown on the Queue tab
                    const dlq = {};
                    const earlyStart = getFieldValue('setting-queue-early-start', 'bool');
                    if (earlyStart !== undefined) dlq.downloadAllowEarlyStart = earlyStart;
                    const skipMissing = getFieldValue('setting-queue-skip-missing', 'bool');
                    if (skipMissing !== undefined) dlq.downloadOnlyWithAllSelectedDubSub = skipMissing;
                    if (Object.keys(dlq).length) newConfig.download = dlq;
                    break;
                }
                    
                case 'calendar':
                    newConfig.calendar = {
                        language: document.getElementById('setting-cal-lang')?.value || 'en-us',
                        dubFilter: document.getElementById('setting-cal-dub-filter')?.value || 'none',
                        hideDubs: document.getElementById('setting-cal-hide-dubs')?.checked || false,
                        showHistoryMark: document.getElementById('setting-cal-history-mark')?.checked ?? true
                    };
                    newConfig.addDownload = {
                        searchAddToHistory: document.getElementById('setting-ad-search-history')?.checked ?? true,
                        singleEpisodeInstantAdd: document.getElementById('setting-ad-instant-add')?.checked ?? true,
                        defaultSearchEnabled: document.getElementById('setting-ad-default-search')?.checked || false
                    };
                    break;
                    
                case 'history': {
                    // Mode 0 ("Default All") is a valid value - don't let || swallow it
                    const histMode = parseInt(document.getElementById('setting-history-mode')?.value);
                    newConfig.history = {
                        enabled: document.getElementById('setting-history-enabled')?.checked ?? true,
                        includeCrArtists: document.getElementById('setting-cr-artists')?.checked || false,
                        removeMissingEpisodes: document.getElementById('setting-remove-missing')?.checked ?? true,
                        checkPartialDownloads: document.getElementById('setting-history-partial')?.checked ?? true,
                        lang: document.getElementById('setting-history-lang')?.value || 'en-US',
                        autoRefreshIntervalMinutes: parseInt(document.getElementById('setting-history-interval')?.value) || 0,
                        autoRefreshMode: isNaN(histMode) ? 0 : histMode
                    };
                    break;
                }
                    
                case 'sonarr': {
                    newConfig.sonarr = {
                        enabled: document.getElementById('setting-sonarr-enabled')?.checked || false,
                        host: document.getElementById('setting-sonarr-host')?.value || null,
                        port: parseInt(document.getElementById('setting-sonarr-port')?.value) || 0,
                        useSsl: document.getElementById('setting-sonarr-ssl')?.checked || false,
                        urlBase: document.getElementById('setting-sonarr-urlbase')?.value || null,
                        useSonarrNumbering: document.getElementById('setting-sonarr-numbering')?.checked || false
                    };
                    // GET /config returns the placeholder "[configured]" instead of the real key -
                    // only send the key if the user actually typed a new one
                    const sonarrApiKey = document.getElementById('setting-sonarr-apikey')?.value;
                    if (sonarrApiKey && sonarrApiKey !== '[configured]') newConfig.sonarr.apiKey = sonarrApiKey;
                    break;
                }
                    
                case 'notifications':
                    const headersText = document.getElementById('setting-webhook-headers')?.value || '';
                    const webhookHeaders = {};
                    headersText.split('\n').forEach(line => {
                        const colonIdx = line.indexOf(':');
                        if (colonIdx > 0) {
                            webhookHeaders[line.substring(0, colonIdx).trim()] = line.substring(colonIdx + 1).trim();
                        }
                    });
                    newConfig.notifications = {
                        webhookUrl: document.getElementById('setting-webhook-url')?.value || null,
                        webhookEnabled: document.getElementById('setting-webhook-enabled')?.checked || false,
                        webhookMethod: document.getElementById('setting-webhook-method')?.value || 'POST',
                        webhookContentType: document.getElementById('setting-webhook-ct')?.value || 'application/json',
                        webhookHeaders: webhookHeaders,
                        webhookBodyTemplate: document.getElementById('setting-webhook-body')?.value || '',
                        notifyQueueFinished: document.getElementById('setting-notify-queue-finished')?.checked || false,
                        notifyDownloadFinished: document.getElementById('setting-notify-download-finished')?.checked || false,
                        notifyDownloadFailed: document.getElementById('setting-notify-download-failed')?.checked || false,
                        notifyUpdateAvailable: document.getElementById('setting-notify-update-available')?.checked || false
                    };
                    break;
                    
                case 'proxy': {
                    newConfig.proxy = {
                        enabled: document.getElementById('setting-proxy-enabled')?.checked || false,
                        allTraffic: document.getElementById('setting-proxy-all-traffic')?.checked ?? true,
                        socks: document.getElementById('setting-proxy-socks')?.checked || false,
                        host: document.getElementById('setting-proxy-host')?.value || null,
                        port: parseInt(document.getElementById('setting-proxy-port')?.value) || 0,
                        username: document.getElementById('setting-proxy-user')?.value || null
                    };
                    // GET /config returns the placeholder "[configured]" instead of the real password -
                    // only send it if the user actually typed a new one
                    const proxyPassword = document.getElementById('setting-proxy-pass')?.value;
                    if (proxyPassword && proxyPassword !== '[configured]') newConfig.proxy.password = proxyPassword;
                    break;
                }
                    
                case 'flaresolverr':
                    newConfig.flareSolverr = {
                        enabled: document.getElementById('setting-flare-enabled')?.checked || false,
                        host: document.getElementById('setting-flare-host')?.value || 'localhost',
                        port: parseInt(document.getElementById('setting-flare-port')?.value) || 0,
                        useSsl: document.getElementById('setting-flare-ssl')?.checked || false,
                        mitmEnabled: document.getElementById('setting-flare-mitm')?.checked || false,
                        mitmHost: document.getElementById('setting-flare-mitm-host')?.value || 'localhost',
                        mitmPort: parseInt(document.getElementById('setting-flare-mitm-port')?.value) || 8080,
                        mitmUseSsl: document.getElementById('setting-flare-mitm-ssl')?.checked || false
                    };
                    break;
                    
                case 'appearance': {
                    // 0 is a valid opacity/blur value - don't let || swallow it
                    const bgOpacity = parseFloat(document.getElementById('setting-bg-opacity')?.value);
                    const bgBlur = parseFloat(document.getElementById('setting-bg-blur')?.value);
                    newConfig.appearance = {
                        theme: document.getElementById('setting-theme')?.value || 'System',
                        accentColor: document.getElementById('setting-accent')?.value || null,
                        backgroundImagePath: document.getElementById('setting-bg-path')?.value || null,
                        backgroundImageOpacity: isNaN(bgOpacity) ? 0.5 : bgOpacity,
                        backgroundImageBlurRadius: isNaN(bgBlur) ? 10 : bgBlur
                    };
                    break;
                }
                    
                default:
                    showToast('Unknown settings tab', 'error');
                    return;
            }
            
            try {
                const res = await fetch('/api/v1/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(newConfig)
                });
                if (res.ok) {
                    showToast('Settings saved', 'success');
                    // Deep merge: merge each section's properties individually
                    // to avoid overwriting properties from other tabs
                    Object.keys(newConfig).forEach(section => {
                        config[section] = config[section] || {};
                        Object.assign(config[section], newConfig[section]);
                    });
                    applyTheme();
                }
                else showToast('Failed to save settings', 'error');
            } catch (e) { showToast('Error saving settings', 'error'); }
        }

        // Reset just the current settings tab to its defaults. We load the factory
        // defaults, drop them into the in-memory config, re-render this tab, then call
        // saveSettings() - which only collects+POSTs THIS tab's fields, so the server
        // merge leaves every other tab untouched. Finally reload the real config.
        async function resetCurrentTab() {
            const tabName = settingsTab;
            if (!confirm('Reset the "' + tabName + '" tab to default settings?')) return;
            try {
                const res = await fetch('/api/v1/config/defaults');
                if (!res.ok) throw new Error('HTTP ' + res.status);
                config = await res.json();
                renderSettingsTab();
                await saveSettings();
                await fetchConfig();
                renderSettingsTab();
                showToast('"' + tabName + '" tab reset to default', 'success');
            } catch (e) {
                console.error('Reset tab failed:', e);
                showToast('Failed to reset tab', 'error');
            }
        }

        // Reset every setting to default. The backend preserves Crunchyroll login,
        // saved stream endpoints, and the token path so the user stays logged in.
        async function resetAllSettings() {
            if (!confirm('Reset ALL settings to default?\n\nYour Crunchyroll login is kept, but every other setting reverts. This cannot be undone.')) return;
            try {
                const res = await fetch('/api/v1/config/reset', { method: 'POST' });
                if (!res.ok) throw new Error('HTTP ' + res.status);
                await fetchConfig();
                renderSettingsTab();
                showToast('All settings reset to default', 'success');
            } catch (e) {
                console.error('Reset all failed:', e);
                showToast('Failed to reset settings', 'error');
            }
        }

        async function testWebhook() {
            const url = document.getElementById('setting-webhook-url')?.value;
            if (!url) {
                showToast('Webhook URL is required', 'error');
                return;
            }
            try {
                const res = await fetch('/api/v1/config/webhook/test', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ url: url })
                });
                if (res.ok) showToast('Webhook test sent', 'success');
                else showToast('Webhook test failed', 'error');
            } catch (e) {
                showToast('Webhook test error', 'error');
            }
        }

        async function toggleSetting(key, value) {
            const settingMap = {
                'removeFinished': { section: 'general', field: 'removeFinishedDownload', label: 'Remove Finished' },
                'autoDownload': { section: 'queue', field: 'autoDownload', label: 'Auto Download' }
            };
            
            const setting = settingMap[key];
            if (!setting) {
                console.warn('Unknown toggle key:', key);
                return;
            }
            
            let updateData = {};
            updateData[setting.section] = { [setting.field]: value };
            
            try {
                const res = await fetch('/api/v1/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(updateData)
                });
                if (res.ok) {
                    showToast(`${setting.label} ${value ? 'enabled' : 'disabled'}`, 'success');
                    // Update local config cache
                    config[setting.section] = config[setting.section] || {};
                    config[setting.section][setting.field] = value;
                } else {
                    showToast('Failed to save setting', 'error');
                    // Revert checkbox on failure
                    const checkboxId = key === 'removeFinished' ? 'toggle-remove-finished' : 'toggle-auto-download';
                    const checkbox = document.getElementById(checkboxId);
                    if (checkbox) checkbox.checked = !value;
                }
            } catch (e) {
                showToast('Error saving setting', 'error');
                // Revert checkbox on failure
                const checkboxId = key === 'removeFinished' ? 'toggle-remove-finished' : 'toggle-auto-download';
                const checkbox = document.getElementById(checkboxId);
                if (checkbox) checkbox.checked = !value;
            }
        }

        async function toggleGlobalPause() {
            try {
                const action = isQueueGloballyPaused ? 'resume' : 'pause';
                const res = await fetch(`/api/v1/queue/${action}`, { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                isQueueGloballyPaused = !isQueueGloballyPaused;
                showToast(isQueueGloballyPaused ? 'Queue paused globally' : 'Queue resumed', 'success');
                fetchDownloads();
                fetchQueueStats();
            } catch (e) { showToast('Failed to toggle pause', 'error'); }
        }

        async function retryFailed() {
            try {
                const res = await fetch('/api/v1/queue/retry-failed', { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Retrying failed downloads', 'success');
                fetchDownloads();
            } catch (e) { showToast('Retry failed', 'error'); }
        }

        async function pauseRunning() {
            try {
                // Find all active/downloading items and pause them individually
                const activeItems = queueData.filter(i => 
                    (i.downloadProgress?.state || '').toLowerCase() === 'downloading'
                );
                if (activeItems.length === 0) {
                    showToast('No active downloads to pause', 'warning');
                    return;
                }
                for (const item of activeItems) {
                    await fetch(`/api/v1/queue/${item.id}/pause`, { method: 'POST' });
                }
                showToast(`Paused ${activeItems.length} download(s)`, 'success');
                fetchDownloads();
            } catch (e) { showToast('Pause failed', 'error'); }
        }

        async function clearQueue() {
            if (!confirm('Clear all items from queue?')) return;
            try {
                const res = await fetch('/api/v1/queue', { method: 'DELETE' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Queue cleared', 'success');
                fetchDownloads();
            } catch (e) { showToast('Clear failed', 'error'); }
        }

        async function retryDownload(id) {
            try {
                const res = await fetch(`/api/v1/queue/${id}/retry`, { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Retrying download', 'success');
                fetchDownloads();
            } catch (e) { showToast('Retry failed', 'error'); }
        }

        async function startDownload(id) {
            try {
                const res = await fetch(`/api/v1/queue/${id}/start`, { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Starting download', 'success');
                fetchDownloads();
            } catch (e) { showToast('Start failed', 'error'); }
        }

        async function togglePauseResume(id, isDownloading) {
            try {
                const endpoint = isDownloading ? 'pause' : 'resume';
                const res = await fetch(`/api/v1/queue/${id}/${endpoint}`, { method: 'POST' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                fetchDownloads();
            } catch (e) { showToast('Action failed', 'error'); }
        }

        async function removeFromQueue(id) {
            if (!confirm('Remove this item from queue?')) return;
            try {
                const res = await fetch(`/api/v1/queue/${id}`, { method: 'DELETE' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Removed from queue', 'success');
                if (currentPage === 'downloads') fetchDownloads();
            } catch (e) { showToast('Remove failed', 'error'); }
        }

        function refreshHistory() {             fetchHistoryData();
            // Restart history auto-refresh interval if it was cleared
            if (!historyIntervalId) {
                historyIntervalId = setInterval(() => {
                    if (currentPage === 'history') fetchHistoryData();
                }, HISTORY_POLL_INTERVAL_MS);
            }
        }
        async function addMissingToQueue() {
            if (!confirm('This will add all missing episodes across all series to the queue. Continue?')) return;
            try {
                // Fetch rich history to find series with missing episodes
                const res = await fetch('/api/v1/history/rich');
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const data = await res.json();
                let added = 0;
                
                // Iterate through history and add missing episodes
                for (const series of (data || [])) {
                    for (const season of (series.seasons || [])) {
                        for (const episode of (season.episodes || [])) {
                            if (episode.wasDownloaded === false && episode.episodeId) {
                                const queueRes = await fetch('/api/v1/queue', {
                                    method: 'POST',
                                    headers: { 'Content-Type': 'application/json' },
                                    body: JSON.stringify({
                                        episodeId: episode.episodeId,
                                        title: episode.episodeTitle || 'Unknown',
                                        seriesTitle: series.seriesTitle || 'Unknown',
                                        seasonNumber: season.seasonNum || 1,
                                        episodeNumber: episode.episode || 1
                                    })
                                });
                                if (queueRes.ok) added++;
                            }
                        }
                    }
                }
                
                if (added > 0) {
                    showToast(`Added ${added} missing episode(s) to queue`, 'success');
                } else {
                    showToast('No missing episodes found', 'info');
                }
            } catch (e) {
                showToast('Failed to add missing episodes', 'error');
            }
        }
        
        function filterHistory(query) {
            historyFilterText = query.toLowerCase();
            renderHistoryContent();
        }
        
        async function refreshSeries(id) {
            try {
                const res = await fetch(`/api/v1/history/update-series/${id}`, { method: 'POST' });
                if (res.ok) {
                    showToast('Series refreshed', 'success');
                    if (currentPage === 'history') fetchHistoryData();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Failed to refresh series', 'error');
                }
            } catch (e) {
                showToast('Failed to refresh series', 'error');
            }
        }

        function attachDropdownListener(dropdownId, dropdownEl) {
            // Remove any existing listener for this dropdown
            const existingListener = ACTIVE_DROPDOWN_LISTENERS.get(dropdownId);
            if (existingListener) {
                document.removeEventListener('click', existingListener);
                ACTIVE_DROPDOWN_LISTENERS.delete(dropdownId);
            }
            const listener = function closeDropdown(e) {
                if (!dropdownEl.contains(e.target)) {
                    dropdownEl.remove();
                    document.removeEventListener('click', listener);
                    ACTIVE_DROPDOWN_LISTENERS.delete(dropdownId);
                }
            };
            ACTIVE_DROPDOWN_LISTENERS.set(dropdownId, listener);
            setTimeout(() => {
                document.addEventListener('click', listener);
            }, 0);
        }

        function removeDropdown(dropdownId) {
            const dropdown = document.getElementById(dropdownId);
            if (dropdown) {
                dropdown.remove();
                const listener = ACTIVE_DROPDOWN_LISTENERS.get(dropdownId);
                if (listener) {
                    document.removeEventListener('click', listener);
                    ACTIVE_DROPDOWN_LISTENERS.delete(dropdownId);
                }
            }
        }

        function showHistoryMaintenanceMenu(e) {
            const existing = document.getElementById('maintenance-dropdown');
            if (existing) { removeDropdown('maintenance-dropdown'); return; }

            const btn = e?.target?.closest('.toolbar-btn');
            const dropdown = document.createElement('div');
            dropdown.id = 'maintenance-dropdown';
            dropdown.className = 'dropdown-menu active';
            dropdown.style.position = 'absolute';
            dropdown.style.top = (btn.offsetTop + btn.offsetHeight + 5) + 'px';
            dropdown.style.left = btn.offsetLeft + 'px';
            dropdown.innerHTML = `
                <div class="dropdown-item" onclick="cleanupUnavailableEpisodes(); removeDropdown('maintenance-dropdown');">
                    <span>&#128465;</span> Remove Unavailable Episodes
                </div>
                <div class="dropdown-item" onclick="sortHistory(); removeDropdown('maintenance-dropdown');">
                    <span>&#8645;</span> Sort History
                </div>
                <div class="dropdown-divider"></div>
                <div class="dropdown-item" onclick="refreshAllSeries(); removeDropdown('maintenance-dropdown');">
                    <span>&#128260;</span> Refresh All Series
                </div>
            `;
            document.body.appendChild(dropdown);
            attachDropdownListener('maintenance-dropdown', dropdown);
        }

        async function cleanupUnavailableEpisodes() {
            if (!confirm('Remove unavailable episodes from history?')) return;
            try {
                showToast('Cleaning up unavailable episodes...', 'info');
                const res = await fetch('/api/v1/history/cleanup', { method: 'POST' });
                if (res.ok) {
                    showToast('Cleanup completed', 'success');
                    fetchHistoryData();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Cleanup failed', 'error');
                }
            } catch (e) {
                showToast('Cleanup failed', 'error');
            }
        }

        async function sortHistory() {
            try {
                showToast('Sorting history...', 'info');
                const res = await fetch('/api/v1/history/sort', { method: 'POST' });
                if (res.ok) {
                    showToast('History sorted', 'success');
                    fetchHistoryData();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Sort failed', 'error');
                }
            } catch (e) {
                showToast('Sort failed', 'error');
            }
        }

        async function refreshAllSeries() {
            if (!historyData || historyData.length === 0) {
                showToast('No series to refresh', 'warning');
                return;
            }
            showToast('Refreshing all series...', 'info');
            let refreshed = 0;
            for (const series of historyData) {
                if (series.seriesId) {
                    try {
                        await fetch(`/api/v1/history/update-series/${series.seriesId}`, { method: 'POST' });
                        refreshed++;
                    } catch (e) {
                        console.error('Failed to refresh series:', series.seriesId);
                    }
                }
            }
            showToast(`Refreshed ${refreshed} series`, 'success');
            fetchHistoryData();
        }

        function toggleSortMenu(e) {
            const existing = document.getElementById('sort-dropdown');
            if (existing) { removeDropdown('sort-dropdown'); return; }

            const btn = e?.target?.closest('.toolbar-btn');
            const dropdown = document.createElement('div');
            dropdown.id = 'sort-dropdown';
            dropdown.className = 'dropdown-menu active';
            dropdown.style.position = 'absolute';
            dropdown.style.top = (btn.offsetTop + btn.offsetHeight + 5) + 'px';
            dropdown.style.left = btn.offsetLeft + 'px';
            dropdown.innerHTML = `
                <div class="dropdown-item" onclick="sortHistoryBy('title'); removeDropdown('sort-dropdown');">
                    <span>&#128199;</span> By Title
                </div>

                <div class="dropdown-item" onclick="sortHistoryBy('status'); removeDropdown('sort-dropdown');">
                    <span>&#9989;</span> By Status
                </div>
            `;
            document.body.appendChild(dropdown);
            attachDropdownListener('sort-dropdown', dropdown);
        }

        function sortHistoryBy(field) {
            if (!historyData) return;
            historyData = [...historyData].sort((a, b) => {
                if (field === 'title') return (a.seriesTitle || '').localeCompare(b.seriesTitle || '');

                if (field === 'status') {
                    const aStatus = (a.downloadedEpisodes||0) >= (a.totalEpisodes||0) ? 2 : (a.downloadedEpisodes||0) > 0 ? 1 : 0;
                    const bStatus = (b.downloadedEpisodes||0) >= (b.totalEpisodes||0) ? 2 : (b.downloadedEpisodes||0) > 0 ? 1 : 0;
                    return aStatus - bStatus;
                }
                return 0;
            });
            renderHistoryContent();
            showToast(`Sorted by ${field}`, 'success');
        }
        
        // ================== HISTORY DETAIL & PARTIAL DOWNLOAD ==================
        
        function isEpisodePartiallyDownloaded(episode) {
            // Upstream parity: partial download tracking can be disabled in History settings
            if (config?.history?.checkPartialDownloads === false) return false;
            const downloadedDubs = episode.downloadedDubLang || [];
            const downloadedSubs = episode.downloadedSoftSubs || [];
            const requestedDubs = episode.requestedDubLang || config?.download?.dubLanguages || [];
            const requestedSubs = episode.requestedSoftSubs || config?.download?.softSubs || [];
            
            const hasSomeDownloads = downloadedDubs.length > 0 || downloadedSubs.length > 0;
            if (!hasSomeDownloads) return false;
            
            const missingDubs = requestedDubs.filter(d => !downloadedDubs.includes(d));
            const missingSubs = requestedSubs.filter(s => !downloadedSubs.includes(s));
            
            return missingDubs.length > 0 || missingSubs.length > 0;
        }
        
        function getEpisodeDownloadStatus(episode) {
            if (episode.wasDownloaded) {
                return { class: 'status-full', icon: '&#10004;' };
            }
            if (isEpisodePartiallyDownloaded(episode)) {
                return { class: 'status-partial', icon: '&#10004;' };
            }
            return { class: 'status-none', icon: '' };
        }
        
        function getEpisodeStatusTooltip(episode) {
            const downloadedDubs = episode.downloadedDubLang || [];
            const downloadedSubs = episode.downloadedSoftSubs || [];
            const requestedDubs = episode.requestedDubLang || config?.download?.dubLanguages || [];
            const requestedSubs = episode.requestedSoftSubs || config?.download?.softSubs || [];
            
            let tooltip = '';
            if (downloadedDubs.length > 0) {
                tooltip += `Downloaded dubs: ${downloadedDubs.join(', ')}\n`;
            }
            if (downloadedSubs.length > 0) {
                tooltip += `Downloaded subs: ${downloadedSubs.join(', ')}\n`;
            }
            
            const missingDubs = requestedDubs.filter(d => !downloadedDubs.includes(d));
            const missingSubs = requestedSubs.filter(s => !downloadedSubs.includes(s));
            const missing = [...missingDubs, ...missingSubs];
            
            if (missing.length > 0) {
                tooltip += `Available but missing: ${missing.join(', ')}`;
            }
            
            return tooltip || 'Not downloaded';
        }
        
        async function showHistorySeriesDetail(seriesId) {
            const series = historyData.find(s => s.seriesId === seriesId);
            if (!series) return;
            
            const modalTitle = document.getElementById('modal-title');
            const modalBody = document.getElementById('modal-body');
            const modalFooter = document.getElementById('modal-footer');
            const modalEl = document.getElementById('modal');
            if (modalTitle) modalTitle.textContent = series.seriesTitle || 'Series Details';
            if (modalBody) modalBody.innerHTML = '<div class="loading"><div class="spinner"></div>Loading episodes...</div>';
            if (modalFooter) modalFooter.innerHTML = `
                <button class="header-btn" onclick="closeModal()">Close</button>
                <button class="header-btn" onclick="showSeriesSettingsOverride('${escapeJsString(seriesId)}')">Settings</button>
                ${series.sonarrSeriesId ? `<button class="header-btn" onclick="matchEpisodesForSeries('${escapeJsString(seriesId)}'); closeModal();">Match Episodes</button>` : ''}
            `;
            if (modalEl) modalEl.classList.add('active');

            // Populate the full season (downloaded + missing) from Crunchyroll the first time this
            // series is opened, so History shows everything for it - not only what was downloaded.
            // Once per series per session. The backend may re-key the series to its real CR id, so
            // afterwards we also match by title.
            const seriesTitle = series.seriesTitle;
            window._seriesPopulated = window._seriesPopulated || {};
            if (!window._seriesPopulated[seriesId]) {
                window._seriesPopulated[seriesId] = true;
                try { await fetch(`/api/v1/history/update-series/${encodeURIComponent(seriesId)}`, { method: 'POST' }); }
                catch (e) { /* keep whatever is already in history */ }
                historyRichData = null;
            }

            // Fetch rich data if needed
            let richSeries = null;
            if (historyRichData && historyRichData.length > 0) {
                richSeries = historyRichData.find(s => s.seriesId === seriesId) || historyRichData.find(s => s.seriesTitle === seriesTitle);
            }

            if (!richSeries) {
                try {
                    const res = await fetch('/api/v1/history/rich');
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    const data = await res.json();
                    historyRichData = data || [];
                    richSeries = historyRichData.find(s => s.seriesId === seriesId) || historyRichData.find(s => s.seriesTitle === seriesTitle);
                } catch (e) {
                    const modalBody = document.getElementById('modal-body');
                    if (modalBody) modalBody.innerHTML = '<div class="empty-state"><div class="empty-state-title">Failed to load episodes</div></div>';
                    return;
                }
            }
            
            if (!richSeries) {
                const modalBody = document.getElementById('modal-body');
                if (modalBody) modalBody.innerHTML = '<div class="empty-state"><div class="empty-state-title">No episode data found</div></div>';
                return;
            }
            
            renderHistorySeriesDetailContent(richSeries);
        }
        
        function renderHistorySeriesDetailContent(series) {
            const body = document.getElementById('modal-body');
            if (!body) return;
            const requestedDubs = config?.download?.dubLanguages || ['ja-JP'];
            const requestedSubs = config?.download?.softSubs || ['en-US'];
            
            let html = `
                <div class="history-detail-header">
                    <div class="history-detail-poster">
                        ${series.thumbnailImageUrl && isSafeUrl(series.thumbnailImageUrl) ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(series.thumbnailImageUrl))}" alt="" onerror="this.outerHTML='📺'">` : '📺'}
                    </div>
                    <div class="history-detail-info">
                        <div class="history-detail-title">${escapeHtml(series.seriesTitle || 'Unknown')}</div>
                        <div class="history-detail-meta">${escapeHtml(series.seriesDescription || '')}</div>
                        <div class="mt-10">
                            <div style="font-size:0.8em; color:var(--text-muted); margin-bottom:4px;">Episodes:</div>
                            <div>${series.downloadedEpisodes || 0} / ${series.totalEpisodes || 0} downloaded${series.hasNewEpisodes ? ' <span class="lang-badge selected">New</span>' : ''}</div>
                        </div>
                    </div>
                </div>
            `;
            
            // Render seasons
            const seasons = series.seasons || [];
            if (seasons.length === 0) {
                html += '<div class="empty-state"><div class="empty-state-title">No seasons found</div></div>';
                body.innerHTML = html;
                return;
            }
            
            html += seasons.map(season => {
                const episodesHtml = (season.episodes || []).map(ep => {
                    const status = getEpisodeDownloadStatus(ep);
                    const tooltip = getEpisodeStatusTooltip(ep);
                    
                    return `
                        <div class="history-episode">
                            <div class="history-episode-number">${ep.episode || '?'}</div>
                            <div class="history-episode-title">${escapeHtml(ep.episodeTitle || 'Unknown Episode')}</div>
                            <div class="history-episode-langs">
                                ${(ep.downloadedDubLang || []).map(d => `<span class="lang-badge downloaded">${escapeHtml(d)}</span>`).join('')}
                            </div>
                            <div class="history-episode-status">
                                <div class="tooltip-container">
                                    <div class="status-icon ${status.class}">${status.icon}</div>
                                    <div class="tooltip-text">${escapeHtml(tooltip).replace(/\n/g, '<br>')}</div>
                                </div>
                            </div>
                            ${!ep.wasDownloaded ? `<button class="btn-icon" onclick="event.stopPropagation(); addHistoryEpisodeToQueue('${escapeJsString(ep.episodeId)}', '${escapeJsString(series.seriesTitle || '')}', '${escapeJsString(ep.episodeTitle || '')}')" title="Add to queue">&#128229;</button>` : ''}
                        </div>
                    `;
                }).join('');
                
                return `
                    <div class="history-season">
                        <div class="history-season-header" onclick="toggleSeasonCollapse(this)">
                            <div>
                                <span class="history-season-title">${escapeHtml(season.seasonTitle || `Season ${season.seasonNum || 1}`)}</span>
                                <span style="font-size:0.8em; color:var(--text-muted); margin-left:8px;">${season.episodes?.length || 0} episodes</span>
                            </div>
                            <div class="history-season-actions">
                                <button class="btn-icon" onclick="event.stopPropagation(); showSeasonSettingsOverride('${escapeJsString(season.seasonId)}')" title="Settings">&#9881;</button>
                                <button class="btn-icon" onclick="event.stopPropagation(); downloadSeason('${escapeJsString(series.seriesId)}', '${escapeJsString(season.seasonId)}')" title="Download season">&#9660;</button>
                            </div>
                        </div>
                        <div class="history-season-body">
                            ${episodesHtml}
                        </div>
                    </div>
                `;
            }).join('');
            
            body.innerHTML = html;
        }
        
        function toggleSeasonCollapse(header) {
            const body = header.nextElementSibling;
            if (body) {
                body.style.display = body.style.display === 'none' ? 'block' : 'none';
            }
        }
        
        async function addHistoryEpisodeToQueue(episodeId, seriesTitle, episodeTitle) {
            try {
                const res = await fetch('/api/v1/queue', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        episodeId: episodeId,
                        title: episodeTitle || 'Unknown Episode',
                        seriesTitle: seriesTitle || 'Unknown'
                    })
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                showToast('Added to queue', 'success');
            } catch (e) {
                showToast('Failed to add to queue', 'error');
            }
        }
        
        async function downloadSeason(seriesId, seasonId) {
            if (!confirm('This will add all episodes in this season to the queue. Continue?')) return;
            try {
                const res = await fetch(`/api/v1/series/${seriesId}/episodes`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const episodes = await res.json();
                let added = 0;
                
                for (const ep of (episodes || [])) {
                    if (ep.seasonId === seasonId || ep.seasonNumber === parseInt(seasonId)) {
                        const queueRes = await fetch('/api/v1/queue', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                episodeId: ep.id,
                                title: ep.title || 'Unknown',
                                seriesTitle: ep.seriesTitle || 'Unknown',
                                seasonNumber: ep.seasonNumber || 1,
                                episodeNumber: ep.episodeNumber || 1,
                                locale: ep.locale || 'ja-JP'
                            })
                        });
                        if (queueRes.ok) added++;
                    }
                }
                
                showToast(`Added ${added} episode(s) to queue`, 'success');
            } catch (e) {
                showToast('Failed to queue season', 'error');
            }
        }
        
        // ================== HISTORY SEARCH POPUP ==================
        
        function openHistorySearchPopup() {
            historySearchPopupOpen = true;
            const popup = document.getElementById('history-search-popup');
            const input = document.getElementById('history-search-input');
            if (popup && input) {
                popup.style.display = 'block';
                input.focus();
                updateHistorySearchPopup(input.value);
            }
            
            // Close popup when clicking outside
            const existingListener = document._historySearchClickListener;
            if (existingListener) {
                document.removeEventListener('click', existingListener);
            }
            document._historySearchClickListener = closeHistorySearchOnClickOutside;
            setTimeout(() => {
                document.addEventListener('click', document._historySearchClickListener);
            }, 0);
        }
        
        function closeHistorySearchPopup() {
            const popup = document.getElementById('history-search-popup');
            if (popup) popup.style.display = 'none';
            historySearchPopupOpen = false;
            if (document._historySearchClickListener) {
                document.removeEventListener('click', document._historySearchClickListener);
                document._historySearchClickListener = null;
            }
            // Update orange dot indicator without full re-render
            if (currentPage === 'history') {
                const input = document.getElementById('history-search-input');
                if (input) {
                    const existingDot = input.parentElement?.querySelector('.search-active-dot');
                    if (historySearchQuery && !existingDot) {
                        const dot = document.createElement('div');
                        dot.className = 'search-active-dot';
                        input.parentElement?.appendChild(dot);
                    } else if (!historySearchQuery && existingDot) {
                        existingDot.remove();
                    }
                }
            }
        }
        
        function closeHistorySearchOnClickOutside(e) {
            const popup = document.getElementById('history-search-popup');
            const input = document.getElementById('history-search-input');
            if (popup && !popup.contains(e.target) && !input?.contains(e.target) && !input?.parentElement?.contains(e.target)) {
                closeHistorySearchPopup();
            }
        }
        
        let historySearchDebounce;
        function onHistorySearchInput(value) {
            historySearchQuery = value;
            clearTimeout(historySearchDebounce);
            historySearchDebounce = setTimeout(() => {
                filterHistory(value);
                updateHistorySearchPopup(value);
            }, HISTORY_SEARCH_DEBOUNCE_MS);
        }
        
        function updateHistorySearchPopup(query) {
            const popup = document.getElementById('history-search-popup');
            if (!popup) return;
            
            const filtered = query 
                ? historyData.filter(item => 
                    (item.seriesTitle || '').toLowerCase().includes(query.toLowerCase()) ||
                    (item.seriesDescription || '').toLowerCase().includes(query.toLowerCase()) ||
                    (item.sonarrSlugTitle || '').toLowerCase().includes(query.toLowerCase())
                  )
                : historyData;
            
            if (filtered.length === 0) {
                popup.innerHTML = '<div style="padding:15px; color:var(--text-muted);">No matching series</div>';
                return;
            }
            
            popup.innerHTML = filtered.slice(0, 10).map(item => `
                <div class="search-result-item" onclick="showHistorySeriesDetail('${escapeJsString(item.seriesId)}'); closeHistorySearchPopup();" style="padding:10px;">
                    <div style="font-weight:500;">${escapeHtml(item.seriesTitle || 'Unknown')}</div>
                    <div style="font-size:0.8em; color:var(--text-secondary);">${escapeHtml((item.seriesDescription || '').substring(0, 60))}${(item.seriesDescription || '').length > 60 ? '...' : ''}</div>
                </div>
            `).join('');
        }
        
        // ================== SONARR MENU ==================
        
        function openSonarrMenu(event) {
            event.stopPropagation();
            const existing = document.getElementById('sonarr-dropdown');
            if (existing) { removeDropdown('sonarr-dropdown'); return; }
            
            const btn = event.target.closest('.toolbar-btn');
            const dropdown = document.createElement('div');
            dropdown.id = 'sonarr-dropdown';
            dropdown.className = 'dropdown-menu active';
            dropdown.style.position = 'absolute';
            dropdown.style.top = (btn.offsetTop + btn.offsetHeight + 5) + 'px';
            dropdown.style.left = btn.offsetLeft + 'px';
            dropdown.innerHTML = `
                <div class="dropdown-item" onclick="matchAllSeriesSonarr(); removeDropdown('sonarr-dropdown');">
                    <span>&#127758;</span> Match All Series
                </div>
                <div class="dropdown-divider"></div>
                <div class="dropdown-item" onclick="fetchHistoryData(); removeDropdown('sonarr-dropdown');">
                    <span>&#128260;</span> Refresh History
                </div>
            `;
            document.body.appendChild(dropdown);
            attachDropdownListener('sonarr-dropdown', dropdown);
        }
        
        // One-shot-per-session background match so Sonarr data appears on the History page
        // automatically (the manual "Match All Series" menu item still works for re-runs).
        async function maybeAutoMatchSonarr() {
            if (window._sonarrAutoMatched) return;
            if (!config?.sonarr?.enabled) return;
            if (!Array.isArray(historyData) || !historyData.some(s => s && !s.sonarrSeriesId)) return;
            window._sonarrAutoMatched = true; // set first so polling never re-triggers or loops
            try {
                const res = await fetch('/api/v1/history/sonarr/match-series', { method: 'POST' });
                if (res.ok) {
                    const data = await res.json().catch(() => ({}));
                    if (data && data.matched > 0) fetchHistoryData();
                }
            } catch (e) { /* silent; manual "Match All Series" remains available */ }
        }

        async function matchAllSeriesSonarr() {
            try {
                showToast('Matching all series with Sonarr...', 'info');
                const res = await fetch('/api/v1/history/sonarr/match-series', { method: 'POST' });
                if (res.ok) {
                    const data = await res.json().catch(() => ({}));
                    const noSonarr = data.sonarrSeriesCount === 0;
                    showToast(data.message || 'Sonarr match completed', noSonarr ? 'error' : 'success');
                    fetchHistoryData();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Sonarr match failed', 'error');
                }
            } catch (e) {
                showToast('Sonarr match failed', 'error');
            }
        }
        
        async function matchEpisodesForSeries(seriesId) {
            try {
                showToast('Matching episodes with Sonarr...', 'info');
                const res = await fetch(`/api/v1/history/sonarr/match-episodes/${seriesId}`, { method: 'POST' });
                if (res.ok) {
                    showToast('Episodes matched successfully', 'success');
                    fetchHistoryData();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Episode match failed', 'error');
                }
            } catch (e) {
                showToast('Episode match failed', 'error');
            }
        }
        
        async function testSonarrConnection() {
            const btn = document.getElementById('sonarr-test-btn');
            const resultRow = document.getElementById('sonarr-test-result');
            const msg = document.getElementById('sonarr-test-msg');
            const apiKeyVal = document.getElementById('setting-sonarr-apikey')?.value;
            const payload = {
                host: document.getElementById('setting-sonarr-host')?.value || null,
                port: parseInt(document.getElementById('setting-sonarr-port')?.value) || 0,
                useSsl: document.getElementById('setting-sonarr-ssl')?.checked || false,
                urlBase: document.getElementById('setting-sonarr-urlbase')?.value || null
            };
            // Only send the key if the user typed a real one; otherwise the server reuses the stored key.
            if (apiKeyVal && apiKeyVal !== '[configured]') payload.apiKey = apiKeyVal;

            if (btn) { btn.disabled = true; btn.textContent = 'Testing...'; }
            if (resultRow) resultRow.style.display = '';
            if (msg) { msg.textContent = 'Testing connection...'; msg.style.color = 'var(--text-secondary)'; }
            try {
                const res = await fetch('/api/v1/config/sonarr/test', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(payload)
                });
                const data = await res.json().catch(() => ({}));
                const ok = !!data.success;
                const text = data.message || (ok ? 'Connected.' : 'Connection failed.');
                if (msg) {
                    msg.textContent = (ok ? '✓ ' : '✗ ') + text;
                    msg.style.color = ok ? 'var(--accent-green)' : 'var(--accent-red, #e5534b)';
                }
                showToast(text, ok ? 'success' : 'error');
            } catch (e) {
                if (msg) { msg.textContent = '✗ Test request failed: ' + e.message; msg.style.color = 'var(--accent-red, #e5534b)'; }
                showToast('Sonarr test failed', 'error');
            } finally {
                if (btn) { btn.disabled = false; btn.textContent = 'Test Connection'; }
            }
        }

        // ================== SETTINGS OVERRIDE ==================

        function showSeriesSettingsOverride(seriesId) {
            const series = historyData.find(s => s.seriesId === seriesId);
            if (!series) return;
            
            const modalTitle = document.getElementById('modal-title');
            const modalBody = document.getElementById('modal-body');
            const modalFooter = document.getElementById('modal-footer');
            const modalEl = document.getElementById('modal');
            if (modalTitle) modalTitle.textContent = 'Series Settings Override';
            if (modalBody) modalBody.innerHTML = `
                <div class="form-group">
                    <label class="form-label">Video Quality</label>
                    <select class="form-select mw-150" id="override-quality-video">
                        <option value="best">Best Available</option>
                        <option value="1080">1080</option>
                        <option value="720">720</option>
                        <option value="480">480</option>
                        <option value="360">360</option>
                        <option value="240">240</option>
                        <option value="worst">Worst</option>
                    </select>
                </div>
                <div class="form-group">
                    <label class="form-label">Dub Languages</label>
                    <select class="form-select mh-120" id="override-dub-langs" multiple>
                        ${LANG_OPTIONS.map(o => `<option value="${escapeHtmlAttribute(o.value)}">${escapeHtml(o.label)}</option>`).join('')}
                    </select>
                </div>
                <div class="form-group">
                    <label class="form-label">Softsubs Languages</label>
                    <select class="form-select mh-120" id="override-soft-subs" multiple>
                        ${LANG_OPTIONS.map(o => `<option value="${escapeHtmlAttribute(o.value)}">${escapeHtml(o.label)}</option>`).join('')}
                    </select>
                </div>
            `;
            if (modalFooter) modalFooter.innerHTML = `
                <button class="header-btn" onclick="closeModal()">Cancel</button>
                <button class="header-btn primary" onclick="saveSeriesSettingsOverride('${escapeJsString(seriesId)}')">Save</button>
            `;
            if (modalEl) modalEl.classList.add('active');
        }
        
        async function saveSeriesSettingsOverride(seriesId) {
            const videoQuality = document.getElementById('override-quality-video')?.value;
            const dubLangs = getMultiSelect('override-dub-langs');
            const softSubs = getMultiSelect('override-soft-subs');
            
            try {
                const res = await fetch(`/api/v1/history/series/${seriesId}/settings`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        videoQuality,
                        dubLanguages: dubLangs,
                        softSubs: softSubs
                    })
                });
                if (res.ok) {
                    showToast('Series settings saved', 'success');
                    closeModal();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Failed to save settings', 'error');
                }
            } catch (e) {
                showToast('Failed to save settings', 'error');
            }
        }
        
        function showSeasonSettingsOverride(seasonId) {
            const modalTitle = document.getElementById('modal-title');
            const modalBody = document.getElementById('modal-body');
            const modalFooter = document.getElementById('modal-footer');
            const modalEl = document.getElementById('modal');
            if (modalTitle) modalTitle.textContent = 'Season Settings Override';
            if (modalBody) modalBody.innerHTML = `
                <div class="form-group">
                    <label class="form-label">Video Quality</label>
                    <select class="form-select mw-150" id="override-season-quality-video">
                        <option value="best">Best Available</option>
                        <option value="1080">1080</option>
                        <option value="720">720</option>
                        <option value="480">480</option>
                        <option value="360">360</option>
                        <option value="240">240</option>
                        <option value="worst">Worst</option>
                    </select>
                </div>
                <div class="form-group">
                    <label class="form-label">Dub Languages</label>
                    <select class="form-select mh-120" id="override-season-dub-langs" multiple>
                        ${LANG_OPTIONS.map(o => `<option value="${escapeHtmlAttribute(o.value)}">${escapeHtml(o.label)}</option>`).join('')}
                    </select>
                </div>
                <div class="form-group">
                    <label class="form-label">Softsubs Languages</label>
                    <select class="form-select mh-120" id="override-season-soft-subs" multiple>
                        ${LANG_OPTIONS.map(o => `<option value="${escapeHtmlAttribute(o.value)}">${escapeHtml(o.label)}</option>`).join('')}
                    </select>
                </div>
            `;
            if (modalFooter) modalFooter.innerHTML = `
                <button class="header-btn" onclick="closeModal()">Cancel</button>
                <button class="header-btn primary" onclick="saveSeasonSettingsOverride('${escapeJsString(seasonId)}')">Save</button>
            `;
            if (modalEl) modalEl.classList.add('active');
        }
        
        async function saveSeasonSettingsOverride(seasonId) {
            const videoQuality = document.getElementById('override-season-quality-video')?.value;
            const dubLangs = getMultiSelect('override-season-dub-langs');
            const softSubs = getMultiSelect('override-season-soft-subs');
            
            try {
                const res = await fetch(`/api/v1/history/season/${seasonId}/settings`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        videoQuality,
                        dubLanguages: dubLangs,
                        softSubs: softSubs
                    })
                });
                if (res.ok) {
                    showToast('Season settings saved', 'success');
                    closeModal();
                } else {
                    const err = await res.json().catch(() => ({}));
                    showToast(err.message || 'Failed to save settings', 'error');
                }
            } catch (e) {
                showToast('Failed to save settings', 'error');
            }
        }
        
        async function downloadSeries(id) {
            if (!confirm('This will add all episodes in this series to the queue. Continue?')) return;
            try {
                // Get episodes and add all to queue
                const res = await fetch(`/api/v1/series/${id}/episodes`);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const episodes = await res.json();
                let added = 0;
                
                for (const ep of (episodes || [])) {
                    if (ep.id) {
                        const queueRes = await fetch('/api/v1/queue', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({
                                episodeId: ep.id,
                                title: ep.title || 'Unknown',
                                seriesTitle: ep.seriesTitle || 'Unknown',
                                seasonNumber: ep.seasonNumber || 1,
                                episodeNumber: ep.episodeNumber || 1,
                                locale: ep.locale || 'ja-JP'
                            })
                        });
                        if (queueRes.ok) added++;
                    }
                }
                
                showToast(`Added ${added} episode(s) to queue`, 'success');
            } catch (e) {
                showToast('Failed to queue series', 'error');
            }
        }

        function closeModal() {
            const modal = document.getElementById('modal');
            if (modal) modal.classList.remove('active');
        }

        // ESC key to close modal
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                const modal = document.getElementById('modal');
                if (modal && modal.classList.contains('active')) {
                    closeModal();
                }
                // Also close any open dropdowns (properly remove listeners)
                document.querySelectorAll('.dropdown-menu.active').forEach(d => {
                    removeDropdown(d.id);
                });
                // Close history search popup
                if (historySearchPopupOpen) closeHistorySearchPopup();
            }
        });

        function showToast(message, type = 'success') {
            const container = document.getElementById('toast-container');
            if (!container) return;
            const toast = document.createElement('div');
            toast.className = `toast ${type}`;
            toast.innerHTML = `<span>${type === 'success' ? '&#10004;' : type === 'error' ? '&#10060;' : '&#9888;'}</span><span>${escapeHtml(message)}</span>`;
            container.appendChild(toast);
            setTimeout(() => { toast.style.opacity = '0'; toast.style.transform = 'translateX(100%)'; setTimeout(() => toast.remove(), 300); }, TOAST_DISPLAY_DURATION_MS);
        }

        function getHistoryStatusBadge(series) {
            const downloaded = series.downloadedEpisodes || 0;
            const total = series.totalEpisodes || 0;
            if (total === 0) return '<span class="badge badge-queued">Empty</span>';
            if (downloaded >= total) return '<span class="badge badge-done">Complete</span>';
            if (downloaded > 0) return '<span class="badge badge-downloading">Partial</span>';
            return '<span class="badge badge-queued">None</span>';
        }

        function formatDownloadSpeed(bytesPerSec) {
            if (!bytesPerSec || bytesPerSec === 0) return '';
            // Use MB/s format (matching qma default DownloadSpeedInBits=false)
            return (bytesPerSec / 1000000.0).toFixed(2) + ' MB/s';
        }

        function formatETA(seconds) {
            if (!seconds || seconds <= 0) return '';
            const h = Math.floor(seconds / 3600);
            const m = Math.floor((seconds % 3600) / 60);
            const s = Math.floor(seconds % 60);
            if (h > 0) return `ETA: ${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
            return `ETA: ${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        }

        function getDoingText(progress) {
            if (!progress) return 'Queued';
            // Handle waiting for retry
            if (progress.retryAtUtc) {
                const retryDate = new Date(progress.retryAtUtc);
                if (!isNaN(retryDate.getTime()) && retryDate > new Date()) {
                    const retryTime = retryDate.toLocaleTimeString();
                    return `Rate limited, retrying at ${escapeHtml(retryTime)}`;
                }
            }
            // Use doing if not empty, otherwise derive from state
            if (progress.doing && progress.doing.trim().length > 0) {
                return escapeHtml(progress.doing);
            }
            const state = (progress.state || 'queued').toLowerCase();
            if (state === 'downloading') return 'Downloading...';
            if (state === 'processing') return 'Processing...';
            if (state === 'done') return 'Complete';
            if (state === 'error') return 'Error';
            if (state === 'paused') return 'Paused';
            if (state === 'cancelled') return 'Cancelled';
            return 'Queued';
        }

        let eventSource = null;
        
        function startPolling() {
            // Check auth status periodically
            checkAuthStatus();
            authIntervalId = setInterval(checkAuthStatus, AUTH_NOTIFICATION_THROTTLE_MS);
            
            // Use SSE for real-time queue updates instead of polling
            startQueueSSE();
            
            // Keep polling history (no SSE for history yet)
            if (!historyIntervalId) {
                historyIntervalId = setInterval(() => {
                    if (currentPage === 'history') fetchHistoryData();
                }, HISTORY_POLL_INTERVAL_MS);
            }
            
            // Clear intervals and SSE on page unload
            window.addEventListener('beforeunload', () => {
                if (authIntervalId) clearInterval(authIntervalId);
                if (historyIntervalId) clearInterval(historyIntervalId);
                if (sseReconnectTimeout) clearTimeout(sseReconnectTimeout);
                if (eventSource) {
                    eventSource.close();
                    eventSource = null;
                }
            });
        }
        
        let sseRetryCount = 0;
        let sseReconnecting = false;
        let sseReconnectTimeout = null;

        function startQueueSSE() {
            if (typeof EventSource === 'undefined') {
                console.warn('EventSource not supported, falling back to polling');
                return;
            }
            // Clean up existing connection
            if (eventSource) {
                eventSource.close();
                eventSource = null;
            }
            // Clear any pending reconnect
            if (sseReconnectTimeout) {
                clearTimeout(sseReconnectTimeout);
                sseReconnectTimeout = null;
            }
            if (sseRetryCount >= SSE_MAX_RETRIES) {
                console.error('SSE max retries exceeded. Falling back to polling.');
                startQueuePollingFallback();
                return;
            }
            
            // EventSource cannot set request headers, so when an API key is configured
            // it must travel in the query string (the one place ?apiKey= is required).
            const sseKey = localStorage.getItem('cruncharrApiKey');
            eventSource = new EventSource('/api/v1/queue/sse' + (sseKey ? ('?apiKey=' + encodeURIComponent(sseKey)) : ''));
            
            eventSource.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    updateQueueData(data.items || []);
                    // Also refresh stats on SSE update so stat cards stay current
                    fetchQueueStats();
                } catch (e) {
                    console.error('Failed to parse SSE data:', e);
                }
            };
            
            eventSource.onerror = (error) => {
                if (sseReconnecting) return;
                sseReconnecting = true;
                console.error('SSE connection error:', error);
                if (eventSource) {
                    eventSource.close();
                    eventSource = null;
                }
                sseRetryCount++;
                const delay = Math.min(SSE_BASE_RETRY_DELAY_MS * Math.pow(2, sseRetryCount), SSE_MAX_RETRY_DELAY_MS);
                sseReconnectTimeout = setTimeout(() => {
                    sseReconnecting = false;
                    if (!eventSource) {
                        startQueueSSE();
                    }
                }, delay);
            };
            
            eventSource.onopen = () => {
                sseRetryCount = 0;
            };
        }
        
        let lastAuthWarning = 0;
        async function checkAuthStatus() {
            try {
                const controller = new AbortController();
                const timeoutId = setTimeout(() => controller.abort(), AUTH_STATUS_TIMEOUT_MS);
                const res = await fetch('/api/v1/auth/status', { signal: controller.signal });
                clearTimeout(timeoutId);
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const status = await res.json();
                authStatus = status;
                
                const now = Date.now();
                if (!status.isAuthenticated) {
                    if (now - lastAuthWarning > AUTH_WARNING_THROTTLE_MS) {
                        lastAuthWarning = now;
                        showToast('You are not logged in. Please go to Account tab to log in.', 'warning');
                    }
                } else if (!status.hasPremium) {
                    // Check if there are premium items in queue
                    const hasPremiumItems = queueData.some(i => i.episode?.isPremium);
                    if (hasPremiumItems && now - lastAuthWarning > AUTH_WARNING_THROTTLE_MS) {
                        lastAuthWarning = now;
                        showToast('No premium subscription detected. Premium content will fail to download.', 'warning');
                    }
                }
            } catch (e) {
                // Only log once to avoid console spam
                if (!window._authCheckFailed) {
                    window._authCheckFailed = true;
                    console.warn('Auth status check failed (will retry silently):', e.message);
                }
            }
        }
        
        function updateConnectionStatus(isConnected) {
            const el = document.getElementById('connection-status');
            if (el) {
                // connection-status is now the status dot inside the version pill.
                el.style.background = isConnected ? 'var(--accent-green)' : 'var(--accent-red)';
                el.style.boxShadow = isConnected ? '0 0 6px var(--accent-green)' : '0 0 6px var(--accent-red)';
                const pill = el.closest('.version-pill');
                if (pill) pill.title = isConnected ? 'Connected' : 'Disconnected';
            }
        }

        // Resolve a Crunchyroll avatar (bare filename -> full URL; pass through real URLs)
        function resolveAvatarUrl(avatar) {
            if (!avatar) return null;
            if (/^https?:\/\//i.test(avatar)) return avatar;
            return 'https://static.crunchyroll.com/assets/avatar/170x170/' + avatar;
        }

        // Top-right profile chip: show the logged-in account icon + username
        async function updateTopbarProfile() {
            const chip = document.getElementById('topbar-profile');
            const nameEl = document.getElementById('topbar-profile-name');
            const avatarEl = document.getElementById('topbar-profile-avatar');
            if (!chip) return;
            try {
                const res = await fetch('/api/v1/auth/status');
                if (!res.ok) throw new Error('status');
                const s = await res.json();
                if (s.isAuthenticated) {
                    // The account username is often empty; the meaningful label is the active
                    // profile name. Fall back to username, then a generic "Account".
                    const selected = Array.isArray(s.multiProfile) ? s.multiProfile.find(p => p.isSelected) : null;
                    const displayName = (selected && selected.profileName)
                        || (s.username && s.username !== '???' ? s.username : '')
                        || 'Account';
                    chip.classList.remove('hidden');
                    if (nameEl) nameEl.textContent = displayName;
                    const url = resolveAvatarUrl(s.avatar);
                    if (avatarEl) {
                        avatarEl.innerHTML = url && isSafeUrl(url)
                            ? `<img loading="lazy" decoding="async" src="${escapeHtml(crImg(url))}" alt="" onerror="this.outerHTML='<span>&#128100;</span>'">`
                            : '<span>&#128100;</span>';
                    }
                } else {
                    chip.classList.add('hidden');
                }
            } catch (e) {
                chip.classList.add('hidden');
            }
        }

        function startQueuePollingFallback() {
            if (window._queuePollInterval) clearInterval(window._queuePollInterval);
            window._queuePollInterval = setInterval(() => {
                if (currentPage === 'downloads') {
                    fetchDownloads();
                    fetchQueueStats();
                }
            }, 5000);
        }

