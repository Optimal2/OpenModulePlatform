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
            scheduleRebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        topbar.querySelectorAll('[data-portal-topbar-hover-menu]').forEach(initHoverMenu);
        topbar.querySelectorAll('[data-portal-topbar-admin-submenu]').forEach(initAdminSubmenu);

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
