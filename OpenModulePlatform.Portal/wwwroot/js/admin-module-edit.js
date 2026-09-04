// File: OpenModulePlatform.Portal/wwwroot/js/admin-module-edit.js
(() => {
    'use strict';

    document.querySelectorAll('[data-contain-wheel-scroll]').forEach((element) => {
        element.addEventListener('wheel', (event) => {
            event.preventDefault();
            event.stopPropagation();
        }, { passive: false });
    });
})();
