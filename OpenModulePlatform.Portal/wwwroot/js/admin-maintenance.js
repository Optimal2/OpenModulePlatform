// File: OpenModulePlatform.Portal/wwwroot/js/admin-maintenance.js
// Finding details expand on row click or via the "Show details" button in
// the row (the keyboard and screen-reader path: a real button with
// aria-expanded/aria-controls). The text lives in a full-width follow row
// (data-list-follow keeps it glued to its finding through sorting,
// filtering and search) instead of a <details> inside the cell. The open
// state is a CSS class; the shared list refresh owns the hidden attribute
// for group visibility, and the page CSS lets hidden win over the open
// class so a filtered-out finding never leaves its detail behind.
// Selection (select-all, counts, enabling the bulk buttons) is the shared
// list's job.
(() => {
    'use strict';

    const toggleFinding = (row) => {
        const follow = row.nextElementSibling;
        if (!follow || !follow.classList.contains('maintenance-finding-detail')) {
            return;
        }

        const open = follow.classList.toggle('maintenance-finding-detail--open');
        row.classList.toggle('maintenance-finding-open', open);
        row.querySelector('.maintenance-finding-hint')?.setAttribute('aria-expanded', open ? 'true' : 'false');
    };

    document.addEventListener('click', (event) => {
        const hint = event.target.closest('.maintenance-finding-hint');
        if (hint) {
            const row = hint.closest('tr[data-finding-row]');
            if (row) {
                toggleFinding(row);
            }
            return;
        }

        if (event.target.closest('a, button, input, select, textarea, summary, label')) {
            return;
        }

        const row = event.target.closest('tr[data-finding-row]');
        if (!row) {
            return;
        }

        if (String(window.getSelection?.() || '')) {
            return;
        }

        toggleFinding(row);
    });

    // The list search box sits inside the findings POST form so the filter
    // and bulk buttons share one toolbar; Enter in it must not submit the
    // form (whose default button is Queue cleanup).
    document.getElementById('maintenance-findings-form')
        ?.querySelector('[data-list-search]')
        ?.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
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
