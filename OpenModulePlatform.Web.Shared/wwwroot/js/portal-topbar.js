(function () {
    // Keep a single set of global listeners for all shared top bars on the page.
    // Rebalance work is coalesced per animation frame so that multiple resize notifications
    // do not trigger repeated layout thrashing for the same visual update.
    var COMPACT_BREAKPOINT_VARIABLE = '--portal-topbar-compact-breakpoint';
    var FALLBACK_COMPACT_BREAKPOINT = '710px';
    var initializedTopbars = new Set();
    var scheduledTopbars = new Set();
    var globalHandlersRegistered = false;
    var rebalanceFrameRequested = false;
    var FAVORITE_CHANGED_EVENT = 'omp:navigation-favorite-changed';
    var SESSION_STATUS_WARNING_EVENT = 'omp:session-status-warning';
    var sessionStatusState = {
        root: null,
        timer: 0,
        running: false,
        failures: 0,
        currentKind: '',
        handlersRegistered: false
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

    function copyTextToClipboard(text) {
        if (!text) {
            return Promise.resolve();
        }

        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text);
        }

        return new Promise(function (resolve, reject) {
            try {
                var textArea = document.createElement('textarea');
                textArea.value = text;
                textArea.setAttribute('readonly', 'readonly');
                textArea.style.position = 'fixed';
                textArea.style.top = '-9999px';
                textArea.style.left = '-9999px';
                document.body.appendChild(textArea);
                textArea.focus();
                textArea.select();

                if (document.execCommand('copy')) {
                    document.body.removeChild(textArea);
                    resolve();
                    return;
                }

                document.body.removeChild(textArea);
                reject(new Error('Copy command failed.'));
            } catch (error) {
                reject(error);
            }
        });
    }

    function tryCopyFromButton(target) {
        var button = target.closest('[data-copy-to-clipboard]');
        if (!button) {
            return false;
        }

        var text = button.getAttribute('data-copy-text') || '';
        copyTextToClipboard(text).catch(function () {
            return null;
        });
        return true;
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
            return baseDelay * Math.min(6, sessionStatusState.failures + 1);
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

            var payload = response.headers.get('content-type')?.indexOf('application/json') >= 0
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
        } catch {
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
            if (tryCopyFromButton(event.target)) {
                return;
            }

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
    }

    function initTopbar(topbar) {
        if (!topbar) {
            return;
        }

        registerGlobalHandlers();

        if (topbar.dataset.portalTopbarInitialized === 'true') {
            initializedTopbars.add(topbar);
            topbar.querySelectorAll('[data-portal-topbar-hover-menu]').forEach(initHoverMenu);
            topbar.querySelectorAll('[data-portal-topbar-admin-submenu]').forEach(initAdminSubmenu);
            topbar.querySelectorAll('[data-portal-topbar-entry-filter]').forEach(initEntryFilter);
            topbar.querySelectorAll('[data-portal-topbar-entry-group-toggle]').forEach(initEntryGroupToggle);
            topbar.querySelectorAll('[data-portal-topbar-favorite-form]').forEach(initFavoriteForm);
            initSessionStatusCheck(topbar);
            scheduleRebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        topbar.querySelectorAll('[data-portal-topbar-hover-menu]').forEach(initHoverMenu);
        topbar.querySelectorAll('[data-portal-topbar-admin-submenu]').forEach(initAdminSubmenu);
        topbar.querySelectorAll('[data-portal-topbar-entry-filter]').forEach(initEntryFilter);
        topbar.querySelectorAll('[data-portal-topbar-entry-group-toggle]').forEach(initEntryGroupToggle);
        topbar.querySelectorAll('[data-portal-topbar-favorite-form]').forEach(initFavoriteForm);
        initSessionStatusCheck(topbar);

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
