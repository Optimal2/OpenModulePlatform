// File: OpenModulePlatform.Portal/wwwroot/js/site.js
// Portal-specific page behavior. The shared list component (sorting, filters,
// search, paging, popovers) lives in OpenModulePlatform.Web.Shared/js/omp-lists.js.
(() => {
    'use strict';

    function initAppHeaderOffset() {
        const header = document.querySelector('.app-header');
        if (!header) {
            return;
        }

        const update = () => {
            document.documentElement.style.setProperty('--app-header-height', `${Math.ceil(header.getBoundingClientRect().height)}px`);
        };

        update();
        window.addEventListener('resize', update);
        if (window.ResizeObserver) {
            new ResizeObserver(update).observe(header);
        }
    }

    function initAll() {
        initAppHeaderOffset();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
