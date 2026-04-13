(function () {
    // Keep a single set of global listeners for all shared top bars on the page.
    var COMPACT_BREAKPOINT_VARIABLE = '--portal-topbar-compact-breakpoint';
    var FALLBACK_COMPACT_BREAKPOINT = '710px';
    var initializedTopbars = new Set();
    var globalHandlersRegistered = false;
    // One observer instance is enough because each top bar is tracked separately in initializedTopbars.
    var resizeObserver = typeof ResizeObserver !== 'undefined'
        ? new ResizeObserver(function (entries) {
            entries.forEach(function (entry) {
                rebalance(entry.target);
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

    function rebalanceAll() {
        cleanupDisconnectedTopbars();
        initializedTopbars.forEach(rebalance);
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
            rebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        initializedTopbars.add(topbar);
        rebalance(topbar);

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
