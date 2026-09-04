// File: OpenModulePlatform.Portal/wwwroot/js/admin-module-package-import.js
(() => {
    'use strict';

    const root = document.querySelector('[data-import-export-tabs]');
    if (!root) {
        return;
    }

    const tabs = Array.from(root.querySelectorAll('[data-package-tab]'));
    const panels = Array.from(root.querySelectorAll('[data-package-panel]'));
    const dropAreas = Array.from(root.querySelectorAll('[data-file-drop-area]'));

    const setSelected = (activeTab) => {
        tabs.forEach(button => {
            const isActive = button === activeTab;
            button.setAttribute('aria-selected', isActive ? 'true' : 'false');
            button.tabIndex = isActive ? 0 : -1;
        });
    };

    const showPanel = panelName => {
        const panel = panels.find(candidate => candidate.dataset.packagePanel === panelName)
            || panels.find(candidate => candidate.dataset.packagePanel === 'import-universal');
        if (!panel) {
            return;
        }

        const tab = tabs.find(button => button.dataset.packageTab === panel.dataset.packagePanel);
        if (tab) {
            setSelected(tab);
        }

        panels.forEach(candidate => {
            candidate.hidden = candidate !== panel;
        });
    };

    tabs.forEach(button => {
        button.addEventListener('click', () => showPanel(button.dataset.packageTab));
    });

    dropAreas.forEach(area => {
        const input = area.querySelector('[data-file-input]');
        const list = area.querySelector('[data-file-list]');
        if (!input || !list) {
            return;
        }

        const renderFiles = () => {
            list.replaceChildren();
            const files = Array.from(input.files || []);
            list.hidden = files.length === 0;
            files.forEach(file => {
                const item = document.createElement('li');
                item.textContent = file.name;
                list.appendChild(item);
            });
        };

        area.addEventListener('click', event => {
            if (event.target === input) {
                return;
            }
            input.click();
        });

        area.addEventListener('keydown', event => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                input.click();
            }
        });

        area.addEventListener('dragover', event => {
            event.preventDefault();
            area.classList.add('is-drag-over');
        });

        area.addEventListener('dragleave', event => {
            if (!area.contains(event.relatedTarget)) {
                area.classList.remove('is-drag-over');
            }
        });

        area.addEventListener('drop', event => {
            event.preventDefault();
            area.classList.remove('is-drag-over');
            if (event.dataTransfer?.files?.length) {
                input.files = event.dataTransfer.files;
                renderFiles();
            }
        });

        input.addEventListener('change', renderFiles);
        renderFiles();
    });

    const quickImportToggle = root.querySelector('[data-quick-import-toggle]');
    const quickImportDisabledOptions = Array.from(root.querySelectorAll('[data-quick-import-disabled]'));
    const syncQuickImportOptions = () => {
        const quickImportEnabled = Boolean(quickImportToggle?.checked);
        quickImportDisabledOptions.forEach(input => {
            if (quickImportEnabled) {
                input.checked = false;
            }

            input.disabled = quickImportEnabled;
            input.closest('.checkbox-row')?.classList.toggle('is-disabled', quickImportEnabled);
        });
    };

    if (quickImportToggle) {
        quickImportToggle.addEventListener('change', syncQuickImportOptions);
        syncQuickImportOptions();
    }

    if (root.dataset.initialPanel) {
        showPanel(root.dataset.initialPanel);
    } else {
        showPanel('import-universal');
    }
})();
