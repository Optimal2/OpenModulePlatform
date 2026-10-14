// File: OpenModulePlatform.Portal/wwwroot/js/portal-search.js
// Search on this page, with depth: a small corner panel (Ctrl+Shift+F, or the
// page's search button) that finds text everywhere the page has it, including
// rows on other pages of a paged list and text in collapsed details, which the
// browser's own find never sees. Matches are painted with the CSS Custom
// Highlight API, so no element is inserted into the page; the one match the
// user is standing on is revealed by asking the page (an `omp:reveal` event the
// match's owners answer by expanding or paging) and then scrolled into view.
//
// Pilot on the maintenance page. The mount is `[data-portal-search]`, with the
// labels in its data attributes and an optional `[data-portal-search-open]`
// button inside it. Nothing here knows how a page hides its content.
(function () {
    'use strict';

    const mount = document.querySelector('[data-portal-search]');
    if (!mount) {
        return;
    }

    const supportsHighlight = typeof CSS !== 'undefined' && 'highlights' in CSS && typeof Highlight === 'function';
    if (!supportsHighlight) {
        // Without painted matches the panel would be a worse find bar than the
        // browser's; leave the page to Ctrl+F.
        mount.hidden = true;
        return;
    }

    const labels = {
        title: mount.dataset.labelTitle || 'Search on this page',
        placeholder: mount.dataset.labelPlaceholder || 'Search',
        status: mount.dataset.labelStatus || '{0} of {1}',
        none: mount.dataset.labelNone || 'No matches',
        many: mount.dataset.labelMany || 'More than {0} matches',
        previous: mount.dataset.labelPrevious || 'Previous match',
        next: mount.dataset.labelNext || 'Next match',
        close: mount.dataset.labelClose || 'Close',
        hidden: mount.dataset.labelHidden || "Hidden by the list's filter"
    };
    const matchCap = Number.parseInt(mount.dataset.matchCap || '500', 10) || 500;
    const debounceMs = 150;
    const highlightName = 'omp-search';
    const currentHighlightName = 'omp-search-current';

    const panel = document.createElement('div');
    panel.className = 'portal-search';
    panel.setAttribute('role', 'search');
    panel.setAttribute('aria-label', labels.title);
    panel.hidden = true;
    panel.id = 'portal-search-panel';
    panel.innerHTML = `
        <input type="text" class="portal-search__input" autocomplete="off" spellcheck="false" placeholder="${escapeAttribute(labels.placeholder)}" aria-label="${escapeAttribute(labels.title)}" />
        <span class="portal-search__status" aria-live="polite"></span>
        <button type="button" class="portal-search__button" data-portal-search-prev title="${escapeAttribute(labels.previous)}" aria-label="${escapeAttribute(labels.previous)}">&#8593;</button>
        <button type="button" class="portal-search__button" data-portal-search-next title="${escapeAttribute(labels.next)}" aria-label="${escapeAttribute(labels.next)}">&#8595;</button>
        <button type="button" class="portal-search__button" data-portal-search-close title="${escapeAttribute(labels.close)}" aria-label="${escapeAttribute(labels.close)}">&#215;</button>`;
    const usePopover = typeof HTMLElement.prototype.showPopover === 'function';
    if (usePopover) {
        panel.setAttribute('popover', 'manual');
    }
    mount.appendChild(panel);

    const input = panel.querySelector('.portal-search__input');
    const status = panel.querySelector('.portal-search__status');
    const previousButton = panel.querySelector('[data-portal-search-prev]');
    const nextButton = panel.querySelector('[data-portal-search-next]');
    const closeButton = panel.querySelector('[data-portal-search-close]');

    let matches = [];
    let current = -1;
    let overflow = false;
    let debounceTimer = 0;
    // The term the matches were computed for; Enter re-runs the search when
    // the field has moved on (a keystroke still inside the debounce).
    let searchedTerm = '';
    // Whether the current match could be shown: a row the list's own
    // search or filter hides stays hidden whatever page it is on.
    let currentHidden = false;
    const opener = mount.querySelector('[data-portal-search-open]');
    let returnFocusTo = null;

    const isOpen = () => !panel.hidden;

    const open = () => {
        if (!isOpen()) {
            const active = document.activeElement;
            returnFocusTo = active instanceof HTMLElement && active !== document.body ? active : opener;
            panel.hidden = false;
            opener?.setAttribute('aria-expanded', 'true');
            if (usePopover) {
                try { panel.showPopover(); } catch { /* already shown, or not in the document */ }
            }
        }

        input.focus();
        input.select();
        if (input.value) {
            // Reopening repaints the remembered term where the page stands now;
            // the list is not paged until the user steps.
            runSearch(input.value, { scroll: false, reveal: false });
        }
    };

    const close = () => {
        window.clearTimeout(debounceTimer);
        clearHighlights();
        matches = [];
        current = -1;
        status.textContent = '';
        searchedTerm = '';
        currentHidden = false;
        panel.classList.remove('portal-search--empty');
        if (usePopover && panel.matches(':popover-open')) {
            try { panel.hidePopover(); } catch { /* not shown */ }
        }

        panel.hidden = true;
        opener?.setAttribute('aria-expanded', 'false');
        if (returnFocusTo?.isConnected) {
            returnFocusTo.focus();
        }

        returnFocusTo = null;
    };

    const clearHighlights = () => {
        CSS.highlights.delete(highlightName);
        CSS.highlights.delete(currentHighlightName);
    };

    // Every text node the page has, shown or hidden: a script, style or
    // template is never text to the reader, and the panel does not search
    // itself. The page can mark a region [data-portal-search-skip] (an
    // embedded document, say) to say it holds nothing to find.
    const collectTextNodes = () => {
        const nodes = [];
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode(node) {
                if (!node.data || !node.data.trim()) {
                    return NodeFilter.FILTER_REJECT;
                }

                const parent = node.parentElement;
                if (!parent || parent.closest('script, style, noscript, template, .portal-search, [data-portal-search-skip]')) {
                    return NodeFilter.FILTER_REJECT;
                }

                return NodeFilter.FILTER_ACCEPT;
            }
        });
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            nodes.push(node);
        }

        return nodes;
    };

    const findMatches = (term) => {
        const found = [];
        overflow = false;
        const needle = term.toLocaleLowerCase('sv');
        if (!needle) {
            return found;
        }

        for (const node of collectTextNodes()) {
            const haystack = node.data.toLocaleLowerCase('sv');
            // Lower-casing can change a string's length for a few scripts; the
            // offsets below would then point at the wrong characters.
            if (haystack.length !== node.data.length) {
                continue;
            }

            let from = 0;
            for (;;) {
                const at = haystack.indexOf(needle, from);
                if (at < 0) {
                    break;
                }

                if (found.length === matchCap) {
                    overflow = true;
                    return found;
                }

                found.push({ node, start: at, end: at + needle.length });

                from = at + needle.length;
            }
        }

        return found;
    };

    const rangeOf = (match) => {
        const range = new Range();
        range.setStart(match.node, match.start);
        range.setEnd(match.node, match.end);
        return range;
    };

    const paint = () => {
        CSS.highlights.set(highlightName, new Highlight(...matches.map(rangeOf)));
        if (current >= 0 && current < matches.length) {
            CSS.highlights.set(currentHighlightName, new Highlight(rangeOf(matches[current])));
        } else {
            CSS.highlights.delete(currentHighlightName);
        }
    };

    const updateStatus = () => {
        if (!input.value) {
            status.textContent = '';
        } else if (matches.length === 0) {
            status.textContent = labels.none;
        } else if (overflow) {
            status.textContent = labels.many.replace('{0}', String(matchCap));
        } else if (currentHidden) {
            status.textContent = labels.hidden;
        } else {
            status.textContent = labels.status
                .replace('{0}', String(current + 1))
                .replace('{1}', String(matches.length));
        }

        panel.classList.toggle('portal-search--empty', !!input.value && matches.length === 0);
    };

    // A match on another page of a paged list: step the list's own pager to the
    // page that shows the row. The shared list script owns the paging; this
    // only presses its buttons, from the first page forward, until the row is
    // in view. (The pilot's way in; the list script will answer `omp:reveal`
    // itself once the search moves into the shared scripts.) A row the list's
    // own search or filter hides is on no page: the walk ends at the last page
    // with the row still hidden, so the list is put back where it was and the
    // caller is told.
    const pageTo = (row) => {
        const table = row.closest('table[data-page-size]');
        if (!table || !table.id) {
            return false;
        }

        const pager = document.querySelector(`[data-list-pager="${table.id}"]`);
        const previous = pager?.querySelector('[data-list-pager-prev]');
        const next = pager?.querySelector('[data-list-pager-next]');
        if (!previous || !next) {
            return false;
        }

        let stepsBack = 0;
        for (let guard = 0; guard < 500 && !previous.disabled; guard++) {
            previous.click();
            stepsBack++;
        }

        for (let guard = 0; guard < 500 && row.hidden && !next.disabled; guard++) {
            next.click();
        }

        if (!row.hidden) {
            return true;
        }

        for (let guard = 0; guard < 500 && !previous.disabled; guard++) {
            previous.click();
        }

        for (let guard = 0; guard < stepsBack && !next.disabled; guard++) {
            next.click();
        }

        return false;
    };

    const reveal = (match) => {
        const element = match.node.parentElement;
        if (!element) {
            return false;
        }

        // A collapsed detail is hidden by its own class, not the row's hidden
        // flag; only a row the list has paged out needs paging.
        const row = element.closest('tr');
        if (row?.hidden && !pageTo(row)) {
            return false;
        }

        // The page's owners expand what they hide (a collapsed detail, a
        // folded group). Nothing listens on a page with nothing to expand.
        element.dispatchEvent(new CustomEvent('omp:reveal', { bubbles: true, detail: { node: match.node } }));
        return true;
    };

    const goTo = (index, { scroll = true, reveal: doReveal = true } = {}) => {
        if (matches.length === 0) {
            current = -1;
            currentHidden = false;
            paint();
            updateStatus();
            return;
        }

        current = ((index % matches.length) + matches.length) % matches.length;
        const match = matches[current];
        currentHidden = doReveal ? !reveal(match) : false;
        paint();
        updateStatus();
        if (scroll && !currentHidden) {
            match.node.parentElement?.scrollIntoView({ block: 'center', inline: 'nearest' });
        }
    };

    const runSearch = (term, options = {}) => {
        const previousNode = current >= 0 ? matches[current]?.node : null;
        searchedTerm = term;
        matches = findMatches(term);
        // Stay on the match the user was at when the term only grew.
        let index = 0;
        if (previousNode) {
            const keep = matches.findIndex((match) => match.node === previousNode);
            if (keep >= 0) {
                index = keep;
            }
        }

        goTo(index, options);
    };

    input.addEventListener('input', () => {
        window.clearTimeout(debounceTimer);
        debounceTimer = window.setTimeout(() => runSearch(input.value), debounceMs);
    });
    input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
            window.clearTimeout(debounceTimer);
            if (input.value !== searchedTerm) {
                runSearch(input.value);
                return;
            }

            goTo(current + (event.shiftKey ? -1 : 1));
        } else if (event.key === 'Escape') {
            event.preventDefault();
            close();
        }
    });
    previousButton.addEventListener('click', () => goTo(current - 1));
    nextButton.addEventListener('click', () => goTo(current + 1));
    closeButton.addEventListener('click', close);
    if (opener) {
        opener.setAttribute('aria-controls', panel.id);
        opener.setAttribute('aria-expanded', 'false');
        opener.addEventListener('click', open);
    }

    // Ctrl+Shift+F (Cmd+Shift+F on a Mac): a combination neither Windows nor
    // the browser uses. Ctrl+F stays the browser's; a user setting to take it
    // over is a later step.
    document.addEventListener('keydown', (event) => {
        if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.key.toLowerCase() === 'f') {
            event.preventDefault();
            open();
        } else if (event.key === 'Escape' && isOpen() && event.target !== input && !document.querySelector('dialog[open]')) {
            // Escape in a modal dialog is the dialog's; the panel stays.
            close();
        }
    });

    function escapeAttribute(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/"/g, '&quot;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }
})();
