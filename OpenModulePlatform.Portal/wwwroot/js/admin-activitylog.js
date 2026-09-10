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
            // The icon follows the label: braces lead to the raw JSON, a document
            // leads back to the formatted fields.
            rawToggle.classList.toggle('btn-leading-icon--data-object', !raw);
            rawToggle.classList.toggle('btn-leading-icon--article', raw);
            cell.querySelector('.activity-detail__formatted').hidden = raw;
            cell.querySelector('.activity-detail__rawpanel').hidden = !raw;
            const copy = cell.querySelector('[data-activity-copy]');
            if (copy) {
                copy.hidden = !raw;
            }
            return;
        }

        const copyButton = event.target.closest('[data-activity-copy]');
        if (copyButton) {
            const text = copyButton.closest('td').querySelector('.activity-detail__rawpanel pre')?.textContent ?? '';
            // Icon-only: the check mark and the tooltip say "Copied" for a moment.
            const setLabel = (label) => {
                copyButton.title = label;
                copyButton.setAttribute('aria-label', label);
            };
            const done = () => {
                copyButton.classList.add('is-copied');
                setLabel(copyButton.dataset.labelCopied);
                window.setTimeout(() => {
                    copyButton.classList.remove('is-copied');
                    setLabel(copyButton.dataset.labelCopy);
                }, 1200);
            };
            if (navigator.clipboard?.writeText) {
                navigator.clipboard.writeText(text).then(done, () => { /* the browser said no; nothing to show */ });
            } else {
                const scratch = document.createElement('textarea');
                scratch.value = text;
                document.body.appendChild(scratch);
                scratch.select();
                try { document.execCommand('copy'); done(); } finally { scratch.remove(); }
            }
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

// The filter bar applies every choice at once (no Apply button). Instead of
// submitting the GET form and reloading the page, a change fetches the page
// with the new query and swaps the results card in place, then rebinds the
// row search to the new table and writes the query to the address bar so a
// reload or a bookmark keeps the filters. A failed fetch falls back to the
// plain submit. The row search is client-side only: typing filters the loaded
// rows through the list component's own search, nothing is sent to the
// server, and Enter does nothing.
(() => {
    'use strict';

    const form = document.getElementById('activity-filter-form');
    const results = document.querySelector('[data-activity-results]');
    if (!form) {
        return;
    }

    // The fallback is a plain navigation: form.submit() fires no submit event,
    // so nothing below can swallow it.
    const submit = () => form.submit();

    // The row search input is replaced by a clone after every swap (see
    // rebindSearch), so it is always looked up live, never held in a variable.
    const currentSearch = () => form.querySelector('.activity-bar__search');
    const clear = form.querySelector('[data-activity-clear]');
    const modules = form.querySelector('.activity-bar__modules');

    // Clear is greyed out by the server while nothing is filtered; text in the
    // row search is something to clear too, so it enables the link on its own.
    const serverClearDisabled = () => clear?.dataset.serverDisabled === 'true';
    const rememberClearState = () => {
        if (clear) {
            clear.dataset.serverDisabled = clear.getAttribute('aria-disabled') === 'true' ? 'true' : 'false';
        }
    };
    const syncClear = () => {
        if (!clear) {
            return;
        }
        const disabled = serverClearDisabled() && !(currentSearch()?.value.trim());
        if (disabled) {
            clear.setAttribute('aria-disabled', 'true');
            clear.setAttribute('tabindex', '-1');
        } else {
            clear.removeAttribute('aria-disabled');
            clear.removeAttribute('tabindex');
        }
    };
    rememberClearState();

    const buildUrl = () => {
        const params = new URLSearchParams(new FormData(form));
        // Empty fields make no filter; leave them out so the address stays short.
        Array.from(params.keys()).forEach((key) => {
            if (params.getAll(key).every((value) => value === '')) {
                params.delete(key);
            }
        });
        const query = params.toString();
        const base = form.getAttribute('action') || window.location.pathname;
        return query ? `${base}?${query}` : base;
    };

    // The list component binds the row search to a table once, by element.
    // After the swap the table is a new element, so the input is replaced by a
    // fresh clone (which drops the old listener) and the component binds it
    // again; the current text is then re-applied.
    const rebindSearch = () => {
        const current = currentSearch();
        if (!current || !window.ompLists?.init) {
            return;
        }
        const clone = current.cloneNode(true);
        // cloneNode copies the value attribute, not the text the user typed.
        clone.value = current.value;
        delete clone.dataset.listSearchInitialized;
        const hadFocus = document.activeElement === current;
        current.replaceWith(clone);
        if (hadFocus) {
            clone.focus();
        }
        clone.addEventListener('input', syncClear);
        clone.addEventListener('keydown', blockEnter);
        window.ompLists.init();
        clone.dispatchEvent(new Event('input', { bubbles: true }));
    };

    let pending = null;
    const refresh = async () => {
        if (!results || typeof window.fetch !== 'function' || typeof DOMParser !== 'function') {
            submit();
            return;
        }

        const url = buildUrl();
        pending?.abort();
        const controller = new AbortController();
        pending = controller;
        results.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(url, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { 'Accept': 'text/html', 'X-Requested-With': 'XMLHttpRequest' },
                signal: controller.signal
            });
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const html = await response.text();
            const doc = new DOMParser().parseFromString(html, 'text/html');
            const fresh = doc.querySelector('[data-activity-results]');
            if (!fresh) {
                throw new Error('No results card in the response');
            }
            results.replaceChildren(...Array.from(fresh.childNodes));
            const freshClear = doc.querySelector('[data-activity-clear]');
            if (clear && freshClear) {
                clear.dataset.serverDisabled = freshClear.getAttribute('aria-disabled') === 'true' ? 'true' : 'false';
                syncClear();
            }
            const freshModules = doc.querySelector('.activity-bar__modules');
            if (modules && freshModules) {
                modules.classList.toggle('list-filter--active', freshModules.classList.contains('list-filter--active'));
                const badge = modules.querySelector('.list-filter__count');
                const freshBadge = freshModules.querySelector('.list-filter__count');
                if (badge && freshBadge) {
                    badge.textContent = freshBadge.textContent;
                    badge.hidden = freshBadge.hidden;
                }
            }
            window.history.replaceState(null, '', url);
            rebindSearch();
        } catch (error) {
            if (error?.name === 'AbortError') {
                return;
            }
            submit();
            return;
        } finally {
            if (pending === controller) {
                pending = null;
                results.removeAttribute('aria-busy');
            }
        }
    };

    form.querySelectorAll('[data-activity-submit]').forEach((field) => {
        field.addEventListener('change', refresh);
    });
    form.addEventListener('omp-daterange-change', refresh);
    // Enter in the row search must not submit the form: the text only filters
    // the loaded rows.
    const blockEnter = (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
        }
    };
    const initialSearch = currentSearch();
    if (initialSearch) {
        initialSearch.addEventListener('input', syncClear);
        initialSearch.addEventListener('keydown', blockEnter);
    }
    if (clear) {
        clear.addEventListener('click', (event) => {
            if (clear.getAttribute('aria-disabled') === 'true') {
                event.preventDefault();
            }
        });
    }
})();
