// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-live-refresh.js
// Shared live-refresh helper for module pages.
//
// One subscription = "tell me when my module's server state changed, and keep
// me fresh even when push is unavailable". The helper owns every transport
// concern so pages only implement how to apply an update:
//
//   var handle = window.ompLiveRefresh.subscribe({
//       module: 'ibs_packager',            // payload.module filter (required)
//       onRefresh: function (info) { ... } // info.source: 'push' | 'fallback' | 'manual'
//       // Optional:
//       // categories: ['module.state-changed'],
//       // debounceMs: 350,
//       // fallback: { intervalMs: 60000, hiddenIntervalMs: 0 }, // 0 = pause when hidden
//       // requestPush: false,  // open a page-owned connection when the top
//       //                      // bar does not provide one (portal mode poll/manual)
//       // onStateChange: function (state) { ... } // { live: bool, transport: 'topbar'|'own'|'none' }
//   });
//   handle.unsubscribe(); handle.isLive(); handle.refreshNow();
//
// Transport rules:
//   * The portal top bar owns the primary SignalR connection in push mode and
//     re-broadcasts every envelope as the 'omp:push-event' DOM event. The
//     helper consumes that bus and never opens a second socket while the top
//     bar channel is connected (state via 'omp:push-channel-state' and
//     window.ompPushChannel).
//   * When a subscriber sets requestPush and no top-bar channel is connected,
//     the helper opens ONE shared page connection (with capped exponential
//     backoff) and re-broadcasts its envelopes on the same DOM bus so legacy
//     listeners benefit too. It closes again when the top bar takes over.
//   * Fallback polling per subscription runs only while no live transport is
//     up, is visibility-aware, and stops the moment push comes back.
(() => {
    'use strict';

    if (window.ompLiveRefresh) {
        return;
    }

    const PUSH_EVENT = 'omp:push-event';
    const CHANNEL_STATE_EVENT = 'omp:push-channel-state';
    const DEFAULT_CATEGORY = 'module.state-changed';
    const DEFAULT_DEBOUNCE_MS = 350;
    const DEFAULT_FALLBACK_MS = 60000;
    const DEDUP_RETENTION_MS = 5 * 60 * 1000;
    const MAX_DEDUP_KEYS = 200;
    const OWN_RECONNECT_BASE_MS = 2000;
    const OWN_RECONNECT_MAX_MS = 60000;
    const MAX_OWN_RECONNECT_ATTEMPTS = 10;
    const SIGNALR_SCRIPT_RELATIVE_URL = '/_content/OpenModulePlatform.Web.Shared/js/signalr.min.js';

    const subscriptions = new Set();
    const seenEnvelopeKeys = new Map();
    let ownConnection = null;
    let ownConnected = false;
    let ownStarting = false;
    let ownReconnectAttempts = 0;
    let ownReconnectTimer = 0;
    let signalRClientPromise = null;

    function getTopbarRoot() {
        return document.querySelector('[data-portal-topbar-root]');
    }

    function getTopbarMode() {
        const root = getTopbarRoot();
        return root
            ? String(root.getAttribute('data-notification-update-mode') || '').trim().toLowerCase()
            : '';
    }

    function isTopbarChannelConnected() {
        return !!(window.ompPushChannel && window.ompPushChannel.connected);
    }

    function currentTransport() {
        if (isTopbarChannelConnected()) {
            return 'topbar';
        }

        return ownConnected ? 'own' : 'none';
    }

    function isLive() {
        return currentTransport() !== 'none';
    }

    // --- envelope handling --------------------------------------------------

    function getEnvelopeKey(envelope) {
        if (!envelope || typeof envelope !== 'object') {
            return '';
        }

        const eventId = envelope.eventId || envelope.id;
        if (eventId !== undefined && eventId !== null && eventId !== '') {
            return 'event:' + String(eventId);
        }

        const deduplicationKey = envelope.deduplicationKey || envelope.dedupKey;
        return deduplicationKey ? 'key:' + String(deduplicationKey) : '';
    }

    // Duplicate suppression across transports: the top bar dedups its own
    // stream, but when the page connection hands over to (or overlaps with)
    // the top bar the same envelope can arrive twice.
    function rememberEnvelope(envelope) {
        const key = getEnvelopeKey(envelope);
        if (!key) {
            return false;
        }

        const now = Date.now();
        seenEnvelopeKeys.forEach((seenAt, seenKey) => {
            if (now - seenAt > DEDUP_RETENTION_MS) {
                seenEnvelopeKeys.delete(seenKey);
            }
        });

        if (seenEnvelopeKeys.has(key)) {
            return true;
        }

        seenEnvelopeKeys.set(key, now);
        if (seenEnvelopeKeys.size > MAX_DEDUP_KEYS) {
            seenEnvelopeKeys.delete(seenEnvelopeKeys.keys().next().value);
        }

        return false;
    }

    function subscriptionMatches(subscription, category, payload) {
        if (!subscription.categories.has(String(category || '').toLowerCase())) {
            return false;
        }

        if (!subscription.module) {
            return true;
        }

        const payloadModule = payload && typeof payload === 'object' ? payload.module : null;
        return String(payloadModule || '').toLowerCase() === subscription.module;
    }

    function scheduleSubscriptionRefresh(subscription, detail) {
        window.clearTimeout(subscription.debounceTimer);
        subscription.debounceTimer = window.setTimeout(() => {
            subscription.debounceTimer = 0;
            invokeRefresh(subscription, { source: 'push', detail: detail || null });
        }, subscription.debounceMs);
    }

    function invokeRefresh(subscription, info) {
        try {
            subscription.onRefresh(info);
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('ompLiveRefresh subscriber onRefresh failed.', error);
            }
        }
    }

    function handleBusEvent(event) {
        const detail = event && event.detail ? event.detail : {};
        subscriptions.forEach((subscription) => {
            if (subscriptionMatches(subscription, detail.category, detail.payload)) {
                scheduleSubscriptionRefresh(subscription, detail);
            }
        });
    }

    function dispatchEnvelopeOnBus(envelope) {
        if (rememberEnvelope(envelope) || typeof window.CustomEvent !== 'function') {
            return;
        }

        window.dispatchEvent(new CustomEvent(PUSH_EVENT, {
            detail: {
                envelope: envelope || null,
                category: envelope && envelope.category ? String(envelope.category) : '',
                payload: envelope && envelope.payload ? envelope.payload : null
            }
        }));
    }

    // --- fallback polling ---------------------------------------------------

    function getFallbackDelay(subscription) {
        if (!subscription.fallback) {
            return 0;
        }

        if (document.visibilityState === 'hidden') {
            return subscription.fallback.hiddenIntervalMs;
        }

        return subscription.fallback.intervalMs;
    }

    function scheduleFallback(subscription) {
        window.clearTimeout(subscription.fallbackTimer);
        subscription.fallbackTimer = 0;

        if (!subscription.fallback || isLive()) {
            return;
        }

        const delay = getFallbackDelay(subscription);
        if (!(delay > 0)) {
            return;
        }

        subscription.fallbackTimer = window.setTimeout(() => {
            subscription.fallbackTimer = 0;
            if (!isLive()) {
                // The fallback must never silently rescue a broken push path:
                // log once per outage episode so a delivery failure is visible
                // instead of being masked for years by quiet polling.
                if (!subscription.fallbackWarned) {
                    subscription.fallbackWarned = true;
                    if (window.console && typeof window.console.warn === 'function') {
                        window.console.warn(
                            'ompLiveRefresh: no live push transport; refreshing module "' +
                            (subscription.module || '*') +
                            '" via fallback polling. If push delivery was expected, check the push channel.');
                    }
                }
                invokeRefresh(subscription, { source: 'fallback', detail: null });
            }
            scheduleFallback(subscription);
        }, delay);
    }

    // --- state propagation --------------------------------------------------

    function notifyState(subscription) {
        const state = { live: isLive(), transport: currentTransport() };
        if (state.live) {
            // Live transport is back: allow the next outage episode to log again.
            subscription.fallbackWarned = false;
        }

        if (subscription.lastLive === state.live && subscription.lastTransport === state.transport) {
            return;
        }

        subscription.lastLive = state.live;
        subscription.lastTransport = state.transport;
        if (typeof subscription.onStateChange === 'function') {
            try {
                subscription.onStateChange(state);
            } catch (error) {
                if (window.console && typeof window.console.warn === 'function') {
                    window.console.warn('ompLiveRefresh subscriber onStateChange failed.', error);
                }
            }
        }
    }

    function recomputeState() {
        subscriptions.forEach((subscription) => {
            notifyState(subscription);
            scheduleFallback(subscription);
        });
        syncOwnConnection();
    }

    // --- page-owned connection ----------------------------------------------

    function anySubscriberRequestsPush() {
        for (const subscription of subscriptions) {
            if (subscription.requestPush) {
                return true;
            }
        }

        return false;
    }

    function resolveSharedAssetUrl(value) {
        if (!value || value.indexOf('/_content/OpenModulePlatform.Web.Shared/') !== 0) {
            return value || '';
        }

        const stylesheet = document.querySelector('link[href*="_content/OpenModulePlatform.Web.Shared/css/portal-topbar.css"]');
        if (!stylesheet) {
            return value;
        }

        const absoluteHref = new URL(stylesheet.getAttribute('href') || '', document.baseURI).href;
        const marker = '_content/OpenModulePlatform.Web.Shared/css/portal-topbar.css';
        const markerIndex = absoluteHref.indexOf(marker);
        return markerIndex < 0 ? value : absoluteHref.substring(0, markerIndex) + value.substring(1);
    }

    function getPushEventUrl() {
        const root = getTopbarRoot();
        const topbarPushUrl = root ? root.getAttribute('data-notification-push-url') : '';
        if (topbarPushUrl) {
            const marker = '/topbar/notifications/updates';
            const markerIndex = topbarPushUrl.toLowerCase().lastIndexOf(marker);
            return markerIndex >= 0
                ? topbarPushUrl.substring(0, markerIndex) + '/push/events'
                : topbarPushUrl;
        }

        return '/push/events';
    }

    function loadSignalRClient() {
        if (window.signalR) {
            return Promise.resolve(window.signalR);
        }

        if (!signalRClientPromise) {
            signalRClientPromise = new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = resolveSharedAssetUrl(SIGNALR_SCRIPT_RELATIVE_URL);
                script.async = true;
                script.onload = () => {
                    window.signalR ? resolve(window.signalR) : reject(new Error('SignalR client script loaded without exposing window.signalR.'));
                };
                script.onerror = () => {
                    signalRClientPromise = null;
                    reject(new Error('Could not load the SignalR client script.'));
                };
                document.head.appendChild(script);
            });
        }

        return signalRClientPromise;
    }

    function ownReconnectDelayMs() {
        if (ownReconnectAttempts >= MAX_OWN_RECONNECT_ATTEMPTS) {
            return null;
        }

        return Math.min(OWN_RECONNECT_MAX_MS, OWN_RECONNECT_BASE_MS * Math.pow(2, ownReconnectAttempts));
    }

    function scheduleOwnReconnect() {
        if (ownReconnectTimer) {
            return;
        }

        const delay = ownReconnectDelayMs();
        if (delay === null) {
            return;
        }

        ownReconnectAttempts += 1;
        ownReconnectTimer = window.setTimeout(() => {
            ownReconnectTimer = 0;
            syncOwnConnection();
        }, delay);
    }

    function stopOwnConnection() {
        if (ownReconnectTimer) {
            window.clearTimeout(ownReconnectTimer);
            ownReconnectTimer = 0;
        }

        const connection = ownConnection;
        ownConnection = null;
        ownConnected = false;
        ownStarting = false;
        ownReconnectAttempts = 0;
        if (connection && typeof connection.stop === 'function') {
            connection.stop().catch(() => {
            });
        }
    }

    async function startOwnConnection() {
        if (ownStarting || ownConnection) {
            return;
        }

        ownStarting = true;
        try {
            const signalR = await loadSignalRClient();
            if (!anySubscriberRequestsPush() || isTopbarChannelConnected()) {
                return;
            }

            const builder = new signalR.HubConnectionBuilder()
                .withUrl(getPushEventUrl())
                .withAutomaticReconnect();
            if (signalR.LogLevel && typeof builder.configureLogging === 'function') {
                builder.configureLogging(signalR.LogLevel.Warning);
            }

            const connection = builder.build();
            const onEnvelope = (envelope) => dispatchEnvelopeOnBus(envelope);
            connection.on('pushEvent', onEnvelope);
            connection.on('notificationStateChanged', onEnvelope);
            connection.onreconnecting(() => {
                ownConnected = false;
                recomputeState();
            });
            connection.onreconnected(() => {
                ownConnected = true;
                ownReconnectAttempts = 0;
                recomputeState();
            });
            connection.onclose(() => {
                if (ownConnection === connection) {
                    ownConnection = null;
                }

                ownConnected = false;
                recomputeState();
                scheduleOwnReconnect();
            });

            ownConnection = connection;
            await connection.start();
            ownConnected = true;
            ownReconnectAttempts = 0;
        } catch (error) {
            ownConnection = null;
            ownConnected = false;
            scheduleOwnReconnect();
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('ompLiveRefresh page push connection failed; fallback polling remains active.', error);
            }
        } finally {
            ownStarting = false;
            recomputeStateWithoutConnectionSync();
        }
    }

    // recomputeState() calls syncOwnConnection(); the connection callbacks must
    // not recurse back into another connect attempt mid-flight.
    let recomputing = false;
    function recomputeStateWithoutConnectionSync() {
        subscriptions.forEach((subscription) => {
            notifyState(subscription);
            scheduleFallback(subscription);
        });
    }

    function syncOwnConnection() {
        if (recomputing) {
            return;
        }

        recomputing = true;
        try {
            const needOwn = anySubscriberRequestsPush() && !isTopbarChannelConnected();
            if (!needOwn && ownConnection) {
                stopOwnConnection();
                recomputeStateWithoutConnectionSync();
            } else if (needOwn && !ownConnection && !ownStarting) {
                startOwnConnection();
            }
        } finally {
            recomputing = false;
        }
    }

    // --- public API ---------------------------------------------------------

    function subscribe(options) {
        if (!options || typeof options.onRefresh !== 'function') {
            throw new Error('ompLiveRefresh.subscribe requires an onRefresh callback.');
        }

        const categories = (Array.isArray(options.categories) && options.categories.length > 0
            ? options.categories
            : [DEFAULT_CATEGORY])
            .map((category) => String(category || '').toLowerCase());

        const fallbackOptions = options.fallback && typeof options.fallback === 'object'
            ? {
                intervalMs: options.fallback.intervalMs > 0 ? options.fallback.intervalMs : DEFAULT_FALLBACK_MS,
                hiddenIntervalMs: options.fallback.hiddenIntervalMs > 0 ? options.fallback.hiddenIntervalMs : 0
            }
            : null;

        const subscription = {
            module: String(options.module || '').toLowerCase(),
            categories: new Set(categories),
            onRefresh: options.onRefresh,
            onStateChange: typeof options.onStateChange === 'function' ? options.onStateChange : null,
            debounceMs: options.debounceMs > 0 ? options.debounceMs : DEFAULT_DEBOUNCE_MS,
            fallback: fallbackOptions,
            requestPush: options.requestPush === true,
            debounceTimer: 0,
            fallbackTimer: 0,
            fallbackWarned: false,
            lastLive: null,
            lastTransport: null
        };

        subscriptions.add(subscription);
        notifyState(subscription);
        scheduleFallback(subscription);
        syncOwnConnection();

        return {
            unsubscribe: () => {
                window.clearTimeout(subscription.debounceTimer);
                window.clearTimeout(subscription.fallbackTimer);
                subscriptions.delete(subscription);
                syncOwnConnection();
            },
            isLive: () => isLive(),
            refreshNow: () => invokeRefresh(subscription, { source: 'manual', detail: null })
        };
    }

    window.addEventListener(PUSH_EVENT, handleBusEvent);
    window.addEventListener(CHANNEL_STATE_EVENT, () => {
        recomputeState();
    });
    document.addEventListener('visibilitychange', () => {
        subscriptions.forEach((subscription) => {
            scheduleFallback(subscription);
        });
    });

    window.ompLiveRefresh = {
        subscribe: subscribe,
        isLive: isLive
    };
})();
