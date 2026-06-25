(function () {
    "use strict";

    var maxVisibleToasts = 2;
    var maxQueuedToasts = 8;
    var maxBackoffMultiplier = 6;
    var summaryThreshold = 3;
    var minToastDuration = 1;
    var defaultVisibleInterval = 60000;
    var defaultHiddenInterval = 180000;
    var defaultToastDuration = 7000;
    var dismissTransitionDuration = 180;
    var pushEventName = "omp:push-event";

    var state = {
        root: null,
        container: null,
        baselineNotificationId: null,
        baselineMessageId: null,
        timer: null,
        running: false,
        failures: 0,
        visible: [],
        queue: []
    };

    function parsePositiveInteger(value, fallback) {
        var parsed = parseInt(value || "", 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function formatText(template, value) {
        return (template || "").replace("{0}", String(value));
    }

    function getConfig() {
        var root = state.root;
        return {
            summaryUrl: root.getAttribute("data-summary-url") || "/notifications/summary",
            notificationsUrl: root.getAttribute("data-notifications-url") || "/notifications",
            messagesUrl: root.getAttribute("data-messages-url") || "/messages",
            visibleInterval: parsePositiveInteger(root.getAttribute("data-visible-interval"), defaultVisibleInterval),
            hiddenInterval: parsePositiveInteger(root.getAttribute("data-hidden-interval"), defaultHiddenInterval),
            toastDuration: parsePositiveInteger(root.getAttribute("data-toast-duration"), defaultToastDuration),
            closeLabel: root.getAttribute("data-close-label") || "Close",
            summaryTitle: root.getAttribute("data-summary-title") || "New notifications",
            summaryTemplate: root.getAttribute("data-summary-template") || "{0} new notifications",
            messageSummaryTitle: root.getAttribute("data-message-summary-title") || "New messages",
            messageSummaryTemplate: root.getAttribute("data-message-summary-template") || "{0} new messages"
        };
    }

    function getPollingDelay(config) {
        var baseDelay = document.visibilityState === "hidden"
            ? config.hiddenInterval
            : config.visibleInterval;

        if (state.failures > 0) {
            return baseDelay * Math.min(maxBackoffMultiplier, state.failures + 1);
        }

        return baseDelay;
    }

    function scheduleNext(delay) {
        if (state.timer) {
            window.clearTimeout(state.timer);
        }

        if (!state.root) {
            return;
        }

        state.timer = window.setTimeout(runPoll, Math.max(0, delay));
    }

    function buildSummaryUrl(config) {
        var url = new URL(config.summaryUrl, window.location.origin);
        if (state.baselineNotificationId !== null) {
            url.searchParams.set("afterNotificationId", String(state.baselineNotificationId));
        }

        if (state.baselineMessageId !== null) {
            url.searchParams.set("afterMessageId", String(state.baselineMessageId));
        }

        return url.toString();
    }

    async function fetchSummary(config) {
        var response = await fetch(buildSummaryUrl(config), {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store",
            headers: {
                "Accept": "application/json",
                "X-Requested-With": "XMLHttpRequest"
            }
        });

        if (response.status === 401 || response.status === 403) {
            throw new Error("Notification toast polling requires sign-in.");
        }

        if (!response.ok) {
            throw new Error("Notification toast polling failed with status " + response.status + ".");
        }

        var contentType = response.headers.get("content-type") || "";
        if (contentType.indexOf("application/json") < 0) {
            throw new Error("Notification toast polling returned a non-JSON response.");
        }

        return await response.json();
    }

    async function runPoll() {
        if (!state.root || state.running) {
            return;
        }

        var config = getConfig();
        state.running = true;

        try {
            var payload = await fetchSummary(config);
            state.failures = 0;
            applySummary(payload, config);
        } catch (error) {
            state.failures += 1;
            if ((state.failures === 1 || state.failures % maxBackoffMultiplier === 0)
                && window.console
                && typeof window.console.warn === "function") {
                window.console.warn("Notification toast polling failed.", error);
            }
        } finally {
            state.running = false;
            scheduleNext(getPollingDelay(config));
        }
    }

    function applySummary(payload, config) {
        if (!payload) {
            return;
        }

        var latestNotificationId = Number(payload.latestNotificationId || 0);
        var latestMessageId = Number(payload.latestMessageId || 0);
        var hasNotificationBaseline = state.baselineNotificationId !== null;
        var hasMessageBaseline = state.baselineMessageId !== null;

        if (!hasNotificationBaseline) {
            state.baselineNotificationId = latestNotificationId;
        }

        if (!hasMessageBaseline) {
            state.baselineMessageId = latestMessageId;
        }

        if (!hasNotificationBaseline && !hasMessageBaseline) {
            return;
        }

        var items = Array.isArray(payload.newNotifications) ? payload.newNotifications : [];
        var newNotificationCount = Number(payload.newNotificationCount || items.length || 0);

        if (hasNotificationBaseline) {
            if (newNotificationCount >= summaryThreshold) {
                enqueueToast({
                    title: config.summaryTitle,
                    content: formatText(config.summaryTemplate, newNotificationCount),
                    targetUrl: config.notificationsUrl,
                    isSummary: true
                }, config);
            } else if (items.length > 0) {
                items.slice().reverse().forEach(function (item) {
                    enqueueToast({
                        title: item.title || config.summaryTitle,
                        content: item.content || "",
                        targetUrl: item.targetUrl || config.notificationsUrl,
                        isSummary: false
                    }, config);
                });
            }
        }

        if (latestNotificationId > state.baselineNotificationId) {
            state.baselineNotificationId = latestNotificationId;
        }

        var messageItems = Array.isArray(payload.newMessages) ? payload.newMessages : [];
        var newMessageCount = Number(payload.newMessageCount || messageItems.length || 0);

        if (hasMessageBaseline) {
            if (newMessageCount >= summaryThreshold) {
                enqueueToast({
                    title: config.messageSummaryTitle,
                    content: formatText(config.messageSummaryTemplate, newMessageCount),
                    targetUrl: config.messagesUrl,
                    isSummary: true,
                    isMessage: true
                }, config);
            } else if (messageItems.length > 0) {
                messageItems.slice().reverse().forEach(function (item) {
                    enqueueToast({
                        title: item.title || config.messageSummaryTitle,
                        content: item.content || "",
                        targetUrl: item.targetUrl || config.messagesUrl,
                        isSummary: false,
                        isMessage: true
                    }, config);
                });
            }
        }

        if (latestMessageId > state.baselineMessageId) {
            state.baselineMessageId = latestMessageId;
        }
    }

    function enqueueToast(toast, config) {
        if (state.visible.length < maxVisibleToasts) {
            showToast(toast, config);
            return;
        }

        state.queue.unshift(toast);
        if (state.queue.length > maxQueuedToasts) {
            state.queue.length = maxQueuedToasts;
        }
    }

    function drainQueue(config) {
        while (state.visible.length < maxVisibleToasts && state.queue.length > 0) {
            showToast(state.queue.shift(), config);
        }
    }

    function isToastPushCategory(category) {
        if (!category) {
            return true;
        }

        var normalized = String(category).toLowerCase();
        return normalized === "notification"
            || normalized === "message"
            || normalized === "topbar.notification-state-changed"
            || normalized === "topbar.message-state-changed"
            || normalized.indexOf("topbar.notification-") === 0
            || normalized.indexOf("topbar.message-") === 0;
    }

    function handlePushEvent(event) {
        var detail = event && event.detail ? event.detail : {};
        var category = detail.category || (detail.envelope && detail.envelope.category) || "";
        if (isToastPushCategory(category)) {
            scheduleNext(0);
        }
    }

    function showToast(toast, config) {
        var element = document.createElement("article");
        element.className = "portal-notification-toast"
            + (toast.isSummary ? " portal-notification-toast--summary" : "")
            + (toast.isMessage ? " portal-notification-toast--message" : "");
        element.tabIndex = 0;
        element.setAttribute("role", "status");
        element.setAttribute("aria-live", "polite");
        element.style.setProperty("--portal-notification-toast-duration", config.toastDuration + "ms");
        element.style.setProperty("--portal-notification-toast-progress", "1");
        element.style.setProperty("--portal-notification-toast-transition-duration", "0ms");

        var close = document.createElement("button");
        close.type = "button";
        close.className = "portal-notification-toast__close";
        close.setAttribute("aria-label", config.closeLabel);
        close.textContent = "\u00d7";

        var title = document.createElement("div");
        title.className = "portal-notification-toast__title";
        title.textContent = toast.title;

        var timer = document.createElement("div");
        timer.className = "portal-notification-toast__timer";
        timer.setAttribute("aria-hidden", "true");

        var timerFill = document.createElement("span");
        timerFill.className = "portal-notification-toast__timer-fill";
        timer.append(timerFill);

        var content = document.createElement("div");
        content.className = "portal-notification-toast__content";
        content.textContent = toast.content;

        element.append(close, title, timer, content);

        var visibleState = {
            element: element,
            duration: config.toastDuration,
            remaining: config.toastDuration,
            startedAt: 0,
            timeout: 0,
            progressFrame: 0,
            paused: false
        };

        close.addEventListener("click", function (event) {
            event.preventDefault();
            event.stopPropagation();
            dismissToast(visibleState, config);
        });

        element.addEventListener("click", function () {
            window.location.href = toast.targetUrl || config.notificationsUrl;
        });

        element.addEventListener("keydown", function (event) {
            if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                window.location.href = toast.targetUrl || config.notificationsUrl;
            }
        });

        element.addEventListener("mouseenter", function () {
            pauseToast(visibleState);
        });

        element.addEventListener("mouseleave", function () {
            resumeToast(visibleState, config);
        });

        element.addEventListener("focusin", function () {
            pauseToast(visibleState);
        });

        element.addEventListener("focusout", function () {
            resumeToast(visibleState, config);
        });

        state.visible.push(visibleState);
        state.container.insertBefore(element, state.container.firstChild);
        window.requestAnimationFrame(function () {
            element.classList.add("is-visible");
            resumeToast(visibleState, config);
        });
    }

    function setToastTimerProgress(visibleState, progress, transitionDuration) {
        var normalizedProgress = Math.max(0, Math.min(1, progress));
        visibleState.element.style.setProperty("--portal-notification-toast-transition-duration", transitionDuration + "ms");
        visibleState.element.style.setProperty("--portal-notification-toast-progress", String(normalizedProgress));
    }

    function getToastTimerProgress(visibleState) {
        var duration = Math.max(minToastDuration, visibleState.duration || defaultToastDuration);
        return visibleState.remaining / duration;
    }

    function cancelToastProgressAnimation(visibleState) {
        if (visibleState.progressFrame) {
            window.cancelAnimationFrame(visibleState.progressFrame);
            visibleState.progressFrame = 0;
        }
    }

    function startToastProgressAnimation(visibleState) {
        cancelToastProgressAnimation(visibleState);

        function tick() {
            if (!visibleState.element.isConnected || visibleState.paused || visibleState.startedAt <= 0) {
                visibleState.progressFrame = 0;
                return;
            }

            var duration = Math.max(minToastDuration, visibleState.duration || defaultToastDuration);
            var elapsed = performance.now() - visibleState.startedAt;
            var currentRemaining = Math.max(0, visibleState.remaining - elapsed);

            setToastTimerProgress(visibleState, currentRemaining / duration, 0);

            if (currentRemaining > 0) {
                visibleState.progressFrame = window.requestAnimationFrame(tick);
            } else {
                visibleState.progressFrame = 0;
            }
        }

        visibleState.progressFrame = window.requestAnimationFrame(tick);
    }

    function pauseToast(visibleState) {
        if (visibleState.paused) {
            return;
        }

        visibleState.paused = true;
        visibleState.element.classList.add("is-paused");
        cancelToastProgressAnimation(visibleState);
        if (visibleState.timeout) {
            window.clearTimeout(visibleState.timeout);
            visibleState.timeout = 0;
        }

        if (visibleState.startedAt > 0) {
            visibleState.remaining = Math.max(0, visibleState.remaining - (performance.now() - visibleState.startedAt));
            visibleState.startedAt = 0;
        }

        setToastTimerProgress(visibleState, getToastTimerProgress(visibleState), 0);
    }

    function resumeToast(visibleState, config) {
        if (!visibleState.element.isConnected || visibleState.remaining <= 0) {
            return;
        }

        visibleState.paused = false;
        visibleState.element.classList.remove("is-paused");
        visibleState.startedAt = performance.now();
        visibleState.timeout = window.setTimeout(function () {
            dismissToast(visibleState, config);
        }, visibleState.remaining);

        setToastTimerProgress(visibleState, getToastTimerProgress(visibleState), 0);
        startToastProgressAnimation(visibleState);
    }

    function dismissToast(visibleState, config) {
        if (visibleState.timeout) {
            window.clearTimeout(visibleState.timeout);
        }

        cancelToastProgressAnimation(visibleState);
        state.visible = state.visible.filter(function (item) {
            return item !== visibleState;
        });

        visibleState.element.classList.remove("is-visible");
        window.setTimeout(function () {
            visibleState.element.remove();
            drainQueue(config);
        }, dismissTransitionDuration);
    }

    function init() {
        state.root = document.querySelector("[data-portal-notification-toasts]");
        if (!state.root || state.root.getAttribute("data-enabled") !== "true") {
            return;
        }

        state.container = document.createElement("div");
        state.container.className = "portal-notification-toast-stack";
        document.body.appendChild(state.container);

        window.addEventListener("focus", function () {
            scheduleNext(0);
        });

        document.addEventListener("visibilitychange", function () {
            if (document.visibilityState === "visible") {
                scheduleNext(0);
            }
        });

        window.addEventListener(pushEventName, handlePushEvent);

        scheduleNext(0);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
}());
