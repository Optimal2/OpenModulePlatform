// File: OpenModulePlatform.Portal/wwwroot/js/admin-activitylog.js
// Activity entries expand on row click or via the "Show details" button (the
// keyboard path, with aria-expanded/aria-controls). The detail lives in a
// full-width follow row (data-list-follow keeps it glued to its entry through
// sorting and search). The open state is a CSS class; the shared list refresh
// owns the hidden attribute, and the page CSS lets hidden win over the open
// class so a filtered-out entry never leaves its detail behind.
(() => {
    'use strict';

    const toggleEntry = (row) => {
        const follow = row.nextElementSibling;
        if (!follow || !follow.classList.contains('activity-detail')) {
            return;
        }

        const open = follow.classList.toggle('activity-detail--open');
        row.classList.toggle('activity-row--open', open);
        row.querySelector('.activity-row__hint')?.setAttribute('aria-expanded', open ? 'true' : 'false');
    };

    document.addEventListener('click', (event) => {
        const rawToggle = event.target.closest('[data-activity-raw-toggle]');
        if (rawToggle) {
            const cell = rawToggle.closest('td');
            const raw = rawToggle.getAttribute('aria-pressed') !== 'true';
            rawToggle.setAttribute('aria-pressed', raw ? 'true' : 'false');
            rawToggle.textContent = raw ? rawToggle.dataset.labelFormatted : rawToggle.dataset.labelRaw;
            cell.querySelector('.activity-detail__formatted').hidden = raw;
            cell.querySelector('.activity-detail__rawpanel').hidden = !raw;
            return;
        }

        const hint = event.target.closest('.activity-row__hint');
        if (hint) {
            const row = hint.closest('tr[data-activity-row]');
            if (row) {
                toggleEntry(row);
            }
            return;
        }

        if (event.target.closest('a, button, input, select, textarea, summary, label, pre')) {
            return;
        }

        const row = event.target.closest('tr[data-activity-row]');
        if (!row) {
            return;
        }

        if (String(window.getSelection?.() || '')) {
            return;
        }

        toggleEntry(row);
    });
})();

// The filter bar applies every choice at once: selects, module checkboxes and
// the period control submit the GET form (no Apply button). Enter in the row
// search submits too, as the server-side text search; typing only filters the
// loaded rows through the list component's own search.
(() => {
    'use strict';

    const form = document.getElementById('activity-filter-form');
    if (!form) {
        return;
    }

    const submit = () => {
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit();
        } else {
            form.submit();
        }
    };

    form.querySelectorAll('[data-activity-submit]').forEach((field) => {
        field.addEventListener('change', submit);
    });
    form.addEventListener('omp-daterange-change', submit);

    const search = form.querySelector('.activity-bar__search');
    if (search) {
        search.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                submit();
            }
        });
    }
})();
