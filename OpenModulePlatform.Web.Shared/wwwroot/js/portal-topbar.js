(function () {
    // This shared asset is intentionally authored for the modern browser baseline
    // required by OMP's fetch/ResizeObserver-based top bar. It is served directly,
    // not transpiled, so async/await usage is part of that supported baseline.
    // Keep a single set of global listeners for all shared top bars on the page.
    // Rebalance work is coalesced per animation frame so that multiple resize notifications
    // do not trigger repeated layout thrashing for the same visual update.
    var COMPACT_BREAKPOINT_VARIABLE = '--portal-topbar-compact-breakpoint';
    var FALLBACK_COMPACT_BREAKPOINT = '710px';
    var MAX_NOTIFICATION_BADGE_COUNT = 99;
    var MAX_BACKOFF_MULTIPLIER = 6;
    var DEFAULT_TOPBAR_POLL_INTERVAL_SECONDS = 60;
    var MIN_TOPBAR_POLL_INTERVAL_SECONDS = 10;
    var MAX_TOPBAR_POLL_INTERVAL_SECONDS = 3600;
    var MAX_TOPBAR_PUSH_DEDUP_KEYS = 200;
    var TOPBAR_PUSH_DEDUP_RETENTION_MS = 5 * 60 * 1000;
    var TOPBAR_UPDATE_MANUAL_MODE = 'manual';
    var TOPBAR_UPDATE_POLL_MODE = 'poll';
    var TOPBAR_UPDATE_PUSH_MODE = 'push';
    var TOPBAR_NOTIFICATION_PUSH_METHOD = 'notificationStateChanged';
    var GENERIC_PUSH_EVENT_METHOD = 'pushEvent';
    var SIGNALR_CLIENT_SCRIPT_URL = '/_content/OpenModulePlatform.Web.Shared/js/signalr.min.js';
    var initializedTopbars = new Set();
    var scheduledTopbars = new Set();
    var globalHandlersRegistered = false;
    var rebalanceFrameRequested = false;
    var FAVORITE_CHANGED_EVENT = 'omp:navigation-favorite-changed';
    var NOTIFICATION_CHANGED_EVENT = 'omp:notification-state-changed';
    var PUSH_EVENT_RECEIVED_EVENT = 'omp:push-event';
    var SESSION_STATUS_WARNING_EVENT = 'omp:session-status-warning';
    var sessionStatusState = {
        root: null,
        timer: 0,
        running: false,
        failures: 0,
        currentKind: '',
        handlersRegistered: false
    };
    var topbarPollingState = {
        root: null,
        timer: 0,
        running: false,
        failures: 0,
        handlersRegistered: false,
        mode: TOPBAR_UPDATE_MANUAL_MODE,
        pushUrl: '',
        pushConnection: null,
        pushStarting: false,
        pushFallbackActive: false,
        pushFallbackWarned: false,
        pushReconnectTimer: 0,
        signalRClientPromise: null,
        recentPushEventIds: new Map()
    };
    var canHoverMedia = typeof window.matchMedia === 'function'
        ? window.matchMedia('(hover: hover) and (pointer: fine)')
        : null;
    var resizeObserver = typeof ResizeObserver !== 'undefined'
        ? new ResizeObserver(function (entries) {
            entries.forEach(function (entry) {
                scheduleRebalance(entry.target);
            });
        })
        : null;

    function canHover() {
        return !!(canHoverMedia && canHoverMedia.matches);
    }

    function openOnHoverEnabled(element) {
        var root = element ? element.closest('[data-portal-topbar-root]') : null;
        return !root || root.getAttribute('data-open-on-hover') !== 'false';
    }

    function canOpenOnHover(element) {
        return canHover() && openOnHoverEnabled(element);
    }

    function sortByIndex(nodes) {
        return Array.from(nodes).sort(function (left, right) {
            return Number(left.dataset.index) - Number(right.dataset.index);
        });
    }

    function getCompactBreakpoint(topbar) {
        if (!topbar || typeof window.getComputedStyle !== 'function') {
            return FALLBACK_COMPACT_BREAKPOINT;
        }

        var configuredBreakpoint = window
            .getComputedStyle(topbar)
            .getPropertyValue(COMPACT_BREAKPOINT_VARIABLE)
            .trim();

        return configuredBreakpoint || FALLBACK_COMPACT_BREAKPOINT;
    }

    function isCompactViewport(topbar) {
        return window.matchMedia('(max-width: ' + getCompactBreakpoint(topbar) + ')').matches;
    }

    function cleanupDisconnectedTopbars() {
        initializedTopbars.forEach(function (topbar) {
            if (topbar && topbar.isConnected) {
                return;
            }

            initializedTopbars.delete(topbar);
            scheduledTopbars.delete(topbar);

            if (resizeObserver && topbar) {
                resizeObserver.unobserve(topbar);
            }
        });
    }

    function rebalance(topbar) {
        if (!topbar) {
            return;
        }

        var modulesHost = topbar.querySelector('[data-portal-topbar-modules]');
        var overflow = topbar.querySelector('[data-portal-topbar-overflow]');
        var menu = topbar.querySelector('[data-portal-topbar-menu]');
        var summary = overflow ? overflow.querySelector('summary') : null;

        if (!modulesHost || !overflow || !menu || !summary) {
            return;
        }

        var allLinks = sortByIndex(topbar.querySelectorAll('[data-portal-topbar-item]'));

        allLinks.forEach(function (link) {
            modulesHost.appendChild(link);
        });

        overflow.open = false;
        overflow.hidden = true;
        overflow.classList.remove('portal-topbar__overflow--measuring');
        menu.replaceChildren();

        if (isCompactViewport(topbar)) {
            return;
        }

        if (topbar.scrollWidth <= topbar.clientWidth) {
            return;
        }

        overflow.hidden = false;
        overflow.classList.add('portal-topbar__overflow--measuring');

        while (modulesHost.children.length > 0 && topbar.scrollWidth > topbar.clientWidth) {
            var linkToMove = modulesHost.lastElementChild;
            if (!linkToMove) {
                break;
            }

            menu.prepend(linkToMove);
        }

        overflow.classList.remove('portal-topbar__overflow--measuring');

        if (menu.children.length === 0) {
            overflow.hidden = true;
        }
    }

    function flushScheduledRebalances() {
        rebalanceFrameRequested = false;
        cleanupDisconnectedTopbars();

        var topbars = Array.from(scheduledTopbars);
        scheduledTopbars.clear();
        topbars.forEach(rebalance);
    }

    function requestRebalanceFrame() {
        if (rebalanceFrameRequested) {
            return;
        }

        rebalanceFrameRequested = true;

        if (typeof window.requestAnimationFrame === 'function') {
            window.requestAnimationFrame(flushScheduledRebalances);
            return;
        }

        window.setTimeout(flushScheduledRebalances, 0);
    }

    function scheduleRebalance(topbar) {
        if (!topbar) {
            return;
        }

        scheduledTopbars.add(topbar);
        requestRebalanceFrame();
    }

    function rebalanceAll() {
        cleanupDisconnectedTopbars();
        initializedTopbars.forEach(scheduleRebalance);
    }

    function setEntryGroupExpanded(group, expanded, temporary) {
        if (!group) {
            return;
        }

        var entries = group.querySelector('[data-portal-topbar-entry-group-entries]');
        var toggle = group.querySelector('[data-portal-topbar-entry-group-toggle]');
        var root = group.closest('[data-portal-topbar-root]');
        var expandText = root ? (root.getAttribute('data-expand-text') || 'Expand') : 'Expand';
        var collapseText = root ? (root.getAttribute('data-collapse-text') || 'Collapse') : 'Collapse';

        group.classList.toggle('is-open', !!expanded);
        group.dataset.temporaryExpanded = temporary ? 'true' : 'false';

        if (entries) {
            entries.hidden = !expanded;
        }

        if (toggle) {
            toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
            toggle.setAttribute('title', expanded ? collapseText : expandText);
            toggle.setAttribute('aria-label', expanded ? collapseText : expandText);
            var icon = toggle.querySelector('span[aria-hidden="true"]');
            if (icon) {
                icon.textContent = expanded ? '▾' : '▸';
            }
        }
    }

    function resetEntryGroup(group) {
        setEntryGroupExpanded(group, group.getAttribute('data-default-open') === 'true', false);
        group.querySelectorAll('[data-portal-topbar-entry-row]').forEach(function (row) {
            row.hidden = false;
        });
        group.hidden = false;
    }

    function applyEntryFilter(menu) {
        if (!menu) {
            return;
        }

        var input = menu.querySelector('[data-portal-topbar-entry-filter]');
        var term = input ? (input.value || '').trim().toLowerCase() : '';

        menu.querySelectorAll('[data-portal-topbar-entry-group]').forEach(function (group) {
            if (term.length === 0) {
                resetEntryGroup(group);
                return;
            }

            var header = group.querySelector(':scope > [data-portal-topbar-entry-row]');
            var childRows = Array.from(group.querySelectorAll('[data-portal-topbar-entry-group-entries] [data-portal-topbar-entry-row]'));
            var headerMatches = header
                ? (header.getAttribute('data-search') || '').toLowerCase().indexOf(term) >= 0
                : false;
            var childMatches = false;

            if (header) {
                header.hidden = false;
            }

            childRows.forEach(function (row) {
                var haystack = (row.getAttribute('data-search') || '').toLowerCase();
                var matches = haystack.indexOf(term) >= 0;
                row.hidden = !matches;
                childMatches = childMatches || matches;
            });

            group.hidden = !headerMatches && !childMatches;
            setEntryGroupExpanded(group, headerMatches || childMatches, true);
        });
    }

    function initEntryFilter(input) {
        if (!input || input.dataset.portalTopbarEntryFilterInitialized === 'true') {
            return;
        }

        input.dataset.portalTopbarEntryFilterInitialized = 'true';
        input.addEventListener('input', function () {
            applyEntryFilter(input.closest('[data-portal-topbar-entry-menu]'));
        });
    }

    function initEntryGroupToggle(button) {
        if (!button || button.dataset.portalTopbarEntryGroupToggleInitialized === 'true') {
            return;
        }

        button.dataset.portalTopbarEntryGroupToggleInitialized = 'true';
        var group = button.closest('[data-portal-topbar-entry-group]');
        setEntryGroupExpanded(group, group && group.getAttribute('data-default-open') === 'true', false);

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();

            var currentGroup = button.closest('[data-portal-topbar-entry-group]');
            if (!currentGroup) {
                return;
            }

            var isExpanded = currentGroup.classList.contains('is-open');
            setEntryGroupExpanded(currentGroup, !isExpanded, false);
        });
    }

    function setFavoriteButton(button, isFavorite, root) {
        if (!button) {
            return;
        }

        var addText = root ? root.getAttribute('data-add-favorite-text') : '';
        var removeText = root ? root.getAttribute('data-remove-favorite-text') : '';
        var label = isFavorite ? (removeText || 'Remove favorite') : (addText || 'Add favorite');
        var icon = button.querySelector('span[aria-hidden="true"]');
        if (icon) {
            icon.textContent = isFavorite ? '★' : '☆';
        } else {
            button.textContent = isFavorite ? '★' : '☆';
        }

        button.classList.toggle('is-favorite', isFavorite);
        button.setAttribute('title', label);
        button.setAttribute('aria-label', label);
    }

    function normalizeFavoritePayload(payload) {
        if (!payload || !payload.entryKey) {
            return null;
        }

        return {
            source: payload.source || '',
            isFavorite: !!payload.isFavorite,
            entryKey: payload.entryKey || '',
            appInstanceId: payload.appInstanceId || '',
            href: payload.href || '',
            groupTitle: payload.groupTitle || '',
            entryTitle: payload.entryTitle || '',
            label: payload.label || ''
        };
    }

    function findNavigationRow(root, entryKey, appInstanceId) {
        if (!root) {
            return null;
        }

        return Array.from(root.querySelectorAll('[data-portal-topbar-entry-row]')).find(function (row) {
            return row.getAttribute('data-entry-key') === entryKey
                && (row.getAttribute('data-app-instance-id') || '') === (appInstanceId || '');
        }) || null;
    }

    function findFavoriteListRow(root, entryKey, appInstanceId) {
        if (!root) {
            return null;
        }

        return Array.from(root.querySelectorAll('[data-portal-topbar-favorite-row]')).find(function (row) {
            return row.getAttribute('data-favorite-entry-key') === entryKey
                && (row.getAttribute('data-favorite-app-instance-id') || '') === (appInstanceId || '');
        }) || null;
    }

    function createHiddenInput(name, value) {
        var input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = value || '';
        return input;
    }

    function createFavoriteForm(root, payload) {
        var form = document.createElement('form');
        form.method = 'post';
        form.action = root.getAttribute('data-favorite-toggle-url') || '';
        form.className = 'portal-topbar__favorite-form';
        form.setAttribute('data-portal-topbar-favorite-form', '');
        form.appendChild(createHiddenInput('entryKey', payload.entryKey));
        form.appendChild(createHiddenInput('appInstanceId', payload.appInstanceId || ''));

        var button = document.createElement('button');
        button.type = 'submit';
        button.className = 'portal-topbar__favorite-toggle is-favorite';
        button.setAttribute('data-portal-topbar-favorite-button', '');

        var icon = document.createElement('span');
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = '★';
        button.appendChild(icon);
        form.appendChild(button);

        setFavoriteButton(button, true, root);
        initFavoriteForm(form);
        return form;
    }

    function renderFavoriteLinkText(link, groupTitle, entryTitle, fallbackLabel) {
        if (!link) {
            return;
        }

        link.replaceChildren();
        if (groupTitle) {
            var module = document.createElement('span');
            module.className = 'portal-topbar__favorite-module';
            module.textContent = groupTitle;
            link.appendChild(module);
            link.appendChild(document.createTextNode(' / '));
        }

        var entry = document.createElement('span');
        entry.textContent = entryTitle || fallbackLabel || '';
        link.appendChild(entry);
    }

    function enrichFavoritePayload(root, payload) {
        var normalized = normalizeFavoritePayload(payload);
        if (!normalized || !root) {
            return normalized;
        }

        var navigationRow = findNavigationRow(root, normalized.entryKey, normalized.appInstanceId);
        if (navigationRow) {
            normalized.href = normalized.href || navigationRow.getAttribute('data-favorite-href') || '';
            normalized.label = normalized.label || navigationRow.getAttribute('data-favorite-label') || '';
            normalized.groupTitle = normalized.groupTitle || navigationRow.getAttribute('data-favorite-group-title') || '';
            normalized.entryTitle = normalized.entryTitle || navigationRow.getAttribute('data-favorite-entry-title') || '';
        }

        return normalized;
    }

    function applyFavoritePayloadToTopbar(root, payload) {
        var normalized = enrichFavoritePayload(root, payload);
        if (!normalized) {
            return;
        }

        updateFavoriteButtons(root, normalized.entryKey, normalized.appInstanceId, normalized.isFavorite);
        updateFavoritesMenu(root, normalized);
    }

    function dispatchFavoriteChanged(payload) {
        var normalized = normalizeFavoritePayload(payload);
        if (!normalized) {
            return;
        }

        normalized.source = 'topbar';
        window.dispatchEvent(new CustomEvent(FAVORITE_CHANGED_EVENT, { detail: normalized }));
    }

    function updateFavoritesMenu(root, payload) {
        payload = enrichFavoritePayload(root, payload);
        if (!root || !payload) {
            return;
        }

        var list = root.querySelector('[data-portal-topbar-favorites-list]');
        var empty = root.querySelector('[data-portal-topbar-favorites-empty]');
        if (!list) {
            return;
        }

        var appInstanceId = payload.appInstanceId || '';
        var existing = findFavoriteListRow(root, payload.entryKey, appInstanceId);
        if (payload.isFavorite) {
            var navigationRow = findNavigationRow(root, payload.entryKey, appInstanceId);
            var label = navigationRow
                ? navigationRow.getAttribute('data-favorite-label')
                : payload.label;
            var groupTitle = navigationRow
                ? navigationRow.getAttribute('data-favorite-group-title')
                : payload.groupTitle;
            var entryTitle = navigationRow
                ? navigationRow.getAttribute('data-favorite-entry-title')
                : payload.entryTitle;

            if (!existing) {
                existing = document.createElement('div');
                existing.className = 'portal-topbar__favorite-row';
                existing.setAttribute('data-portal-topbar-favorite-row', '');
                existing.setAttribute('data-favorite-entry-key', payload.entryKey);
                existing.setAttribute('data-favorite-app-instance-id', appInstanceId);

                var link = document.createElement('a');
                link.className = 'portal-topbar__favorite-link';
                existing.appendChild(link);
                existing.appendChild(createFavoriteForm(root, payload));
                list.appendChild(existing);
            }

            var existingLink = existing.querySelector('.portal-topbar__favorite-link');
            if (existingLink) {
                existingLink.href = payload.href || '#';
                renderFavoriteLinkText(existingLink, groupTitle, entryTitle, label || payload.label || payload.entryKey);
            }
        } else if (existing) {
            existing.remove();
        }

        if (empty) {
            empty.hidden = list.querySelectorAll('[data-favorite-entry-key]').length > 0;
        }
    }

    function updateFavoriteButtons(root, entryKey, appInstanceId, isFavorite) {
        if (!root) {
            return;
        }

        root.querySelectorAll('[data-portal-topbar-entry-row], [data-portal-topbar-favorite-row]').forEach(function (row) {
            var rowEntryKey = row.getAttribute('data-entry-key') || row.getAttribute('data-favorite-entry-key');
            var rowAppInstanceId = row.getAttribute('data-app-instance-id') || row.getAttribute('data-favorite-app-instance-id') || '';
            if (rowEntryKey !== entryKey || rowAppInstanceId !== (appInstanceId || '')) {
                return;
            }

            row.querySelectorAll('[data-portal-topbar-favorite-button]').forEach(function (button) {
                setFavoriteButton(button, isFavorite, root);
            });
        });
    }

    function initFavoriteForm(form) {
        if (!form || form.dataset.portalTopbarFavoriteFormInitialized === 'true') {
            return;
        }

        form.dataset.portalTopbarFavoriteFormInitialized = 'true';
        form.addEventListener('submit', function (event) {
            event.preventDefault();
            event.stopPropagation();

            var root = form.closest('[data-portal-topbar-root]');
            var button = form.querySelector('[data-portal-topbar-favorite-button]');
            if (button) {
                button.disabled = true;
            }

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(function (response) {
                    if (!response.ok) {
                        throw new Error('Favorite toggle failed with status ' + response.status + '.');
                    }

                    return response.json();
                })
                .then(function (payload) {
                    var normalized = enrichFavoritePayload(root, payload);
                    applyFavoritePayloadToTopbar(root, normalized);
                    dispatchFavoriteChanged(normalized);
                })
                .catch(function (error) {
                    if (window.console && typeof window.console.warn === 'function') {
                        window.console.warn('OMP topbar favorite toggle failed.', error);
                    }
                })
                .finally(function () {
                    if (button) {
                        button.disabled = false;
                    }
                });
        });
    }

    function notificationBadgeText(count) {
        return count > MAX_NOTIFICATION_BADGE_COUNT
            ? MAX_NOTIFICATION_BADGE_COUNT + '+'
            : String(Math.max(0, count));
    }

    function updateNotificationBadge(root, count) {
        if (!root) {
            return;
        }

        var badge = root.querySelector('[data-portal-topbar-notification-badge]');
        if (!badge) {
            return;
        }

        var safeCount = Math.max(0, Number(count) || 0);
        badge.textContent = notificationBadgeText(safeCount);
        badge.hidden = safeCount === 0;
        updateNotificationMarkAllState(root, safeCount);
    }

    function updateMessageBadge(root, count) {
        if (!root) {
            return;
        }

        var badge = root.querySelector('[data-portal-topbar-message-badge]');
        if (!badge) {
            return;
        }

        var safeCount = Math.max(0, Number(count) || 0);
        badge.textContent = notificationBadgeText(safeCount);
        badge.hidden = safeCount === 0;

        var markAll = root.querySelector('.portal-topbar__messages-mark-all-form');
        if (markAll) {
            markAll.hidden = safeCount === 0;
        }
    }

    function updateNotificationEmptyState(root) {
        if (!root) {
            return;
        }

        var list = root.querySelector('[data-portal-topbar-notifications-list]');
        var empty = root.querySelector('[data-portal-topbar-notifications-empty]');
        var hasRows = !!(list && list.querySelector('[data-portal-topbar-notification-form]'));

        if (empty) {
            empty.hidden = hasRows;
        }

        updateNotificationMarkAllState(root);
    }

    function updateNotificationMarkAllState(root, unreadCount) {
        if (!root) {
            return;
        }

        var markAll = root.querySelector('[data-portal-topbar-notification-mark-all-form]');
        if (markAll) {
            var safeCount = Number(unreadCount);
            var hasUnread = Number.isFinite(safeCount)
                ? safeCount > 0
                : !!root.querySelector('[data-portal-topbar-notification-form].is-unread');
            markAll.hidden = !hasUnread;
        }
    }

    function markNotificationRowRead(form) {
        if (!form) {
            return;
        }

        form.classList.remove('is-unread');
        form.classList.add('is-read');
        var button = form.querySelector('.portal-topbar__notification-row');
        if (button) {
            button.classList.add('portal-topbar__notification-row--read');
        }
    }

    function emitNotificationChanged(notificationId, unreadCount, allRead) {
        window.dispatchEvent(new CustomEvent(NOTIFICATION_CHANGED_EVENT, {
            detail: {
                source: 'topbar',
                notificationId: notificationId ? String(notificationId) : '',
                unreadCount: Number.isFinite(Number(unreadCount)) ? Number(unreadCount) : null,
                allRead: !!allRead
            }
        }));
    }

    function notificationCallerText(item) {
        var source = item && (item.callerDisplayName || item.callerKey);
        return source ? String(source).trim().charAt(0).toUpperCase() : '!';
    }

    function notificationLevelClass(level) {
        var normalized = String(level || 'info').toLowerCase();
        return ['info', 'success', 'warning', 'error'].indexOf(normalized) >= 0 ? normalized : 'info';
    }

    function resolveSharedAssetUrl(value) {
        if (!value || value.indexOf('/_content/OpenModulePlatform.Web.Shared/') !== 0) {
            return value || '';
        }

        var stylesheet = document.querySelector('link[href*="_content/OpenModulePlatform.Web.Shared/css/portal-topbar.css"]');
        if (!stylesheet) {
            return value;
        }

        var href = stylesheet.getAttribute('href') || '';
        var absoluteHref = new URL(href, document.baseURI).href;
        var marker = '_content/OpenModulePlatform.Web.Shared/css/portal-topbar.css';
        var markerIndex = absoluteHref.indexOf(marker);
        if (markerIndex < 0) {
            return value;
        }

        return absoluteHref.substring(0, markerIndex) + value.substring(1);
    }

    function createNotificationForm(list, item) {
        var form = document.createElement('form');
        form.method = 'post';
        form.action = list.dataset.markReadUrl || '/notifications/mark-read';
        form.className = 'portal-topbar__notification-form ' + (item.isUnread ? 'is-unread' : 'is-read');
        form.setAttribute('data-portal-topbar-notification-form', '');
        form.setAttribute('data-enhance', 'false');
        form.dataset.notificationId = String(item.notificationId || '');
        form.dataset.notificationCreatedAt = item.createdAt || '';

        var input = document.createElement('input');
        input.type = 'hidden';
        input.name = 'notificationId';
        input.value = String(item.notificationId || '');
        form.appendChild(input);

        var button = document.createElement('button');
        button.type = 'submit';
        button.className = 'portal-topbar__notification-row portal-topbar__notification-row--' + notificationLevelClass(item.level);
        button.dataset.destinationUrl = item.destinationUrl || '';
        if (!item.isUnread) {
            button.classList.add('portal-topbar__notification-row--read');
        }

        var icon = document.createElement('span');
        icon.className = 'portal-topbar__notification-caller-icon';
        icon.setAttribute('aria-hidden', 'true');
        if (item.callerIcon) {
            var img = document.createElement('img');
            img.src = resolveSharedAssetUrl(item.callerIcon);
            img.alt = '';
            icon.appendChild(img);
        } else {
            var fallback = document.createElement('span');
            fallback.textContent = notificationCallerText(item);
            icon.appendChild(fallback);
        }
        button.appendChild(icon);

        var copy = document.createElement('span');
        copy.className = 'portal-topbar__notification-copy';

        var title = document.createElement('span');
        title.className = 'portal-topbar__notification-title';
        title.textContent = item.title || '';
        copy.appendChild(title);

        var content = document.createElement('span');
        content.className = 'portal-topbar__notification-content';
        content.textContent = item.content || '';
        copy.appendChild(content);

        button.appendChild(copy);
        form.appendChild(button);
        initNotificationForm(form);
        return form;
    }

    function reportNotificationSessionWarning(kind) {
        window.dispatchEvent(new CustomEvent(SESSION_STATUS_WARNING_EVENT, {
            detail: { kind: kind || 'auth' }
        }));
    }

    function initNotificationForm(form) {
        if (!form || form.dataset.portalTopbarNotificationFormInitialized === 'true') {
            return;
        }

        form.dataset.portalTopbarNotificationFormInitialized = 'true';
        var submitForm = function (event) {
            if (event) {
                event.preventDefault();
                event.stopPropagation();
            }

            if (form.dataset.portalTopbarNotificationSubmitting === 'true') {
                return;
            }

            form.dataset.portalTopbarNotificationSubmitting = 'true';

            var root = form.closest('[data-portal-topbar-root]');
            var button = form.querySelector('button[type="submit"]');
            if (button) {
                button.disabled = true;
            }

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(function (response) {
                    if (response.status === 401 || response.status === 403 || isSessionLoginResponse(response)) {
                        reportNotificationSessionWarning('auth');
                        throw new Error('Notification action requires sign-in.');
                    }

                    if (!response.ok) {
                        throw new Error('Notification mark-read failed with status ' + response.status + '.');
                    }

                    return response.json();
                })
                .then(function (payload) {
                    markNotificationRowRead(form);
                    updateNotificationBadge(root, payload.unreadCount);
                    updateNotificationEmptyState(root);
                    emitNotificationChanged(form.dataset.notificationId, payload.unreadCount, false);

                    if (payload.destinationUrl) {
                        window.location.href = payload.destinationUrl;
                    }
                })
                .catch(function (error) {
                    if (window.console && typeof window.console.warn === 'function') {
                        window.console.warn('OMP topbar notification action failed.', error);
                    }
                })
                .finally(function () {
                    form.dataset.portalTopbarNotificationSubmitting = 'false';
                    if (button) {
                        button.disabled = false;
                    }
                });
        };

        form.addEventListener('submit', submitForm);
        var rowButton = form.querySelector('button[type="submit"]');
        if (rowButton) {
            rowButton.addEventListener('click', submitForm);
        }
    }

    function initNotificationMarkAllForm(form) {
        if (!form || form.dataset.portalTopbarNotificationMarkAllFormInitialized === 'true') {
            return;
        }

        form.dataset.portalTopbarNotificationMarkAllFormInitialized = 'true';
        var submitForm = function (event) {
            if (event) {
                event.preventDefault();
                event.stopPropagation();
            }

            if (form.dataset.portalTopbarNotificationMarkAllSubmitting === 'true') {
                return;
            }

            form.dataset.portalTopbarNotificationMarkAllSubmitting = 'true';

            var root = form.closest('[data-portal-topbar-root]');
            var button = form.querySelector('button[type="submit"]');
            if (button) {
                button.disabled = true;
            }

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(function (response) {
                    if (response.status === 401 || response.status === 403 || isSessionLoginResponse(response)) {
                        reportNotificationSessionWarning('auth');
                        throw new Error('Notification action requires sign-in.');
                    }

                    if (!response.ok) {
                        throw new Error('Notification mark-all-read failed with status ' + response.status + '.');
                    }

                    return response.json();
                })
                .then(function (payload) {
                    var list = root ? root.querySelector('[data-portal-topbar-notifications-list]') : null;
                    if (list) {
                        list.querySelectorAll('[data-portal-topbar-notification-form]').forEach(function (row) {
                            markNotificationRowRead(row);
                        });
                    }

                    updateNotificationBadge(root, payload.unreadCount);
                    updateNotificationEmptyState(root);
                    emitNotificationChanged('', payload.unreadCount, true);
                })
                .catch(function (error) {
                    if (window.console && typeof window.console.warn === 'function') {
                        window.console.warn('OMP topbar notification action failed.', error);
                    }
                })
                .finally(function () {
                    form.dataset.portalTopbarNotificationMarkAllSubmitting = 'false';
                    if (button) {
                        button.disabled = false;
                    }
                });
        };

        form.addEventListener('submit', submitForm);
        var markAllButton = form.querySelector('button[type="submit"]');
        if (markAllButton) {
            markAllButton.addEventListener('click', submitForm);
        }
    }

    function getNotificationCursor(list) {
        var rows = list ? list.querySelectorAll('[data-portal-topbar-notification-form]') : [];
        if (!rows.length) {
            return null;
        }

        var last = rows[rows.length - 1];
        return {
            beforeCreatedAt: last.dataset.notificationCreatedAt || '',
            beforeNotificationId: last.dataset.notificationId || ''
        };
    }

    function setNotificationLazyState(root, loading, ended) {
        var loadingEl = root ? root.querySelector('[data-portal-topbar-notifications-loading]') : null;
        var endEl = root ? root.querySelector('[data-portal-topbar-notifications-end]') : null;
        if (loadingEl) {
            loadingEl.hidden = !loading;
        }

        if (endEl) {
            endEl.hidden = !ended;
        }
    }

    function loadMoreNotifications(list) {
        if (!list || list.dataset.notificationLoading === 'true' || list.dataset.notificationEnd === 'true') {
            return;
        }

        var root = list.closest('[data-portal-topbar-root]');
        var loadUrl = list.dataset.loadUrl;
        if (!loadUrl) {
            return;
        }

        var cursor = getNotificationCursor(list);
        list.dataset.notificationLoading = 'true';
        setNotificationLazyState(root, true, false);

        var url = new URL(loadUrl, window.location.origin);
        url.searchParams.set('limit', list.dataset.pageSize || '10');
        if (cursor && cursor.beforeCreatedAt && cursor.beforeNotificationId) {
            url.searchParams.set('beforeCreatedAt', cursor.beforeCreatedAt);
            url.searchParams.set('beforeNotificationId', cursor.beforeNotificationId);
        }

        fetch(url.toString(), {
            method: 'GET',
            credentials: 'same-origin',
            headers: {
                'X-Requested-With': 'XMLHttpRequest'
            }
        })
            .then(function (response) {
                if (response.status === 401 || response.status === 403 || isSessionLoginResponse(response)) {
                    reportNotificationSessionWarning('auth');
                    throw new Error('Notification list requires sign-in.');
                }

                if (!response.ok) {
                    throw new Error('Notification list failed with status ' + response.status + '.');
                }

                return response.json();
            })
            .then(function (payload) {
                var items = Array.isArray(payload.items) ? payload.items : [];
                items.forEach(function (item) {
                    list.appendChild(createNotificationForm(list, item));
                });

                if (!payload.hasMore || items.length === 0) {
                    list.dataset.notificationEnd = 'true';
                }

                updateNotificationEmptyState(root);
                setNotificationLazyState(root, false, list.dataset.notificationEnd === 'true' && !!list.querySelector('[data-portal-topbar-notification-form]'));
            })
            .catch(function (error) {
                if (window.console && typeof window.console.warn === 'function') {
                    window.console.warn('OMP topbar notification list failed.', error);
                }
                setNotificationLazyState(root, false, false);
            })
            .finally(function () {
                list.dataset.notificationLoading = 'false';
            });
    }

    function initNotificationLazyList(list) {
        if (!list || list.dataset.portalTopbarNotificationLazyInitialized === 'true') {
            return;
        }

        list.dataset.portalTopbarNotificationLazyInitialized = 'true';
        var scroller = list.closest('.portal-topbar__tray-body') || list.closest('.portal-topbar__notifications-menu') || list;
        scroller.addEventListener('scroll', function () {
            if (scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 48) {
                loadMoreNotifications(list);
            }
        });
    }

    function handleExternalNotificationChanged(event) {
        var detail = event.detail || {};
        var notificationId = String(detail.notificationId || '');
        initializedTopbars.forEach(function (root) {
            if (detail.allRead) {
                root.querySelectorAll('[data-portal-topbar-notification-form]').forEach(markNotificationRowRead);
            } else if (notificationId) {
                root.querySelectorAll('[data-portal-topbar-notification-form]').forEach(function (form) {
                    if (form.dataset.notificationId === notificationId) {
                        markNotificationRowRead(form);
                    }
                });
            }

            if (Number.isFinite(Number(detail.unreadCount))) {
                updateNotificationBadge(root, Number(detail.unreadCount));
            }

            updateNotificationEmptyState(root);
        });
    }

    function handleExternalFavoriteChanged(event) {
        var payload = normalizeFavoritePayload(event.detail);
        if (!payload || payload.source === 'topbar') {
            return;
        }

        document.querySelectorAll('[data-portal-topbar-root]').forEach(function (root) {
            applyFavoritePayloadToTopbar(root, payload);
        });
    }

    function getAdminSubmenus(root) {
        var scope = root || document;
        return Array.from(scope.querySelectorAll('[data-portal-topbar-admin-submenu]'));
    }

    function getHoverMenus(root) {
        var scope = root || document;
        return Array.from(scope.querySelectorAll('[data-portal-topbar-hover-menu]'));
    }

    function isPinned(menu) {
        return menu.dataset.pinned === 'true';
    }

    function setPinned(menu, value) {
        menu.dataset.pinned = value ? 'true' : 'false';
    }

    function closeAdminSubmenu(submenu) {
        if (!submenu) {
            return;
        }

        submenu.open = false;
        setPinned(submenu, false);
    }

    function closeSiblingAdminSubmenus(submenu) {
        var parentMenu = submenu ? submenu.closest('[data-portal-topbar-admin-menu]') : null;
        if (!parentMenu) {
            return;
        }

        getAdminSubmenus(parentMenu).forEach(function (candidate) {
            if (candidate !== submenu) {
                closeAdminSubmenu(candidate);
            }
        });
    }

    function openAdminSubmenu(submenu, pinned) {
        if (!submenu) {
            return;
        }

        closeSiblingAdminSubmenus(submenu);
        submenu.open = true;
        setPinned(submenu, !!pinned);
    }

    function closeHoverMenu(menu) {
        if (!menu) {
            return;
        }

        menu.open = false;
        setPinned(menu, false);
        getAdminSubmenus(menu).forEach(closeAdminSubmenu);
    }

    function closeSiblingHoverMenus(menu) {
        var root = menu ? menu.closest('[data-portal-topbar-root]') : null;
        var scope = root || document;
        getHoverMenus(scope).forEach(function (candidate) {
            if (candidate !== menu) {
                closeHoverMenu(candidate);
            }
        });
    }

    function openHoverMenu(menu, pinned) {
        if (!menu) {
            return;
        }

        closeSiblingHoverMenus(menu);
        menu.open = true;
        setPinned(menu, !!pinned);
    }

    function initAdminSubmenu(submenu) {
        if (!submenu || submenu.dataset.portalTopbarAdminSubmenuInitialized === 'true') {
            return;
        }

        submenu.dataset.portalTopbarAdminSubmenuInitialized = 'true';
        setPinned(submenu, false);

        var summary = submenu.querySelector('summary');
        if (!summary) {
            return;
        }

        summary.addEventListener('click', function (event) {
            event.preventDefault();

            var shouldOpenPinned = !submenu.open || !isPinned(submenu);
            if (shouldOpenPinned) {
                openAdminSubmenu(submenu, true);
            } else {
                closeAdminSubmenu(submenu);
            }
        });

        submenu.addEventListener('mouseenter', function () {
            if (!canOpenOnHover(submenu) || isPinned(submenu)) {
                return;
            }

            openAdminSubmenu(submenu, false);
        });

        submenu.addEventListener('mouseleave', function () {
            if (!canOpenOnHover(submenu) || isPinned(submenu)) {
                return;
            }

            closeAdminSubmenu(submenu);
        });
    }

    function initHoverMenu(menu) {
        if (!menu || menu.dataset.portalTopbarHoverMenuInitialized === 'true') {
            return;
        }

        menu.dataset.portalTopbarHoverMenuInitialized = 'true';
        setPinned(menu, false);

        var summary = menu.querySelector(':scope > summary');
        if (!summary) {
            return;
        }

        summary.addEventListener('click', function (event) {
            event.preventDefault();

            var shouldOpenPinned = !menu.open || !isPinned(menu);
            if (shouldOpenPinned) {
                openHoverMenu(menu, true);
            } else {
                closeHoverMenu(menu);
            }
        });

        menu.addEventListener('mouseenter', function () {
            if (!canOpenOnHover(menu) || isPinned(menu)) {
                return;
            }

            openHoverMenu(menu, false);
        });

        menu.addEventListener('mouseleave', function () {
            if (!canOpenOnHover(menu) || isPinned(menu)) {
                return;
            }

            closeHoverMenu(menu);
        });
    }

    function isEditableShortcutTarget(element) {
        if (!element) {
            return false;
        }

        if (element.isContentEditable) {
            return true;
        }

        return !!element.closest('input, textarea, select, button, a, [contenteditable], .toastui-editor-defaultUI, .toastui-editor, [role="dialog"], [aria-modal="true"]');
    }

    function normalizeShortcut(value) {
        return (value || '').trim().toLowerCase();
    }

    function findShortcutRoot() {
        return document.querySelector('[data-portal-topbar-root][data-shortcuts-enabled="true"]');
    }

    function handleTopbarShortcut(event) {
        if (!event || event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey) {
            return false;
        }

        if (isEditableShortcutTarget(document.activeElement)) {
            return false;
        }

        var root = findShortcutRoot();
        if (!root) {
            return false;
        }

        var key = normalizeShortcut(event.key);
        if (!key || event.shiftKey) {
            return false;
        }

        var allModulesShortcut = normalizeShortcut(root.getAttribute('data-shortcut-all-modules'));
        var favoritesShortcut = normalizeShortcut(root.getAttribute('data-shortcut-favorites'));

        if (allModulesShortcut && key === allModulesShortcut) {
            var allModules = root.querySelector('[data-portal-topbar-all-modules]');
            if (!allModules) {
                return false;
            }

            event.preventDefault();
            openHoverMenu(allModules, true);
            var input = allModules.querySelector('[data-portal-topbar-entry-filter]');
            if (input) {
                window.setTimeout(function () {
                    input.focus();
                }, 0);
            }
            return true;
        }

        if (favoritesShortcut && key === favoritesShortcut) {
            var favorites = root.querySelector('[data-portal-topbar-favorites]');
            if (!favorites) {
                return false;
            }

            event.preventDefault();
            if (favorites.open && isPinned(favorites)) {
                closeHoverMenu(favorites);
            } else {
                openHoverMenu(favorites, true);
            }
            return true;
        }

        return false;
    }

    function closeOverlaysOnOutsideClick(event) {
        document.querySelectorAll('[data-portal-topbar-admin-submenu][open]').forEach(function (submenu) {
            if (!submenu.contains(event.target)) {
                closeAdminSubmenu(submenu);
            }
        });

        document.querySelectorAll('[data-portal-topbar-hover-menu][open]').forEach(function (menu) {
            if (!menu.contains(event.target)) {
                closeHoverMenu(menu);
            }
        });

        document.querySelectorAll('[data-portal-topbar-overlay][open]').forEach(function (overlay) {
            if (overlay.matches('[data-portal-topbar-hover-menu], [data-portal-topbar-admin-submenu]')) {
                return;
            }

            if (!overlay.contains(event.target)) {
                overlay.open = false;
            }
        });
    }

    function parsePositiveInteger(value, fallback) {
        var parsed = parseInt(value || '', 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function normalizeTopbarUpdateMode(value) {
        var normalized = (value || '').trim().toLowerCase();
        return normalized === TOPBAR_UPDATE_MANUAL_MODE
            || normalized === TOPBAR_UPDATE_POLL_MODE
            || normalized === TOPBAR_UPDATE_PUSH_MODE
            ? normalized
            : TOPBAR_UPDATE_POLL_MODE;
    }

    function parseTopbarPollIntervalSeconds(value, fallback) {
        var text = (value || '').trim();
        if (!/^\d+$/.test(text)) {
            return fallback;
        }

        var parsed = Number(text);
        return Number.isFinite(parsed)
            && parsed >= MIN_TOPBAR_POLL_INTERVAL_SECONDS
            && parsed <= MAX_TOPBAR_POLL_INTERVAL_SECONDS
            ? parsed
            : fallback;
    }

    function getTopbarPollingConfig(root) {
        var pollInterval = parseTopbarPollIntervalSeconds(
            root ? root.getAttribute('data-notification-poll-interval') : '',
            DEFAULT_TOPBAR_POLL_INTERVAL_SECONDS);
        var hiddenInterval = parseTopbarPollIntervalSeconds(
            root ? root.getAttribute('data-topbar-polling-hidden-interval') : '',
            pollInterval);
        var mode = normalizeTopbarUpdateMode(root ? root.getAttribute('data-notification-update-mode') : '');

        return {
            enabled: !!root && mode !== TOPBAR_UPDATE_MANUAL_MODE,
            mode: mode,
            url: root ? root.getAttribute('data-topbar-summary-url') || '/topbar/summary' : '/topbar/summary',
            pushUrl: root ? root.getAttribute('data-notification-push-url') || '/topbar/notifications/updates' : '/topbar/notifications/updates',
            visibleInterval: pollInterval * 1000,
            hiddenInterval: hiddenInterval * 1000
        };
    }

    function isTopbarAutomaticRefreshActive(config) {
        return !!config && (
            config.mode === TOPBAR_UPDATE_POLL_MODE
            || (config.mode === TOPBAR_UPDATE_PUSH_MODE && topbarPollingState.pushFallbackActive)
        );
    }

    function getTopbarPollingDelay(config) {
        var baseDelay = document.visibilityState === 'hidden'
            ? config.hiddenInterval
            : config.visibleInterval;
        if (topbarPollingState.failures > 0) {
            return baseDelay * Math.min(MAX_BACKOFF_MULTIPLIER, topbarPollingState.failures + 1);
        }

        return baseDelay;
    }

    function stopTopbarSummaryTimer() {
        if (topbarPollingState.timer) {
            window.clearTimeout(topbarPollingState.timer);
            topbarPollingState.timer = 0;
        }
    }

    function scheduleTopbarSummaryRefresh(delay, force) {
        stopTopbarSummaryTimer();
        if (!topbarPollingState.root) {
            return;
        }

        var config = getTopbarPollingConfig(topbarPollingState.root);
        if (!force && !isTopbarAutomaticRefreshActive(config)) {
            return;
        }

        topbarPollingState.timer = window.setTimeout(function () {
            runTopbarSummaryRefresh(!!force);
        }, Math.max(0, delay));
    }

    function runTopbarSummaryRefreshSoon(force) {
        if (!topbarPollingState.root) {
            return;
        }

        scheduleTopbarSummaryRefresh(0, !!force);
    }

    async function runTopbarSummaryRefreshForRoot(root) {
        var config = getTopbarPollingConfig(root);
        if (!config.url || topbarPollingState.running) {
            return;
        }

        topbarPollingState.running = true;
        try {
            var payload = await fetchTopbarJson(config.url);
            applyTopbarSummary(payload);
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP topbar summary refresh failed.', error);
            }
        } finally {
            topbarPollingState.running = false;
        }
    }

    function applyTopbarSummary(payload) {
        if (!payload) {
            return;
        }

        initializedTopbars.forEach(function (root) {
            if (!root || !root.isConnected) {
                return;
            }

            if (payload.notifications) {
                updateNotificationBadge(root, payload.notifications.unreadCount);
            }

            if (payload.messages) {
                updateMessageBadge(root, payload.messages.unreadCount);
            }
        });
    }

    async function fetchTopbarJson(url) {
        var response = await fetch(url, {
            method: 'GET',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: {
                'Accept': 'application/json',
                'X-Requested-With': 'XMLHttpRequest'
            }
        });

        if (response.status === 401 || response.status === 403 || isSessionLoginResponse(response)) {
            reportNotificationSessionWarning('auth');
            throw new Error('Topbar summary requires sign-in.');
        }

        if (!response.ok) {
            throw new Error('Topbar summary failed with status ' + response.status + '.');
        }

        var contentType = response.headers.get('content-type') || '';
        if (contentType.indexOf('application/json') < 0) {
            throw new Error('Topbar summary returned a non-JSON response.');
        }

        return await response.json();
    }

    async function runTopbarSummaryRefresh(force) {
        var root = topbarPollingState.root;
        var config = getTopbarPollingConfig(root);
        if ((!force && !isTopbarAutomaticRefreshActive(config)) || !config.url) {
            return;
        }

        if (topbarPollingState.running) {
            scheduleTopbarSummaryRefresh(force ? 250 : config.visibleInterval, !!force);
            return;
        }

        topbarPollingState.running = true;
        try {
            var payload = await fetchTopbarJson(config.url);
            topbarPollingState.failures = 0;
            applyTopbarSummary(payload);
        } catch (error) {
            topbarPollingState.failures += 1;
            if (window.console && typeof window.console.warn === 'function' && topbarPollingState.failures === 1) {
                window.console.warn('OMP topbar summary refresh failed.', error);
            }
        } finally {
            topbarPollingState.running = false;
            if (isTopbarAutomaticRefreshActive(getTopbarPollingConfig(topbarPollingState.root))) {
                scheduleTopbarSummaryRefresh(getTopbarPollingDelay(config), false);
            }
        }
    }

    function initTopbarSummaryDropdownRefresh(menu) {
        if (!menu || menu.dataset.portalTopbarSummaryRefreshInitialized === 'true') {
            return;
        }

        menu.dataset.portalTopbarSummaryRefreshInitialized = 'true';
        menu.addEventListener('toggle', function () {
            if (menu.open) {
                runTopbarSummaryRefreshForRoot(menu.closest('[data-portal-topbar-root]'));
            }
        });
    }

    function loadSignalRClient() {
        if (window.signalR && typeof window.signalR.HubConnectionBuilder === 'function') {
            return Promise.resolve(window.signalR);
        }

        if (topbarPollingState.signalRClientPromise) {
            return topbarPollingState.signalRClientPromise;
        }

        topbarPollingState.signalRClientPromise = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = resolveSharedAssetUrl(SIGNALR_CLIENT_SCRIPT_URL);
            script.async = true;
            script.onload = function () {
                if (window.signalR && typeof window.signalR.HubConnectionBuilder === 'function') {
                    resolve(window.signalR);
                    return;
                }

                reject(new Error('SignalR browser client did not initialize.'));
            };
            script.onerror = function () {
                reject(new Error('SignalR browser client could not be loaded.'));
            };
            document.head.appendChild(script);
        });

        return topbarPollingState.signalRClientPromise;
    }

    function startTopbarPushFallback(error) {
        topbarPollingState.pushFallbackActive = true;
        if (!topbarPollingState.pushFallbackWarned && window.console && typeof window.console.warn === 'function') {
            topbarPollingState.pushFallbackWarned = true;
            window.console.warn('OMP topbar notification push setup failed; falling back to polling.', error);
        }

        scheduleTopbarSummaryRefresh(0, false);
    }

    function rememberTopbarPushEvent(eventKey) {
        if (!eventKey) {
            return false;
        }

        var now = Date.now();
        topbarPollingState.recentPushEventIds.forEach(function (seenAt, key) {
            if (now - seenAt > TOPBAR_PUSH_DEDUP_RETENTION_MS) {
                topbarPollingState.recentPushEventIds.delete(key);
            }
        });

        if (topbarPollingState.recentPushEventIds.has(eventKey)) {
            return true;
        }

        topbarPollingState.recentPushEventIds.set(eventKey, now);
        if (topbarPollingState.recentPushEventIds.size > MAX_TOPBAR_PUSH_DEDUP_KEYS) {
            var oldestKey = topbarPollingState.recentPushEventIds.keys().next().value;
            topbarPollingState.recentPushEventIds.delete(oldestKey);
        }

        return false;
    }

    function getTopbarPushEventKey(envelope) {
        if (!envelope || typeof envelope !== 'object') {
            return '';
        }

        var eventId = envelope.eventId || envelope.id;
        if (eventId !== undefined && eventId !== null && eventId !== '') {
            return 'event:' + String(eventId);
        }

        var deduplicationKey = envelope.deduplicationKey || envelope.dedupKey;
        return deduplicationKey ? 'key:' + String(deduplicationKey) : '';
    }

    function isTopbarSummaryPushCategory(category) {
        if (!category) {
            return true;
        }

        var normalized = String(category).toLowerCase();
        return normalized === 'notification'
            || normalized === 'message'
            || normalized === 'topbar.notification-state-changed'
            || normalized === 'topbar.message-state-changed'
            || normalized.indexOf('topbar.notification-') === 0
            || normalized.indexOf('topbar.message-') === 0;
    }

    function dispatchOmpPushEvent(envelope) {
        if (typeof window.CustomEvent !== 'function') {
            return;
        }

        window.dispatchEvent(new CustomEvent(PUSH_EVENT_RECEIVED_EVENT, {
            detail: {
                envelope: envelope || null,
                category: envelope && envelope.category ? String(envelope.category) : '',
                payload: envelope && envelope.payload ? envelope.payload : null
            }
        }));
    }

    function handleTopbarPushEvent(envelope) {
        var eventKey = getTopbarPushEventKey(envelope);
        if (rememberTopbarPushEvent(eventKey)) {
            return;
        }

        dispatchOmpPushEvent(envelope);

        if (!envelope || typeof envelope !== 'object') {
            runTopbarSummaryRefreshSoon(true);
            return;
        }

        if (isTopbarSummaryPushCategory(envelope.category)) {
            runTopbarSummaryRefreshSoon(true);
        }
    }

    function scheduleTopbarPushReconnect(config) {
        if (topbarPollingState.pushReconnectTimer || !topbarPollingState.root) {
            return;
        }

        topbarPollingState.pushReconnectTimer = window.setTimeout(function () {
            topbarPollingState.pushReconnectTimer = 0;
            var currentConfig = getTopbarPollingConfig(topbarPollingState.root);
            if (currentConfig.mode === TOPBAR_UPDATE_PUSH_MODE) {
                startTopbarPush(currentConfig);
            }
        }, Math.max(config.visibleInterval, DEFAULT_TOPBAR_POLL_INTERVAL_SECONDS * 1000));
    }

    function clearTopbarPushReconnect() {
        if (topbarPollingState.pushReconnectTimer) {
            window.clearTimeout(topbarPollingState.pushReconnectTimer);
            topbarPollingState.pushReconnectTimer = 0;
        }
    }

    function stopTopbarPushConnection() {
        clearTopbarPushReconnect();
        var connection = topbarPollingState.pushConnection;
        topbarPollingState.pushConnection = null;
        topbarPollingState.pushStarting = false;
        topbarPollingState.pushFallbackActive = false;
        if (connection && typeof connection.stop === 'function') {
            connection.stop().catch(function () {
            });
        }
    }

    async function startTopbarPush(config) {
        if (!config.pushUrl || topbarPollingState.pushStarting || topbarPollingState.pushConnection) {
            return;
        }

        topbarPollingState.pushStarting = true;
        try {
            var signalR = await loadSignalRClient();
            var currentConfig = getTopbarPollingConfig(topbarPollingState.root);
            if (currentConfig.mode !== TOPBAR_UPDATE_PUSH_MODE || currentConfig.pushUrl !== config.pushUrl) {
                return;
            }

            var builder = new signalR.HubConnectionBuilder()
                .withUrl(config.pushUrl)
                .withAutomaticReconnect();
            if (signalR.LogLevel && typeof builder.configureLogging === 'function') {
                builder.configureLogging(signalR.LogLevel.Warning);
            }

            var connection = builder.build();
            connection.on(TOPBAR_NOTIFICATION_PUSH_METHOD, function (envelope) {
                handleTopbarPushEvent(envelope);
            });
            connection.on(GENERIC_PUSH_EVENT_METHOD, function (envelope) {
                handleTopbarPushEvent(envelope);
            });
            connection.onreconnecting(function (error) {
                startTopbarPushFallback(error);
            });
            connection.onreconnected(function () {
                topbarPollingState.pushFallbackActive = false;
                topbarPollingState.failures = 0;
                stopTopbarSummaryTimer();
                runTopbarSummaryRefreshSoon(true);
            });
            connection.onclose(function (error) {
                if (topbarPollingState.pushConnection === connection) {
                    topbarPollingState.pushConnection = null;
                }

                var closedConfig = getTopbarPollingConfig(topbarPollingState.root);
                if (closedConfig.mode === TOPBAR_UPDATE_PUSH_MODE) {
                    startTopbarPushFallback(error);
                    scheduleTopbarPushReconnect(closedConfig);
                }
            });

            topbarPollingState.pushConnection = connection;
            await connection.start();
            topbarPollingState.pushFallbackActive = false;
            topbarPollingState.pushFallbackWarned = false;
            topbarPollingState.failures = 0;
            stopTopbarSummaryTimer();
        } catch (error) {
            if (topbarPollingState.pushConnection && typeof topbarPollingState.pushConnection.stop === 'function') {
                topbarPollingState.pushConnection.stop().catch(function () {
                });
            }

            topbarPollingState.pushConnection = null;
            startTopbarPushFallback(error);
        } finally {
            topbarPollingState.pushStarting = false;
        }
    }

    function registerTopbarSummaryRefreshHandlers() {
        if (topbarPollingState.handlersRegistered) {
            return;
        }

        topbarPollingState.handlersRegistered = true;
        window.addEventListener('focus', function () {
            if (isTopbarAutomaticRefreshActive(getTopbarPollingConfig(topbarPollingState.root))) {
                runTopbarSummaryRefreshSoon(false);
            }
        });
        document.addEventListener('visibilitychange', function () {
            var config = getTopbarPollingConfig(topbarPollingState.root);
            if (!isTopbarAutomaticRefreshActive(config)) {
                return;
            }

            if (document.visibilityState === 'visible') {
                runTopbarSummaryRefreshSoon(false);
            } else {
                scheduleTopbarSummaryRefresh(getTopbarPollingDelay(config), false);
            }
        });
        window.addEventListener(NOTIFICATION_CHANGED_EVENT, function () {
            if (isTopbarAutomaticRefreshActive(getTopbarPollingConfig(topbarPollingState.root))) {
                runTopbarSummaryRefreshSoon(false);
            }
        });
    }

    function initTopbarNotificationUpdates(topbar) {
        if (!topbar) {
            return;
        }

        var config = getTopbarPollingConfig(topbar);
        if (!config.enabled) {
            if (topbarPollingState.root === topbar) {
                stopTopbarSummaryTimer();
                stopTopbarPushConnection();
                topbarPollingState.root = null;
                topbarPollingState.mode = TOPBAR_UPDATE_MANUAL_MODE;
            }

            return;
        }

        topbar.querySelectorAll('[data-portal-topbar-notifications], [data-portal-topbar-messages]').forEach(initTopbarSummaryDropdownRefresh);
        registerTopbarSummaryRefreshHandlers();

        var rootChanged = topbarPollingState.root !== topbar;
        var modeChanged = topbarPollingState.mode !== config.mode;
        var pushUrlChanged = topbarPollingState.pushUrl !== config.pushUrl;
        if (rootChanged || modeChanged || pushUrlChanged) {
            stopTopbarSummaryTimer();
            stopTopbarPushConnection();
            topbarPollingState.failures = 0;
            topbarPollingState.pushFallbackWarned = false;
        }

        topbarPollingState.root = topbar;
        topbarPollingState.mode = config.mode;
        topbarPollingState.pushUrl = config.pushUrl;

        if (config.mode === TOPBAR_UPDATE_PUSH_MODE) {
            startTopbarPush(config);
            return;
        }

        if (!topbarPollingState.timer) {
            scheduleTopbarSummaryRefresh(0, false);
        }
    }

    function getSessionStatusConfig(root) {
        return {
            enabled: !!root && root.getAttribute('data-session-status-enabled') === 'true',
            url: root ? root.getAttribute('data-session-status-url') || '/auth/session-status' : '/auth/session-status',
            loginUrl: root ? root.getAttribute('data-session-login-url') || '/auth/login' : '/auth/login',
            visibleInterval: parsePositiveInteger(root ? root.getAttribute('data-session-status-visible-interval') : '', 60) * 1000,
            hiddenInterval: parsePositiveInteger(root ? root.getAttribute('data-session-status-hidden-interval') : '', 180) * 1000,
            sessionLostText: root ? root.getAttribute('data-session-lost-text') || 'Your session appears to have expired. Sign in again before continuing.' : 'Your session appears to have expired. Sign in again before continuing.',
            networkLostText: root ? root.getAttribute('data-session-network-lost-text') || 'The server could not be reached. Check the connection.' : 'The server could not be reached. Check the connection.',
            loginText: root ? root.getAttribute('data-session-login-text') || 'Sign in again' : 'Sign in again',
            reloadText: root ? root.getAttribute('data-session-reload-text') || 'Reload' : 'Reload'
        };
    }

    function getSessionStatusDelay(config) {
        var baseDelay = document.visibilityState === 'hidden'
            ? config.hiddenInterval
            : config.visibleInterval;
        if (sessionStatusState.currentKind === 'network' && sessionStatusState.failures > 0) {
            return baseDelay * Math.min(MAX_BACKOFF_MULTIPLIER, sessionStatusState.failures + 1);
        }

        return baseDelay;
    }

    function scheduleSessionStatusCheck(delay) {
        if (sessionStatusState.timer) {
            window.clearTimeout(sessionStatusState.timer);
        }

        if (!sessionStatusState.root) {
            return;
        }

        sessionStatusState.timer = window.setTimeout(runSessionStatusCheck, Math.max(0, delay));
    }

    function runSessionStatusCheckSoon() {
        if (!sessionStatusState.root) {
            return;
        }

        scheduleSessionStatusCheck(0);
    }

    function isSessionLoginResponse(response) {
        if (!response) {
            return false;
        }

        var url = (response.url || '').toLowerCase();
        return (response.redirected && url.indexOf('/login') >= 0)
            || url.indexOf('/auth/login') >= 0;
    }

    async function runSessionStatusCheck() {
        var root = sessionStatusState.root;
        var config = getSessionStatusConfig(root);
        if (!config.enabled || !config.url) {
            return;
        }

        if (sessionStatusState.running) {
            scheduleSessionStatusCheck(config.visibleInterval);
            return;
        }

        sessionStatusState.running = true;
        try {
            var response = await fetch(config.url, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403 || isSessionLoginResponse(response)) {
                sessionStatusState.currentKind = 'auth';
                showSessionStatusBanner('auth', config);
                return;
            }

            if (!response.ok) {
                sessionStatusState.failures += 1;
                sessionStatusState.currentKind = 'network';
                showSessionStatusBanner('network', config);
                return;
            }

            var payload = (response.headers.get('content-type') || '').indexOf('application/json') >= 0
                ? await response.json()
                : null;
            if (payload && payload.authenticated === false) {
                sessionStatusState.currentKind = 'auth';
                showSessionStatusBanner('auth', config);
                return;
            }

            sessionStatusState.failures = 0;
            sessionStatusState.currentKind = '';
            hideSessionStatusBanner();
        } catch (error) {
            sessionStatusState.failures += 1;
            sessionStatusState.currentKind = 'network';
            showSessionStatusBanner('network', config);
        } finally {
            sessionStatusState.running = false;
            scheduleSessionStatusCheck(getSessionStatusDelay(config));
        }
    }

    function createSessionStatusBanner() {
        var banner = document.querySelector('[data-session-status-banner]');
        if (banner) {
            return banner;
        }

        banner = document.createElement('div');
        banner.className = 'omp-session-status-banner';
        banner.setAttribute('data-session-status-banner', '');
        banner.setAttribute('role', 'status');
        banner.setAttribute('aria-live', 'polite');

        var message = document.createElement('span');
        message.className = 'omp-session-status-banner__message';
        message.setAttribute('data-session-status-message', '');
        banner.appendChild(message);

        var actions = document.createElement('span');
        actions.className = 'omp-session-status-banner__actions';

        var login = document.createElement('a');
        login.className = 'omp-session-status-banner__action';
        login.setAttribute('data-session-status-login', '');
        actions.appendChild(login);

        var reload = document.createElement('button');
        reload.type = 'button';
        reload.className = 'omp-session-status-banner__action';
        reload.setAttribute('data-session-status-reload', '');
        reload.addEventListener('click', function () {
            window.location.reload();
        });
        actions.appendChild(reload);

        banner.appendChild(actions);
        document.body.appendChild(banner);
        return banner;
    }

    function buildLoginUrl(loginUrl) {
        var returnUrl = window.location.pathname + window.location.search;
        var separator = loginUrl.indexOf('?') >= 0 ? '&' : '?';
        return loginUrl + separator + 'returnUrl=' + encodeURIComponent(returnUrl || '/');
    }

    function showSessionStatusBanner(kind, config) {
        var banner = createSessionStatusBanner();
        var message = banner.querySelector('[data-session-status-message]');
        var login = banner.querySelector('[data-session-status-login]');
        var reload = banner.querySelector('[data-session-status-reload]');

        banner.classList.toggle('omp-session-status-banner--auth', kind === 'auth');
        banner.classList.toggle('omp-session-status-banner--network', kind !== 'auth');
        if (message) {
            message.textContent = kind === 'auth' ? config.sessionLostText : config.networkLostText;
        }

        if (login) {
            login.textContent = config.loginText;
            login.href = buildLoginUrl(config.loginUrl);
            login.hidden = kind !== 'auth';
        }

        if (reload) {
            reload.textContent = config.reloadText;
        }

        banner.hidden = false;
    }

    function hideSessionStatusBanner() {
        var banner = document.querySelector('[data-session-status-banner]');
        if (banner) {
            banner.hidden = true;
        }
    }

    function handleSessionStatusWarning(event) {
        var root = sessionStatusState.root || document.querySelector('[data-portal-topbar-root][data-session-status-enabled="true"]');
        var config = getSessionStatusConfig(root);
        var kind = event && event.detail && event.detail.kind === 'network' ? 'network' : 'auth';
        if (root) {
            sessionStatusState.root = root;
        }

        if (kind === 'auth' && config.enabled && config.url && sessionStatusState.root) {
            runSessionStatusCheckSoon();
            return;
        }

        sessionStatusState.currentKind = kind;
        if (kind === 'network') {
            sessionStatusState.failures += 1;
        }

        showSessionStatusBanner(kind, config);
    }

    function initSessionStatusCheck(topbar) {
        if (!topbar || topbar.getAttribute('data-session-status-enabled') !== 'true') {
            return;
        }

        sessionStatusState.root = topbar;

        if (!sessionStatusState.handlersRegistered) {
            sessionStatusState.handlersRegistered = true;
            window.addEventListener('focus', runSessionStatusCheckSoon);
            document.addEventListener('visibilitychange', function () {
                if (document.visibilityState === 'visible') {
                    runSessionStatusCheckSoon();
                } else {
                    scheduleSessionStatusCheck(getSessionStatusDelay(getSessionStatusConfig(sessionStatusState.root)));
                }
            });
            window.addEventListener(SESSION_STATUS_WARNING_EVENT, handleSessionStatusWarning);
        }

        if (!sessionStatusState.timer) {
            scheduleSessionStatusCheck(0);
        }
    }

    function registerGlobalHandlers() {
        if (globalHandlersRegistered) {
            return;
        }

        globalHandlersRegistered = true;
        document.addEventListener('click', function (event) {
            closeOverlaysOnOutsideClick(event);
        });
        document.addEventListener('keydown', function (event) {
            if (handleTopbarShortcut(event)) {
                return;
            }

            if (event.key !== 'Escape') {
                return;
            }

            document.querySelectorAll('[data-portal-topbar-admin-submenu][open]').forEach(closeAdminSubmenu);
            document.querySelectorAll('[data-portal-topbar-hover-menu][open]').forEach(closeHoverMenu);
            document.querySelectorAll('[data-portal-topbar-overlay][open]').forEach(function (overlay) {
                if (!overlay.matches('[data-portal-topbar-hover-menu], [data-portal-topbar-admin-submenu]')) {
                    overlay.open = false;
                }
            });
        });
        window.addEventListener('resize', rebalanceAll);
        window.addEventListener('load', rebalanceAll);
        window.addEventListener(FAVORITE_CHANGED_EVENT, handleExternalFavoriteChanged);
        window.addEventListener(NOTIFICATION_CHANGED_EVENT, handleExternalNotificationChanged);
    }

    function initTopbarComponents(topbar) {
        topbar.querySelectorAll('[data-portal-topbar-hover-menu]').forEach(initHoverMenu);
        topbar.querySelectorAll('[data-portal-topbar-admin-submenu]').forEach(initAdminSubmenu);
        topbar.querySelectorAll('[data-portal-topbar-entry-filter]').forEach(initEntryFilter);
        topbar.querySelectorAll('[data-portal-topbar-entry-group-toggle]').forEach(initEntryGroupToggle);
        topbar.querySelectorAll('[data-portal-topbar-favorite-form]').forEach(initFavoriteForm);
        topbar.querySelectorAll('[data-portal-topbar-notification-form]').forEach(initNotificationForm);
        topbar.querySelectorAll('[data-portal-topbar-notification-mark-all-form]').forEach(initNotificationMarkAllForm);
        topbar.querySelectorAll('[data-portal-topbar-notifications-list]').forEach(initNotificationLazyList);
        updateNotificationEmptyState(topbar);
        initSessionStatusCheck(topbar);
        initTopbarNotificationUpdates(topbar);
    }

    function initTopbar(topbar) {
        if (!topbar) {
            return;
        }

        registerGlobalHandlers();

        if (topbar.dataset.portalTopbarInitialized === 'true') {
            initializedTopbars.add(topbar);
            initTopbarComponents(topbar);
            scheduleRebalance(topbar);
            if (resizeObserver) {
                resizeObserver.observe(topbar);
            }
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        initTopbarComponents(topbar);

        topbar.querySelectorAll('[data-portal-topbar-admin-menu]').forEach(function (menu) {
            var parentDetails = menu.closest('details');
            if (!parentDetails || parentDetails.dataset.portalTopbarAdminMenuInitialized === 'true') {
                return;
            }

            parentDetails.dataset.portalTopbarAdminMenuInitialized = 'true';
            parentDetails.addEventListener('toggle', function () {
                if (parentDetails.open) {
                    return;
                }

                getAdminSubmenus(menu).forEach(closeAdminSubmenu);
            });
        });

        scheduleRebalance(topbar);

        if (resizeObserver) {
            resizeObserver.observe(topbar);
        }
    }

    function initAll() {
        document.querySelectorAll('[data-portal-topbar-root]').forEach(initTopbar);
    }

    window.ompPortalTopBar = window.ompPortalTopBar || {};
    window.ompPortalTopBar.initAll = initAll;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
