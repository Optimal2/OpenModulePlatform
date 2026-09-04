// File: OpenModulePlatform.Portal/wwwroot/js/message-thread-page.js
(() => {
    'use strict';

    const UPDATE_MANUAL_MODE = 'manual';
    const UPDATE_POLL_MODE = 'poll';
    const UPDATE_PUSH_MODE = 'push';
    const DEFAULT_POLL_INTERVAL_SECONDS = 60;
    const MIN_POLL_INTERVAL_SECONDS = 10;
    const MAX_POLL_INTERVAL_SECONDS = 3600;
    const MESSAGE_PUSH_CATEGORY = 'topbar.message-state-changed';
    const MESSAGE_CHANGED_EVENT_NAME = 'omp:message-state-changed';
    const PUSH_EVENT_NAME = 'omp:push-event';
    const REFRESH_DEBOUNCE_MS = 350;

    const scrollContainer = document.querySelector('[data-message-thread-scroll]');
    const form = document.querySelector('[data-message-thread-composer]');
    const scrollInput = document.querySelector('[data-message-thread-scroll-input]');
    const status = document.querySelector('[data-message-thread-status]');
    const errorBanner = document.querySelector('.portal-message-thread__error-banner');

    if (!scrollContainer || !form) {
        return;
    }

    const restoreValue = Number.parseInt(scrollContainer.dataset.restoreScrollTop || '', 10);
    if (Number.isFinite(restoreValue) && restoreValue >= 0) {
        scrollContainer.scrollTop = restoreValue;
    } else {
        scrollContainer.scrollTop = scrollContainer.scrollHeight;
    }

    const syncScrollInput = () => {
        if (scrollInput) {
            const distanceFromBottom = scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;
            scrollInput.value = distanceFromBottom <= 24
                ? ''
                : Math.round(scrollContainer.scrollTop).toString();
        }
    };

    form?.addEventListener('submit', syncScrollInput);

    if (scrollContainer.dataset.liveDisabled === 'true') {
        return;
    }

    const conversationId = Number.parseInt(form.dataset.conversationId || '', 10);
    const refreshUrl = form.dataset.refreshUrl || '';
    if (!Number.isFinite(conversationId) || conversationId <= 0 || refreshUrl.length === 0) {
        return;
    }

    let refreshTimer = 0;
    let pollTimer = 0;
    let refreshRunning = false;
    let pendingRefresh = false;

    const parseIntervalSeconds = (value, fallback) => {
        const parsed = Number.parseInt(value || '', 10);
        if (!Number.isFinite(parsed)) {
            return fallback;
        }

        return Math.min(MAX_POLL_INTERVAL_SECONDS, Math.max(MIN_POLL_INTERVAL_SECONDS, parsed));
    };

    const getTopbarConfig = () => {
        const topbar = document.querySelector('[data-portal-topbar-root]');
        const rawMode = (topbar?.getAttribute('data-notification-update-mode') || UPDATE_POLL_MODE)
            .trim()
            .toLowerCase();
        const mode = rawMode === UPDATE_MANUAL_MODE || rawMode === UPDATE_PUSH_MODE
            ? rawMode
            : UPDATE_POLL_MODE;
        const visibleInterval = parseIntervalSeconds(
            topbar?.getAttribute('data-notification-poll-interval'),
            DEFAULT_POLL_INTERVAL_SECONDS) * 1000;
        const hiddenInterval = parseIntervalSeconds(
            topbar?.getAttribute('data-topbar-polling-hidden-interval'),
            visibleInterval / 1000) * 1000;

        return {
            mode,
            interval: document.visibilityState === 'hidden' ? hiddenInterval : visibleInterval
        };
    };

    const getContentRoot = () => scrollContainer.querySelector('[data-message-thread-content]');

    const getLatestMessageId = () => {
        const latest = Number.parseInt(getContentRoot()?.dataset.latestMessageId || '0', 10);
        return Number.isFinite(latest) ? latest : 0;
    };

    const isOwnMessage = (messageId) => {
        const id = String(messageId);
        const message = Array.from(scrollContainer.querySelectorAll('[data-message-id]'))
            .find((item) => item.dataset.messageId === id);
        return message?.classList.contains('is-own') === true;
    };

    const playIncomingMessageSound = (messageId, options) => {
        if (options.suppressSound === true || isOwnMessage(messageId)) {
            return;
        }

        window.ompToastSound?.playMessage(false);
    };

    const isNearBottom = () => {
        const distanceFromBottom = scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;
        return distanceFromBottom <= 80;
    };

    const announceUpdated = () => {
        if (!status) {
            return;
        }

        status.textContent = '';
        window.setTimeout(() => {
            status.textContent = form.dataset.updatedText || 'Conversation updated';
        }, 0);
    };

    const replaceMessages = (html, options = {}) => {
        const currentRoot = getContentRoot();
        if (!currentRoot) {
            return false;
        }

        const template = document.createElement('template');
        template.innerHTML = (html || '').trim();
        const nextRoot = template.content.querySelector('[data-message-thread-content]');
        if (!nextRoot) {
            return false;
        }

        const previousLatestMessageId = getLatestMessageId();
        const shouldStickToBottom = options.stickToBottom === true || isNearBottom();
        const previousScrollTop = scrollContainer.scrollTop;
        currentRoot.replaceWith(nextRoot);

        if (shouldStickToBottom) {
            scrollContainer.scrollTop = scrollContainer.scrollHeight;
        } else {
            scrollContainer.scrollTop = previousScrollTop;
        }

        const latestMessageId = getLatestMessageId();
        if (latestMessageId > previousLatestMessageId) {
            announceUpdated();
            playIncomingMessageSound(latestMessageId, options);
        }

        return true;
    };

    const setComposerErrors = (messages) => {
        if (!errorBanner) {
            return;
        }

        const list = Array.isArray(messages)
            ? messages.filter((message) => typeof message === 'string' && message.trim().length > 0)
            : [];

        errorBanner.textContent = list.join(' ');
        errorBanner.classList.toggle('validation-summary-errors', list.length > 0);
        errorBanner.classList.toggle('validation-summary-valid', list.length === 0);
    };

    const readSubmitErrors = async (response) => {
        const fallback = form.dataset.sendErrorText || 'The message could not be sent.';
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            try {
                const payload = await response.json();
                if (Array.isArray(payload?.errors) && payload.errors.length > 0) {
                    return payload.errors;
                }
            } catch {
                return [fallback];
            }
        }

        return [fallback];
    };

    const setComposerDisabled = (disabled) => {
        form.querySelectorAll('input, button').forEach((control) => {
            control.disabled = disabled;
        });
    };

    const refreshMessages = async () => {
        if (refreshRunning) {
            pendingRefresh = true;
            return;
        }

        refreshRunning = true;
        try {
            const response = await fetch(refreshUrl, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403) {
                return;
            }

            if (!response.ok) {
                throw new Error('Message thread refresh failed with status ' + response.status + '.');
            }

            replaceMessages(await response.text());
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP message thread refresh failed.', error);
            }
        } finally {
            refreshRunning = false;
            if (pendingRefresh) {
                pendingRefresh = false;
                scheduleRefresh(REFRESH_DEBOUNCE_MS);
            }
        }
    };

    const submitMessage = async (event) => {
        event.preventDefault();
        syncScrollInput();

        if (form.dataset.messageThreadSubmitting === 'true') {
            return;
        }

        form.dataset.messageThreadSubmitting = 'true';
        setComposerErrors([]);

        const body = new FormData(form);
        setComposerDisabled(true);

        try {
            const response = await fetch(form.action, {
                method: 'POST',
                body,
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403 || response.redirected) {
                setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
                return;
            }

            if (!response.ok) {
                setComposerErrors(await readSubmitErrors(response));
                return;
            }

            const html = await response.text();
            if (!replaceMessages(html, { stickToBottom: true, suppressSound: true })) {
                setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
                return;
            }

            form.reset();
            if (scrollInput) {
                scrollInput.value = '';
            }
        } catch (error) {
            setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP message send failed.', error);
            }
        } finally {
            form.dataset.messageThreadSubmitting = 'false';
            setComposerDisabled(false);
        }
    };

    form.addEventListener('submit', submitMessage);

    function scheduleRefresh(delay) {
        if (refreshTimer) {
            window.clearTimeout(refreshTimer);
        }

        refreshTimer = window.setTimeout(() => {
            refreshTimer = 0;
            refreshMessages();
        }, Math.max(0, delay));
    }

    const schedulePoll = () => {
        if (pollTimer) {
            window.clearTimeout(pollTimer);
            pollTimer = 0;
        }

        const config = getTopbarConfig();
        if (config.mode !== UPDATE_POLL_MODE) {
            return;
        }

        pollTimer = window.setTimeout(async () => {
            pollTimer = 0;
            await refreshMessages();
            schedulePoll();
        }, config.interval);
    };

    const getPayloadConversationId = (payload) => {
        if (!payload || typeof payload !== 'object') {
            return null;
        }

        const value = payload.conversationId;
        const parsed = typeof value === 'number'
            ? value
            : Number.parseInt(value || '', 10);

        return Number.isFinite(parsed) ? parsed : null;
    };

    const handlePushEvent = (event) => {
        const detail = event?.detail || {};
        const category = (detail.category || detail.envelope?.category || '').toString().toLowerCase();
        if (category !== MESSAGE_PUSH_CATEGORY) {
            return;
        }

        const payload = detail.payload || detail.envelope?.payload || null;
        const payloadAction = (payload?.action || '').toString().toLowerCase();
        const isReadAll = payloadAction === 'read-all';
        if (!isReadAll && getPayloadConversationId(payload) !== conversationId) {
            return;
        }

        const config = getTopbarConfig();
        if (config.mode !== UPDATE_PUSH_MODE) {
            return;
        }

        scheduleRefresh(REFRESH_DEBOUNCE_MS);
    };

    const handleMessageChanged = (event) => {
        const detail = event?.detail || {};
        if (detail.allRead === true) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
            return;
        }

        if (Number.isFinite(Number(detail.unreadCount)) && Number(detail.unreadCount) === 0) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    const handleVisibilityOrFocus = () => {
        const config = getTopbarConfig();
        if (config.mode === UPDATE_POLL_MODE) {
            schedulePoll();
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
            return;
        }

        if (config.mode === UPDATE_PUSH_MODE && document.visibilityState === 'visible' && document.hasFocus()) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    window.addEventListener(PUSH_EVENT_NAME, handlePushEvent);
    window.addEventListener(MESSAGE_CHANGED_EVENT_NAME, handleMessageChanged);
    window.addEventListener('focus', handleVisibilityOrFocus);
    document.addEventListener('visibilitychange', handleVisibilityOrFocus);

    if (getTopbarConfig().mode === UPDATE_POLL_MODE) {
        schedulePoll();
    }
})();
