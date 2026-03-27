(function () {
    function sortByIndex(nodes) {
        return Array.from(nodes).sort(function (left, right) {
            return Number(left.dataset.index) - Number(right.dataset.index);
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

        if (window.matchMedia('(max-width: 710px)').matches) {
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

    function registerOutsideClose(root) {
        if (!root || root.dataset.portalTopbarOutsideCloseRegistered === 'true') {
            return;
        }

        root.dataset.portalTopbarOutsideCloseRegistered = 'true';
        document.addEventListener('click', function (event) {
            root.querySelectorAll('[data-portal-topbar-overlay]').forEach(function (overlay) {
                if (overlay.open && !overlay.contains(event.target)) {
                    overlay.open = false;
                }
            });
        });
    }

    function initTopbar(topbar) {
        if (!topbar || topbar.dataset.portalTopbarInitialized === 'true') {
            rebalance(topbar);
            return;
        }

        topbar.dataset.portalTopbarInitialized = 'true';
        var root = topbar.closest('[data-portal-topbar-root]') || topbar;
        var rebalanceTopbar = function () { rebalance(topbar); };

        rebalanceTopbar();
        registerOutsideClose(root);

        if (typeof ResizeObserver !== 'undefined') {
            var resizeObserver = new ResizeObserver(rebalanceTopbar);
            resizeObserver.observe(topbar);
        }

        window.addEventListener('resize', rebalanceTopbar);
        window.addEventListener('load', rebalanceTopbar);
    }

    function initAll() {
        document.querySelectorAll('[data-portal-topbar]').forEach(initTopbar);
    }

    window.ompPortalTopBar = window.ompPortalTopBar || {};
    window.ompPortalTopBar.initAll = initAll;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }
})();
