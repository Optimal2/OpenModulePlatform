// File: OpenModulePlatform.Portal/wwwroot/js/admin-maintenance.js
(() => {
    'use strict';

    const selector = document.getElementById('select-maintenance-findings');
    if (!selector) {
        return;
    }

    const boxes = Array.from(document.querySelectorAll('.maintenance-finding-checkbox'));
    selector.addEventListener('change', () => {
        for (const box of boxes) {
            box.checked = selector.checked;
        }
    });
})();

(() => {
    'use strict';

    const scrollStorageKey = 'omp.adminMaintenance.retentionCleanupScrollY';
    const storedScrollY = window.sessionStorage.getItem(scrollStorageKey);
    if (storedScrollY) {
        window.sessionStorage.removeItem(scrollStorageKey);
        const scrollY = Number.parseInt(storedScrollY, 10);
        if (Number.isFinite(scrollY)) {
            window.requestAnimationFrame(() => window.scrollTo({ top: scrollY, left: window.scrollX }));
        }
    }

    const form = document.querySelector('[data-maintenance-retention-form]');
    if (!form) {
        return;
    }

    const input = form.querySelector('[data-maintenance-retention-input]');
    if (!input) {
        return;
    }

    const confirmButton = form.querySelector('[data-maintenance-retention-confirm]');
    const cancelButton = form.querySelector('[data-maintenance-retention-cancel]');
    const queueButton = form.querySelector('[data-maintenance-retention-queue]');
    const stepButtons = Array.from(form.querySelectorAll('[data-maintenance-retention-step]'));
    const confirmedValue = input.defaultValue;

    const getNumericValue = () => {
        const parsed = Number.parseInt(input.value, 10);
        return Number.isFinite(parsed) ? parsed : Number.parseInt(confirmedValue, 10);
    };

    const clampValue = (value) => {
        const min = Number.parseInt(input.min, 10);
        const max = Number.parseInt(input.max, 10);
        let nextValue = value;

        if (Number.isFinite(min)) {
            nextValue = Math.max(min, nextValue);
        }

        if (Number.isFinite(max)) {
            nextValue = Math.min(max, nextValue);
        }

        return nextValue;
    };

    const updateDirtyState = () => {
        const isDirty = input.value !== confirmedValue;
        const isValid = input.checkValidity();
        form.classList.toggle('is-dirty', isDirty);

        if (confirmButton) {
            confirmButton.disabled = !isDirty || !isValid;
        }

        if (cancelButton) {
            cancelButton.disabled = !isDirty;
        }

        if (queueButton) {
            queueButton.disabled = isDirty || !isValid;
        }
    };

    for (const stepButton of stepButtons) {
        stepButton.addEventListener('click', () => {
            const delta = Number.parseInt(stepButton.dataset.maintenanceRetentionStep ?? '0', 10);
            const currentValue = getNumericValue();
            input.value = clampValue(currentValue + delta).toString();
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.focus();
        });
    }

    input.addEventListener('input', updateDirtyState);
    input.addEventListener('change', updateDirtyState);
    form.addEventListener('submit', (event) => {
        const submitter = event.submitter;
        if (submitter?.matches('[data-maintenance-retention-queue]') && !submitter.disabled) {
            window.sessionStorage.setItem(scrollStorageKey, Math.round(window.scrollY).toString());
        }
    });
    cancelButton?.addEventListener('click', () => {
        input.value = confirmedValue;
        updateDirtyState();
        input.focus();
    });

    updateDirtyState();
})();

(() => {
    'use strict';

    const preview = document.querySelector('[data-maintenance-preview]');
    if (!preview) {
        return;
    }

    const groups = Array.from(preview.querySelectorAll('[data-maintenance-preview-group]'));
    const expandAll = preview.querySelector('[data-maintenance-preview-expand-all]');
    const collapseAll = preview.querySelector('[data-maintenance-preview-collapse-all]');

    const updateBulkActions = () => {
        const allExpanded = groups.length > 0 && groups.every(group => group.classList.contains('is-expanded'));
        const allCollapsed = groups.length === 0 || groups.every(group => group.classList.contains('is-collapsed'));

        if (expandAll) {
            expandAll.disabled = allExpanded;
        }

        if (collapseAll) {
            collapseAll.disabled = allCollapsed;
        }
    };

    const setGroupExpanded = (group, expanded) => {
        const toggle = group.querySelector('[data-maintenance-preview-toggle]');
        const body = group.querySelector('[data-maintenance-preview-body]');
        group.classList.toggle('is-expanded', expanded);
        group.classList.toggle('is-collapsed', !expanded);

        if (toggle) {
            toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        }

        if (body) {
            body.hidden = !expanded;
        }
    };

    for (const group of groups) {
        const toggle = group.querySelector('[data-maintenance-preview-toggle]');
        if (!toggle) {
            continue;
        }

        toggle.addEventListener('click', () => {
            setGroupExpanded(group, toggle.getAttribute('aria-expanded') !== 'true');
            updateBulkActions();
        });
    }

    expandAll?.addEventListener('click', () => {
        for (const group of groups) {
            setGroupExpanded(group, true);
        }
        updateBulkActions();
    });

    collapseAll?.addEventListener('click', () => {
        for (const group of groups) {
            setGroupExpanded(group, false);
        }
        updateBulkActions();
    });

    updateBulkActions();
})();
