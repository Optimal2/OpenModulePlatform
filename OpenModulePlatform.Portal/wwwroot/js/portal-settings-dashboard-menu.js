// File: OpenModulePlatform.Portal/wwwroot/js/portal-settings-dashboard-menu.js
// The settings page's "Put the menu back": the dashboard's edit menu keeps
// the place it was dragged to in the browser, per user, under the key the
// dashboard script uses (portal-dashboard.js, bindDashboardMenuDrag); this
// clears it, so the menu stands in its corner the next time the dashboard
// is shown.
(function () {
    'use strict';

    var panel = document.querySelector('[data-dashboard-menu-settings]');
    var button = panel ? panel.querySelector('[data-dashboard-menu-reset-position-button]') : null;
    if (!panel || !button) {
        return;
    }

    var storageKey = 'omp.dashboard.editMenuPosition:' + (panel.getAttribute('data-dashboard-draft-key') || 'anonymous');
    button.addEventListener('click', function () {
        try {
            window.localStorage.removeItem(storageKey);
        } catch (error) {
            // Storage blocked: nothing was stored to clear.
        }

        var status = panel.querySelector('[data-dashboard-menu-reset-status]');
        if (status) {
            status.hidden = false;
        }
    });
})();
