(function () {
    function sortByIndex(nodes) {
        return Array.from(nodes).sort(function (left, right) {
            return Number(left.dataset.index) - Number(right.dataset.index);
        });
    }

    function rebalance(topbar) {
        const modulesHost = topbar.querySelector('[data-portal-topbar-modules]');
        const overflow = topbar.querySelector('[data-portal-topbar-overflow]');
        const menu = topbar.querySelector('[data-portal-topbar-menu]');
        const summary = overflow ? overflow.querySelector('summary') : null;

        if (!modulesHost || !overflow || !menu || !summary) {
            return;
        }

        const allLinks = sortByIndex([
            ...modulesHost.querySelectorAll('[data-portal-topbar-item]'),
            ...menu.querySelectorAll('[data-portal-topbar-item]')
        ]);

        for (const link of allLinks) {
            modulesHost.appendChild(link);
        }

        overflow.open = false;
        overflow.hidden = true;
        overflow.classList.remove('portal-topbar__overflow--measuring');
        menu.replaceChildren();

        if (topbar.scrollWidth <= topbar.clientWidth) {
            return;
        }

        overflow.hidden = false;
        overflow.classList.add('portal-topbar__overflow--measuring');

        while (modulesHost.children.length > 0 && topbar.scrollWidth > topbar.clientWidth) {
            const linkToMove = modulesHost.lastElementChild;
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
        if (!root || root.dataset.portalTopbarOutsideCloseBound === '1') {
            return;
        }

        root.dataset.portalTopbarOutsideCloseBound = '1';
        const overlays = root.querySelectorAll('[data-portal-topbar-overlay]');
        if (!overlays.length) {
            return;
        }

        document.addEventListener('click', function (event) {
            for (const overlay of overlays) {
                if (!overlay.open) {
                    continue;
                }

                if (!overlay.contains(event.target)) {
                    overlay.open = false;
                }
            }
        });
    }

    function initializeTopbar(topbar) {
        if (!topbar) {
            return;
        }

        const rebalanceTopbar = function () { rebalance(topbar); };

        if (topbar.dataset.portalTopbarInitialized !== '1') {
            topbar.dataset.portalTopbarInitialized = '1';
            const root = topbar.closest('[data-portal-topbar-root]') || topbar;
            registerOutsideClose(root);

            if (typeof ResizeObserver !== 'undefined') {
                const resizeObserver = new ResizeObserver(rebalanceTopbar);
                resizeObserver.observe(topbar);
            }

            window.addEventListener('resize', rebalanceTopbar);
            window.addEventListener('load', rebalanceTopbar);
        }

        rebalanceTopbar();
    }

    function initAll() {
        const topbars = document.querySelectorAll('[data-portal-topbar]');
        for (const topbar of topbars) {
            initializeTopbar(topbar);
        }
    }

    window.ompPortalTopBar = {
        initAll: initAll
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll);
    } else {
        initAll();
    }
})();
