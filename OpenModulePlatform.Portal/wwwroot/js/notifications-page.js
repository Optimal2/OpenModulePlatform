// File: OpenModulePlatform.Portal/wwwroot/js/notifications-page.js
(() => {
    'use strict';

    const PUSH_EVENT_NAME = 'omp:push-event';
    const NOTIFICATION_CHANGED_EVENT_NAME = 'omp:notification-state-changed';
    const NOTIFICATION_PUSH_CATEGORY = 'topbar.notification-state-changed';
    const UPDATE_POLL_MODE = 'poll';
    const UPDATE_PUSH_MODE = 'push';
    const REFRESH_DEBOUNCE_MS = 250;

    let refreshTimer = 0;
    let refreshRunning = false;
    let pendingRefresh = false;

    const getTopbarConfig = () => {
        const topbar = document.querySelector('[data-portal-topbar-root]');
        const mode = (topbar?.getAttribute('data-notification-update-mode') || UPDATE_POLL_MODE)
            .trim()
            .toLowerCase();
        return {
            mode: mode === UPDATE_POLL_MODE || mode === UPDATE_PUSH_MODE ? mode : UPDATE_POLL_MODE
        };
    };

    const replaceLiveContent = (html) => {
        const current = document.querySelector('[data-notifications-live-content]');
        if (!current) {
            return false;
        }

        const template = document.createElement('template');
        template.innerHTML = (html || '').trim();
        const next = template.content.querySelector('[data-notifications-live-content]');
        if (!next) {
            return false;
        }

        current.replaceWith(next);
        return true;
    };

    const refresh = async () => {
        if (refreshRunning) {
            pendingRefresh = true;
            return;
        }

        refreshRunning = true;
        try {
            const response = await fetch(window.location.href, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (!response.ok) {
                return;
            }

            replaceLiveContent(await response.text());
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP notifications page refresh failed.', error);
            }
        } finally {
            refreshRunning = false;
            if (pendingRefresh) {
                pendingRefresh = false;
                scheduleRefresh(REFRESH_DEBOUNCE_MS);
            }
        }
    };

    function scheduleRefresh(delay) {
        if (refreshTimer) {
            window.clearTimeout(refreshTimer);
        }

        refreshTimer = window.setTimeout(() => {
            refreshTimer = 0;
            refresh();
        }, Math.max(0, delay));
    }

    const handlePushEvent = (event) => {
        const detail = event?.detail || {};
        const category = (detail.category || detail.envelope?.category || '').toString().toLowerCase();
        if (category === NOTIFICATION_PUSH_CATEGORY && getTopbarConfig().mode === UPDATE_PUSH_MODE) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    const handleNotificationChanged = () => {
        scheduleRefresh(REFRESH_DEBOUNCE_MS);
    };

    const handleVisibilityOrFocus = () => {
        if (getTopbarConfig().mode === UPDATE_POLL_MODE) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    window.addEventListener(PUSH_EVENT_NAME, handlePushEvent);
    window.addEventListener(NOTIFICATION_CHANGED_EVENT_NAME, handleNotificationChanged);
    window.addEventListener('focus', handleVisibilityOrFocus);
    document.addEventListener('visibilitychange', handleVisibilityOrFocus);
})();
