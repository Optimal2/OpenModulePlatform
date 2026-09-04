// File: OpenModulePlatform.Portal/wwwroot/js/admin-iframe-urls.js
(() => {
    'use strict';

    const root = document.querySelector('[data-iframe-url-tabs]');
    if (!root) {
        return;
    }

    const tabs = Array.from(root.querySelectorAll('[data-iframe-url-tab]'));
    const panels = Array.from(root.querySelectorAll('[data-iframe-url-panel]'));

    const showPanel = panelName => {
        const panel = panels.find(candidate => candidate.dataset.iframeUrlPanel === panelName)
            || panels.find(candidate => candidate.dataset.iframeUrlPanel === 'urls');
        if (!panel) {
            return;
        }

        tabs.forEach(button => {
            const isActive = button.dataset.iframeUrlTab === panel.dataset.iframeUrlPanel;
            button.setAttribute('aria-selected', isActive ? 'true' : 'false');
            button.tabIndex = isActive ? 0 : -1;
        });

        panels.forEach(candidate => {
            const isActivePanel = candidate === panel;
            candidate.hidden = !isActivePanel;
            candidate.querySelectorAll('input, select, textarea, button').forEach(control => {
                if (!control.dataset.iframeOriginalDisabled) {
                    control.dataset.iframeOriginalDisabled = control.disabled ? 'true' : 'false';
                }

                control.disabled = !isActivePanel || control.dataset.iframeOriginalDisabled === 'true';
            });
        });
    };

    tabs.forEach(button => {
        button.addEventListener('click', () => showPanel(button.dataset.iframeUrlTab));
    });

    showPanel(root.dataset.initialPanel || 'urls');
})();
