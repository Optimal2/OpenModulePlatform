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

    function applyEntryFilter(menu) {
        if (!menu) {
            return;
        }

        var input = menu.querySelector('[data-portal-topbar-entry-filter]');
        var term = input ? (input.value || '').trim().toLowerCase() : '';
        menu.querySelectorAll('[data-portal-topbar-entry-row]').forEach(function (row) {
            var haystack = (row.getAttribute('data-search') || '').toLowerCase();
            row.hidden = term.length > 0 && haystack.indexOf(term) < 0;
        });

        menu.querySelectorAll('[data-portal-topbar-entry-group]').forEach(function (group) {
            var visibleRows = Array.from(group.querySelectorAll('[data-portal-topbar-entry-row]'))
                .some(function (row) { return !row.hidden; });
            group.hidden = !visibleRows;

            if (term.length > 0 && visibleRows) {
                group.open = true;
            }
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

    function updateFavoritesMenu(root, payload) {
        if (!root || !payload || !payload.entryKey) {
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
                : null;
            var groupTitle = navigationRow
                ? navigationRow.getAttribute('data-favorite-group-title')
                : null;
            var entryTitle = navigationRow
                ? navigationRow.getAttribute('data-favorite-entry-title')
                : null;

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
                    updateFavoriteButtons(root, payload.entryKey, payload.appInstanceId || '', !!payload.isFavorite);
                    updateFavoritesMenu(root, payload);
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
            if (!canHover() || isPinned(submenu)) {
                return;
            }

            openAdminSubmenu(submenu, false);
        });

        submenu.addEventListener('mouseleave', function () {
            if (!canHover() || isPinned(submenu)) {
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
            if (!canHover() || isPinned(menu)) {
                return;
            }

            openHoverMenu(menu, false);
        });

        menu.addEventListener('mouseleave', function () {
            if (!canHover() || isPinned(menu)) {
                return;
            }

            closeHoverMenu(menu);
        });
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
            topbar.querySelectorAll('[data-portal-topbar-favorite-form]').forEach(initFavoriteForm);
            scheduleRebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        topbar.querySelectorAll('[data-portal-topbar-hover-menu]').forEach(initHoverMenu);
        topbar.querySelectorAll('[data-portal-topbar-admin-submenu]').forEach(initAdminSubmenu);
        topbar.querySelectorAll('[data-portal-topbar-entry-filter]').forEach(initEntryFilter);
        topbar.querySelectorAll('[data-portal-topbar-favorite-form]').forEach(initFavoriteForm);

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
