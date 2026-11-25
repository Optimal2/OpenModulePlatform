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
// server, and Enter does nothing. The server search below the bar is the
// opposite: its field is named Q, a submit searches the server with GET, and
// the search starts from the default page size (Take is left out of its
// query). Typing there fetches in place after a beat, like a picker change;
// the plain GET submit of its own form is the fallback.
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
    // The server search form is swapped back from the response the same way,
    // so its inputs are looked up live too.
    const currentSearch = () => form.querySelector('.activity-bar__search');
    const qformOf = () => document.getElementById('activity-qsearch-form');
    const currentQ = () => qformOf()?.querySelector('input[name="Q"]') ?? null;
    const clear = form.querySelector('[data-activity-clear]');

    // Clear is greyed out by the server while nothing is filtered; text in the
    // row search or the server search is something to clear too, so either
    // enables the link on its own.
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
        const disabled = serverClearDisabled() && !(currentSearch()?.value.trim()) && !(currentQ()?.value.trim());
        if (disabled) {
            clear.setAttribute('aria-disabled', 'true');
            clear.setAttribute('tabindex', '-1');
        } else {
            clear.removeAttribute('aria-disabled');
            clear.removeAttribute('tabindex');
        }
    };
    rememberClearState();

    // Empty fields make no filter; leave them out so the address stays short.
    const dropEmptyParams = (params) => {
        Array.from(params.keys()).forEach((key) => {
            if (params.getAll(key).every((value) => value === '')) {
                params.delete(key);
            }
        });
    };

    const buildUrl = () => {
        const params = new URLSearchParams(new FormData(form));
        // Q lives in its own form, but an in-place change of the bar must
        // keep an active server search: merge the live text.
        params.delete('Q');
        const q = currentQ()?.value.trim() ?? '';
        if (q !== '') {
            params.set('Q', q);
        }
        dropEmptyParams(params);
        const query = params.toString();
        const base = form.getAttribute('action') || window.location.pathname;
        return query ? `${base}?${query}` : base;
    };

    // The server search query: the bar's live values plus the live Q, but
    // never Take, so the search starts from the default page size.
    const buildQSearchUrl = () => {
        const params = new URLSearchParams(new FormData(form));
        params.delete('Take');
        params.delete('Q');
        const q = currentQ()?.value.trim() ?? '';
        if (q !== '') {
            params.set('Q', q);
        }
        dropEmptyParams(params);
        const query = params.toString();
        const qform = qformOf();
        const base = qform?.getAttribute('action') || form.getAttribute('action') || window.location.pathname;
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
    let requestSequence = 0;
    const refresh = async (url) => {
        if (!results || typeof window.fetch !== 'function' || typeof DOMParser !== 'function') {
            // The fallback is the same URL as a plain navigation: it carries
            // the live query including Q, which the bar form alone has not.
            if (typeof url === 'string') {
                window.location.assign(url);
                return;
            }
            submit();
            return;
        }

        const requestId = ++requestSequence;
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
            if (requestId !== requestSequence) {
                return;
            }
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const html = await response.text();
            if (requestId !== requestSequence) {
                return;
            }
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
            // Each picker's checked state follows the server's answer (a user
            // or module that vanished, a value the server rejected); the shared
            // picker redraws its text and active colour from that.
            form.querySelectorAll('details[data-activity-picker]').forEach((picker) => {
                const fresh = doc.querySelector(`details[data-activity-picker="${picker.dataset.activityPicker}"]`);
                if (!fresh) {
                    return;
                }
                const freshChecked = new Set(Array.from(fresh.querySelectorAll('input:checked')).map((input) => input.value));
                picker.querySelectorAll('input').forEach((input) => {
                    input.checked = freshChecked.has(input.value);
                });
                window.ompPicker?.sync(picker);
            });
            // The page-size radio follows the same rule: a server search
            // answers with the default size, and the bar shows it instead of
            // the size the search started from.
            const freshTake = doc.querySelector('#activity-filter-form input[name="Take"]:checked');
            if (freshTake) {
                form.querySelectorAll('input[name="Take"]').forEach((input) => {
                    input.checked = input.value === freshTake.value;
                });
                const rowsPicker = form.querySelector('details[data-omp-picker-plain]');
                const rowsLabel = rowsPicker?.querySelector('[data-omp-picker-label]');
                if (rowsLabel) {
                    rowsLabel.textContent = freshTake.value;
                }
                if (rowsPicker) {
                    window.ompPicker?.sync(rowsPicker);
                }
            }
            // The server search form is the response's fresh copy: its hidden
            // filter copies, its Clear link and its disabled state follow the
            // answer, so the plain GET fallback stays correct after in-place
            // changes. Focus returns to the new field when it was there.
            const qform = qformOf();
            const freshQForm = doc.querySelector('#activity-qsearch-form');
            if (qform && freshQForm) {
                const current = currentQ();
                const freshQ = freshQForm.querySelector('input[name="Q"]');
                if (current && freshQ) {
                    freshQ.value = current.value;
                }
                const hadFocus = qform.contains(document.activeElement);
                const caret = current?.selectionStart ?? null;
                qform.replaceWith(freshQForm);
                if (hadFocus) {
                    const next = currentQ();
                    next?.focus();
                    if (next && caret !== null) {
                        try { next.setSelectionRange(caret, caret); } catch { /* not text-like */ }
                    }
                }
                bindQForm();
                syncClear();
            }
            window.history.replaceState(null, '', url);
            rebindSearch();
        } catch (error) {
            if (requestId !== requestSequence || error?.name === 'AbortError') {
                return;
            }
            // A failed fetch falls back to the plain navigation of the same
            // URL, not to the bar form submit, so an active Q survives it.
            window.location.assign(url);
            return;
        } finally {
            if (pending === controller) {
                pending = null;
                results.removeAttribute('aria-busy');
            }
        }
    };

    // A change waits a beat before fetching: arrow keys browsing a picker
    // fire one change per step, and only the value the user settles on
    // should reach the server. Typing in the server search waits the same
    // beat; an explicit submit searches at once. Every trigger shares one
    // timer, so one fetch follows however many controls changed.
    let changeTimer = 0;
    const refreshSoon = () => {
        window.clearTimeout(changeTimer);
        changeTimer = window.setTimeout(() => refresh(buildUrl()), 250);
    };
    const refreshQSoon = () => {
        window.clearTimeout(changeTimer);
        changeTimer = window.setTimeout(() => refresh(buildQSearchUrl()), 250);
    };
    form.querySelectorAll('[data-activity-submit]').forEach((field) => {
        field.addEventListener('change', refreshSoon);
    });
    form.addEventListener('omp-daterange-change', refreshSoon);
    // An implicit submit of the bar (Enter on a picker control) fetches in
    // place with the live query instead of reloading; the same URL is the
    // fallback navigation when the swap is not available.
    form.addEventListener('submit', (event) => {
        event.preventDefault();
        window.clearTimeout(changeTimer);
        refresh(buildUrl());
    });
    // Enter in the row search must not submit the form: the text only filters
    // the loaded rows. Enter in the server search submits its own form, and
    // that text searches the server.
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
    // The server search form is swapped from the response after every fetch,
    // so its listeners are (re)bound to the live form, like the row search.
    // A submit searches at once without Take; typing waits the shared beat.
    // The Clear link only empties the field and fires the same input event,
    // so one debounced fetch keeps the other filters and drops Q.
    const bindQForm = () => {
        const qform = qformOf();
        const q = currentQ();
        if (q && !q.dataset.qsearchBound) {
            q.dataset.qsearchBound = 'true';
            q.addEventListener('input', () => {
                syncClear();
                refreshQSoon();
            });
        }
        if (qform && !qform.dataset.qsearchBound) {
            qform.dataset.qsearchBound = 'true';
            qform.addEventListener('submit', (event) => {
                event.preventDefault();
                window.clearTimeout(changeTimer);
                refresh(buildQSearchUrl());
            });
            qform.querySelector('[data-activity-clear-q]')?.addEventListener('click', (event) => {
                const link = event.currentTarget;
                if (link?.getAttribute('aria-disabled') === 'true') {
                    event.preventDefault();
                    return;
                }
                if (!results || typeof window.fetch !== 'function') {
                    return;
                }
                event.preventDefault();
                const field = currentQ();
                if (field && field.value) {
                    field.value = '';
                    field.dispatchEvent(new Event('input', { bubbles: true }));
                }
            });
        }
    };
    bindQForm();
    // Clear puts every control back in place: the row search and the server
    // search emptied, every picker row off, the period on its neutral
    // preset. Each of those fires the change the bar already listens to, so
    // one debounced fetch follows and the address is rewritten; the link's
    // own navigation is the fallback when the swap is not available.
    if (clear) {
        clear.addEventListener('click', (event) => {
            if (clear.getAttribute('aria-disabled') === 'true') {
                event.preventDefault();
                return;
            }
            if (!results || typeof window.fetch !== 'function') {
                return;
            }
            event.preventDefault();
            const search = currentSearch();
            if (search && search.value) {
                search.value = '';
                search.dispatchEvent(new Event('input', { bubbles: true }));
            }
            const q = currentQ();
            if (q && q.value) {
                q.value = '';
                q.dispatchEvent(new Event('input', { bubbles: true }));
            }
            form.querySelectorAll('details[data-activity-picker]').forEach((picker) => {
                const on = Array.from(picker.querySelectorAll('input:checked'));
                on.forEach((input) => {
                    input.checked = false;
                });
                if (on.length > 0) {
                    on[0].dispatchEvent(new Event('change', { bubbles: true }));
                }
                window.ompPicker?.sync(picker);
            });
            // Only a period that is a choice goes back; the neutral one would
            // fire a change for nothing.
            const period = form.querySelector('[data-omp-daterange]');
            if (period?.classList.contains('omp-daterange--active')) {
                window.ompDatetime?.applyRangePreset?.(period);
            }
            syncClear();
        });
    }
})();
