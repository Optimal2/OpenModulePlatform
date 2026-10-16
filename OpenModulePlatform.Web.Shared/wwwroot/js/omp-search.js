// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-search.js
// Search on this page, with depth: the top bar's find bar (Ctrl+Shift+F, or
// the search icon among the top bar's actions) that finds text everywhere the
// page has it, including rows on other pages of a paged list and text in
// collapsed details, which the browser's own find never sees. Matches are
// painted with the CSS Custom Highlight API, so no element is inserted into
// the page; the one match the user is standing on is revealed by asking the
// page (an `omp:reveal` event the match's owners answer by expanding or
// paging) and then scrolled into view.
//
// Depth is a checkbox in the panel, on by default: off, the search sees only
// what the reader sees, like the browser's own find. The choice is kept in
// the browser until a user setting carries it. Ctrl+F stays the browser's
// unless the app's user setting (rendered on <body>) hands it over.
//
// The panel has two placements. With room in the bar it grows leftward out
// of the icon, inside the bar, taking only the space the navigation leaves
// free (never pushing the bar's own overflow logic). Without room, or on a
// compact viewport, it hangs under the bar instead.
//
// The mount is `[data-omp-search]` in the top bar (the PortalTopBar view and
// its Blazor twin render it), with the labels in its data attributes and the
// opener `[data-omp-search-open]` inside it. The mount stays hidden until
// this script marks it ready, so an app that does not load the script shows
// no dead icon. A host may replace the mount after the script bound it (a
// Blazor app re-renders its prerendered bar once the circuit is up, and on
// enhanced navigation); the script watches for that and binds the new one.
// A page can mark a region `[data-omp-search-skip]` (an embedded document,
// say) as holding nothing to find. Nothing here knows how a page hides its
// content.
(function () {
    'use strict';

    // Bound once the document is parsed, like portal-topbar.js: a host that
    // renders the bar after the script tag (a Blazor app without
    // prerendering, say) would otherwise never get its mount.
    const init = () => {

        let mount = document.querySelector('[data-omp-search]');
        if (!mount) {
            return;
        }

        let bar = mount.closest('.portal-topbar') || mount.parentElement;
        let nav = bar.querySelector('.portal-topbar__nav');

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
            hidden: mount.dataset.labelHidden || "Hidden by the list's filter",
            deep: mount.dataset.labelDeep || 'Deep',
            deepTitle: mount.dataset.labelDeepTitle || 'Also search collapsed rows and other pages'
        };
        const deepStorageKey = 'omp.search.deep';
        const matchCap = Number.parseInt(mount.dataset.matchCap || '500', 10) || 500;
        const debounceMs = 150;
        // Inline, the field needs room for the input, the count, the depth box
        // and three buttons; below this it hangs under the bar instead.
        const inlineMinWidth = 280;
        const inlineMaxWidth = 430;
        const inlineMargin = 24;
        const growMs = 150;
        const highlightName = 'omp-search';
        const currentHighlightName = 'omp-search-current';

        const panel = document.createElement('div');
        panel.className = 'omp-search';
        panel.setAttribute('role', 'search');
        panel.setAttribute('aria-label', labels.title);
        panel.hidden = true;
        panel.id = 'omp-search-panel';
        panel.innerHTML = `
            <input type="text" class="omp-search__input" autocomplete="off" spellcheck="false" placeholder="${escapeAttribute(labels.placeholder)}" aria-label="${escapeAttribute(labels.title)}" />
            <span class="omp-search__status" aria-live="polite"></span>
            <label class="omp-search__deep" title="${escapeAttribute(labels.deepTitle)}"><input type="checkbox" data-omp-search-deep checked /><span>${escapeAttribute(labels.deep)}</span></label>
            <button type="button" class="omp-search__button" data-omp-search-prev title="${escapeAttribute(labels.previous)}" aria-label="${escapeAttribute(labels.previous)}">&#8593;</button>
            <button type="button" class="omp-search__button" data-omp-search-next title="${escapeAttribute(labels.next)}" aria-label="${escapeAttribute(labels.next)}">&#8595;</button>
            <button type="button" class="omp-search__button" data-omp-search-close title="${escapeAttribute(labels.close)}" aria-label="${escapeAttribute(labels.close)}">&#215;</button>`;
        const usePopover = typeof HTMLElement.prototype.showPopover === 'function';
        // Hanging under the bar, the panel lives on <body>: a fixed header with
        // a backdrop-filter, or a sticky header with a low z-index, would
        // otherwise trap or cover it wherever the browser has no top layer to
        // lift it into. Inline, it moves into the mount, left of the icon.
        document.body.appendChild(panel);
        let inline = false;
        let growTimer = 0;
        let refitTimer = 0;
        // Shrinking back into the icon: still displayed, no longer open.
        let closing = false;
        // The bar's content box last reported by the observer. Observing
        // always reports once at once: that report is the baseline, not a
        // change, and must not cut the opening grow; only the observer's own
        // numbers are compared, since another measurement differs by a hair
        // at a device scale factor other than 1. Height counts too: a bar
        // that wraps changes height first, and the hanging panel's top
        // follows it.
        let observedBarSize = null;

        const input = panel.querySelector('.omp-search__input');
        const status = panel.querySelector('.omp-search__status');
        const previousButton = panel.querySelector('[data-omp-search-prev]');
        const nextButton = panel.querySelector('[data-omp-search-next]');
        const closeButton = panel.querySelector('[data-omp-search-close]');
        const deepBox = panel.querySelector('[data-omp-search-deep]');
        const ctrlFOverridden = document.body.dataset.pageSearchCtrlF === 'true';

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
        let opener = null;
        let returnFocusTo = null;

        const readDeep = () => {
            try {
                const stored = window.localStorage.getItem(deepStorageKey);
                return stored === null ? true : stored === '1';
            } catch {
                return true;
            }
        };
        const writeDeep = (value) => {
            try { window.localStorage.setItem(deepStorageKey, value ? '1' : '0'); } catch { /* storage unavailable */ }
        };
        let deep = readDeep();
        deepBox.checked = deep;

        const isOpen = () => !panel.hidden && !closing;


        // The panel hangs from the bottom edge of the top bar, its right edge at
        // the opener's, clamped to the viewport. Measured when opened and again
        // when the viewport moves; the Portal's header is fixed, other apps'
        // bars may scroll, so a bar that left the viewport pins the panel to
        // its top.
        const isCompactViewport = () => {
            const breakpoint = Number.parseFloat(getComputedStyle(bar).getPropertyValue('--portal-topbar-compact-breakpoint'));
            return Number.isFinite(breakpoint) && window.innerWidth <= breakpoint;
        };

        // The width a flex row's shown children take, with the row's gaps.
        const contentWidth = (row) => {
            const gap = Number.parseFloat(getComputedStyle(row).columnGap) || 0;
            let used = 0;
            let shown = 0;
            for (const child of row.children) {
                if (child.hidden || getComputedStyle(child).display === 'none') {
                    continue;
                }

                // A child that grows (the modules host) fills the row, so its box
                // says nothing; its own children do (links the bar clips count).
                const childStyle = getComputedStyle(child);
                const grows = (Number.parseFloat(childStyle.flexGrow) || 0) > 0;
                // Margin boxes, as the flex algorithm sums them (the bar's menus
                // pull their padding back with negative margins).
                used += (grows ? contentWidth(child) : child.getBoundingClientRect().width)
                    + (Number.parseFloat(childStyle.marginLeft) || 0)
                    + (Number.parseFloat(childStyle.marginRight) || 0);
                shown++;
            }

            return used + Math.max(0, shown - 1) * gap;
        };

        // The room the bar leaves free for the field: the bar's inner width
        // minus what the navigation's and the actions' children take (the
        // field itself not counted), measured from content widths, never
        // from boxes, so a bar that has wrapped or a nav the field has
        // squeezed does not fool it. The bar's own overflow script measures
        // the whole bar, which the field never widens, so the two never fight.
        const freeSpace = () => {
            if (!nav || isCompactViewport()) {
                return 0;
            }

            const barStyle = getComputedStyle(bar);
            const inner = bar.clientWidth - (Number.parseFloat(barStyle.paddingLeft) || 0) - (Number.parseFloat(barStyle.paddingRight) || 0);
            const barGap = Number.parseFloat(barStyle.columnGap) || 0;
            const actions = mount.parentElement;
            const ownWidth = inline && !panel.hidden ? panel.getBoundingClientRect().width + (Number.parseFloat(getComputedStyle(panel).marginRight) || 0) : 0;
            const actionsWidth = actions && actions !== bar ? contentWidth(actions) - ownWidth : 0;
            return inner - contentWidth(nav) - barGap - actionsWidth;
        };

        const inlineWidth = () => {
            const width = Math.min(inlineMaxWidth, Math.floor(freeSpace() - inlineMargin));
            return width >= inlineMinWidth ? width : 0;
        };

        const place = () => {
            const barRect = bar.getBoundingClientRect();
            const anchor = (opener || mount).getBoundingClientRect();
            const width = panel.offsetWidth || 0;
            // The layout viewport, without the scrollbar, is what `right` measures from.
            const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
            const right = Math.max(8, Math.min(viewportWidth - anchor.right, viewportWidth - 8 - width));
            panel.style.top = `${Math.max(0, Math.round(barRect.bottom))}px`;
            panel.style.right = `${Math.round(right)}px`;
        };

        // Shows the panel inline (grown leftward out of the icon to `width`)
        // or, with width 0, hanging under the bar.
        const show = (width) => {
            window.clearTimeout(growTimer);
            closing = false;
            inline = width > 0;
            panel.classList.toggle('omp-search--inline', inline);
            if (inline) {
                panel.removeAttribute('popover');
                panel.style.top = '';
                panel.style.right = '';
                if (panel.parentElement !== mount) {
                    mount.insertBefore(panel, opener && opener.parentElement === mount ? opener : null);
                }

                panel.style.width = '0px';
                panel.hidden = false;
                // A frame at width 0 first, so the growth is a transition.
                void panel.offsetWidth;
                panel.style.width = `${width}px`;
                return;
            }

            if (panel.parentElement !== document.body) {
                document.body.appendChild(panel);
            }

            panel.style.width = '';
            if (usePopover) {
                panel.setAttribute('popover', 'manual');
            }

            panel.hidden = false;
            if (usePopover) {
                try { panel.showPopover(); } catch { /* already shown, or not in the document */ }
            }

            place();
        };

        const hide = () => {
            window.clearTimeout(growTimer);
            if (usePopover && panel.matches(':popover-open')) {
                try { panel.hidePopover(); } catch { /* not shown */ }
            }

            if (inline) {
                // Shrink back into the icon, then hide.
                closing = true;
                panel.style.width = '0px';
                growTimer = window.setTimeout(() => {
                    panel.hidden = true;
                    closing = false;
                }, growMs + 20);
                return;
            }

            panel.hidden = true;
        };

        // The bar or the viewport changed: the hanging panel follows the bar;
        // the inline field keeps to the room left, and hangs under the bar
        // when the room is gone (or comes back inline when it returns). The
        // room is measured a moment later, after the bar's own script has
        // rebalanced its navigation for the new width (a timer, not a frame:
        // frames pause in a hidden tab, and the bar's next size change
        // brings another measurement anyway).
        const refit = () => {
            if (!isOpen()) {
                return;
            }

            const width = inlineWidth();
            if (inline && width > 0) {
                panel.style.width = `${width}px`;
            } else if (inline !== (width > 0)) {
                // The placement changes under the user: keep the focus where it
                // was (in the panel, on the same control; elsewhere, untouched).
                const focused = panel.contains(document.activeElement) ? document.activeElement : null;
                window.clearTimeout(growTimer);
                panel.hidden = true;
                show(width);
                focused?.focus();
            } else {
                place();
            }
        };

        const onViewportChange = () => {
            // At once, and without the width transition, so a window being
            // dragged never leaves the bar wrapped around a field whose used
            // width lags its target; once more after the bar's own script has
            // settled, and the transition is back for the user's own actions.
            panel.classList.add('omp-search--settling');
            refit();
            window.clearTimeout(refitTimer);
            refitTimer = window.setTimeout(() => {
                refit();
                panel.classList.remove('omp-search--settling');
            }, 40);
        };

        // Scrolling moves only the hanging panel, which follows the bar.
        const onScroll = () => {
            if (!inline) {
                place();
            }
        };

        // The bar's size is what decides the placement; a ResizeObserver sees
        // it settle after layout, which a window resize event does not.
        const barObserver = typeof ResizeObserver === 'function'
            ? new ResizeObserver((entries) => {
                const rect = entries[entries.length - 1]?.contentRect;
                const size = rect ? `${rect.width}x${rect.height}` : '';
                const first = observedBarSize === null;
                if (first || size === observedBarSize) {
                    observedBarSize = size;
                    return;
                }

                observedBarSize = size;
                onViewportChange();
            })
            : null;

        const open = () => {
            if (!isOpen()) {
                const active = document.activeElement;
                returnFocusTo = active instanceof HTMLElement && active !== document.body ? active : opener;
                show(inlineWidth());
                opener?.setAttribute('aria-expanded', 'true');
                observedBarSize = null;
                barObserver?.observe(bar);
                window.addEventListener('resize', onViewportChange);
                window.addEventListener('scroll', onScroll, { passive: true });
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
            panel.classList.remove('omp-search--empty');
            hide();
            barObserver?.disconnect();
            window.clearTimeout(refitTimer);
            panel.classList.remove('omp-search--settling');
            window.removeEventListener('resize', onViewportChange);
            window.removeEventListener('scroll', onScroll);
            opener?.setAttribute('aria-expanded', 'false');
            (returnFocusTo?.isConnected ? returnFocusTo : opener)?.focus();

            returnFocusTo = null;
        };

        const clearHighlights = () => {
            CSS.highlights.delete(highlightName);
            CSS.highlights.delete(currentHighlightName);
        };

        // Every text node the page has, shown or hidden: a script, style or
        // template is never text to the reader, and the panel does not search
        // itself. The page can mark a region [data-omp-search-skip] (an
        // embedded document, say) to say it holds nothing to find. Without depth
        // only text that is laid out counts, which is what the browser's find
        // sees: a paged-out row, a collapsed detail and a closed group have no
        // boxes.
        const collectTextNodes = () => {
            const nodes = [];
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                acceptNode(node) {
                    if (!node.data || !node.data.trim()) {
                        return NodeFilter.FILTER_REJECT;
                    }

                    const parent = node.parentElement;
                    if (!parent || parent.closest('script, style, noscript, template, .omp-search, [data-omp-search-skip]')) {
                        return NodeFilter.FILTER_REJECT;
                    }

                    if (!deep && parent.getClientRects().length === 0) {
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

            panel.classList.toggle('omp-search--empty', !!input.value && matches.length === 0);
        };

        // The page's owners show what they hide: the shared list script pages
        // to a row it has paged out, the page expands a collapsed detail or a
        // folded group. Nothing listens on a page with nothing to show. A row
        // still hidden afterwards is one the list's own search or filter
        // excludes (it is on no page), and the caller is told.
        const reveal = (match) => {
            const element = match.node.parentElement;
            if (!element) {
                return false;
            }

            element.dispatchEvent(new CustomEvent('omp:reveal', { bubbles: true, detail: { node: match.node } }));
            // A collapsed detail is hidden by its own class, not the row's hidden
            // flag; the flag is the list's, and means paged out or excluded.
            return !element.closest('tr')?.hidden;
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
        deepBox.addEventListener('change', () => {
            deep = deepBox.checked;
            writeDeep(deep);
            window.clearTimeout(debounceTimer);
            if (input.value) {
                runSearch(input.value);
            }

            input.focus();
        });
        // Binds a mount: the bar it sits in, its opener, and the ready mark
        // that shows it. Called again when a host replaces the mount; a panel
        // that went with the old mount (inline) comes back closed on <body>.
        const bindMount = (next) => {
            mount = next;
            bar = mount.closest('.portal-topbar') || mount.parentElement;
            nav = bar.querySelector('.portal-topbar__nav');
            opener = mount.querySelector('[data-omp-search-open]');

            if (!panel.isConnected) {
                // The panel left with the old mount: back, closed, on <body>.
                window.clearTimeout(growTimer);
                closing = false;
                inline = false;
                panel.classList.remove('omp-search--inline', 'omp-search--settling');
                panel.style.width = '';
                panel.hidden = true;
                document.body.appendChild(panel);
                barObserver?.disconnect();
                window.removeEventListener('resize', onViewportChange);
                window.removeEventListener('scroll', onScroll);
            } else if (isOpen()) {
                // Hanging under a bar that was replaced: watch the new bar.
                barObserver?.disconnect();
                observedBarSize = null;
                barObserver?.observe(bar);
                place();
            }

            if (opener) {
                opener.setAttribute('aria-controls', panel.id);
                opener.setAttribute('aria-expanded', isOpen() ? 'true' : 'false');
                opener.addEventListener('click', () => (isOpen() ? close() : open()));
            }

            mount.dataset.ompSearchReady = 'true';
        };

        bindMount(mount);

        if (typeof MutationObserver === 'function') {
            let rebindTimer = 0;
            new MutationObserver(() => {
                window.clearTimeout(rebindTimer);
                rebindTimer = window.setTimeout(() => {
                    const next = document.querySelector('[data-omp-search]');
                    if (next && next !== mount) {
                        bindMount(next);
                    }
                }, 0);
            }).observe(document.body, { childList: true, subtree: true });
        }

        // Ctrl+Shift+F (Cmd+Shift+F on a Mac): a combination neither Windows nor
        // the browser uses. Ctrl+F stays the browser's unless the user's setting
        // hands it over; even then Ctrl+Shift+F keeps working.
        document.addEventListener('keydown', (event) => {
            const isF = (event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'f';
            if (isF && (event.shiftKey || ctrlFOverridden)) {
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
    };

    // A host that renders the bar after parsing (a Blazor app without
    // prerendering) gets its mount bound when it appears.
    const start = () => {
        if (document.querySelector('[data-omp-search]') || typeof MutationObserver !== 'function') {
            init();
            return;
        }

        const waiting = new MutationObserver(() => {
            if (document.querySelector('[data-omp-search]')) {
                waiting.disconnect();
                init();
            }
        });
        waiting.observe(document.body, { childList: true, subtree: true });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
