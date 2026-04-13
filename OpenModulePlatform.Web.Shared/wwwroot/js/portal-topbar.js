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
    var resizeObserver = typeof ResizeObserver !== 'undefined'
        ? new ResizeObserver(function (entries) {
            entries.forEach(function (entry) {
                scheduleRebalance(entry.target);
            });
        })
        : null;

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

        var allLinks = sortByIndex(
            topbar.querySelectorAll('[data-portal-topbar-item]')
        );

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

    function closeOverlaysOnOutsideClick(event) {
        document.querySelectorAll('[data-portal-topbar-overlay][open]').forEach(function (overlay) {
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
        document.addEventListener('click', closeOverlaysOnOutsideClick);
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
            scheduleRebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        scheduleRebalance(topbar);

        if (resizeObserver) {
            resizeObserver.observe(topbar);
        }
    }

    function initAll() {
        document.querySelectorAll('[data-portal-topbar]').forEach(initTopbar);
    }

    window.ompPortalTopBar = window.ompPortalTopBar || {};
    window.ompPortalTopBar.initAll = initAll;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
