// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-lists.js
// Shared OMP list component: sortable columns, combinable filters, search,
// row counter, client-side paging, row selection with bulk actions, viewport
// height lock, follow rows, resizable columns, truncated messages with
// popovers, and info badges.
// Markup conventions are opt-in per table; see the Portal admin pages for examples.
(() => {
    'use strict';

    function initSortableLists(root) {
        root.querySelectorAll('[data-sortable-list]').forEach((table) => {
            if (table.dataset.sortableListInitialized === 'true') {
                return;
            }

            const tbody = table.tBodies[0];
            if (!tbody) {
                return;
            }

            table.dataset.sortableListInitialized = 'true';

            const applySort = (header, direction) => {
                sortTableRows(table, tbody, header.cellIndex, header.dataset.sortType || 'text', direction);

                Array.from(table.tHead.querySelectorAll('th[data-sort-type]')).forEach((sortableHeader) => {
                    sortableHeader.dataset.sortDirection = '';
                    sortableHeader.setAttribute('aria-sort', sortableHeader === header ? direction : 'none');
                });

                header.dataset.sortDirection = direction;
            };

            let defaultHeader = null;
            Array.from(table.tHead?.querySelectorAll('th[data-sort-type]') || []).forEach((header) => {
                const button = header.querySelector('button[type="button"]');
                if (!button) {
                    return;
                }

                header.setAttribute('aria-sort', 'none');
                button.addEventListener('click', () => {
                    applySort(header, header.dataset.sortDirection === 'ascending' ? 'descending' : 'ascending');
                });

                if (header.dataset.sortDefault) {
                    defaultHeader = header;
                }
            });

            if (defaultHeader) {
                applySort(defaultHeader, defaultHeader.dataset.sortDefault === 'descending' ? 'descending' : 'ascending');
            }
        });
    }

    function getListRowGroups(tbody) {
        const groups = [];
        Array.from(tbody.rows).forEach((row) => {
            // Ignored rows (e.g. server-rendered empty-state rows) are left
            // alone entirely: never sorted, hidden, counted or searched.
            if (row.hasAttribute('data-list-ignore')) {
                return;
            }

            if (row.hasAttribute('data-list-follow') && groups.length > 0) {
                groups[groups.length - 1].push(row);
            } else {
                groups.push([row]);
            }
        });

        return groups;
    }

    // Deep-search hits highlight matched text like the browser's find-on-page.
    // Marks are rebuilt on every refresh, so previous marks must be unwrapped
    // first or repeated searches would nest and duplicate them.
    function clearDeepMarks(root) {
        root.querySelectorAll('mark.list-deep-mark').forEach((mark) => {
            const parent = mark.parentNode;
            mark.replaceWith(document.createTextNode(mark.textContent));
            parent.normalize();
        });
    }

    function markDeepMatches(root, term) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode: (node) => node.parentElement?.closest('button, select, textarea, script, style, template')
                ? NodeFilter.FILTER_REJECT
                : NodeFilter.FILTER_ACCEPT
        });
        const textNodes = [];
        while (walker.nextNode()) {
            textNodes.push(walker.currentNode);
        }

        textNodes.forEach((node) => {
            const text = node.nodeValue;
            const lower = text.toLocaleLowerCase();
            let index = lower.indexOf(term);
            if (index === -1) {
                return;
            }

            const fragment = document.createDocumentFragment();
            let consumed = 0;
            while (index !== -1) {
                if (index > consumed) {
                    fragment.appendChild(document.createTextNode(text.slice(consumed, index)));
                }

                const mark = document.createElement('mark');
                mark.className = 'list-deep-mark';
                mark.textContent = text.slice(index, index + term.length);
                fragment.appendChild(mark);
                consumed = index + term.length;
                index = lower.indexOf(term, consumed);
            }

            if (consumed < text.length) {
                fragment.appendChild(document.createTextNode(text.slice(consumed)));
            }

            node.replaceWith(fragment);
        });
    }

    function sortTableRows(table, tbody, columnIndex, sortType, direction) {
        const multiplier = direction === 'descending' ? -1 : 1;
        const groups = getListRowGroups(tbody).map((group, index) => ({ group, index }));

        groups.sort((left, right) => {
            const comparison = compareSortableValues(
                getSortableCellValue(left.group[0].cells[columnIndex], sortType),
                getSortableCellValue(right.group[0].cells[columnIndex], sortType),
                sortType);

            return comparison === 0
                ? left.index - right.index
                : comparison * multiplier;
        });

        const fragment = document.createDocumentFragment();
        groups.forEach(({ group }) => group.forEach((row) => fragment.appendChild(row)));
        tbody.appendChild(fragment);
        table.dispatchEvent(new CustomEvent('sortable-list:sorted', { bubbles: true }));
    }

    function getSortableCellValue(cell, sortType) {
        const rawValue = (cell?.getAttribute('data-sort-value') || cell?.textContent || '').trim();

        if (sortType === 'number') {
            const normalized = rawValue.replace(/\s+/g, '').replace(/,/g, '.');
            const parsed = Number.parseFloat(normalized);
            return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY;
        }

        if (sortType === 'date') {
            const parsed = Date.parse(rawValue);
            return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY;
        }

        return rawValue.toLocaleLowerCase();
    }

    function compareSortableValues(left, right, sortType) {
        if (sortType === 'text') {
            return left.localeCompare(right, undefined, { numeric: true, sensitivity: 'base' });
        }

        if (left < right) {
            return -1;
        }

        if (left > right) {
            return 1;
        }

        return 0;
    }

    const listControllers = new Map();

    function getListController(tableId) {
        const table = document.getElementById(tableId || '');
        const tbody = table?.tBodies[0];
        if (!tbody) {
            return null;
        }

        let controller = listControllers.get(table);
        if (controller) {
            return controller;
        }

        controller = {
            table,
            tbody,
            searchTerm: '',
            filterElement: null,
            filterBadge: null,
            filterInputs: [],
            pageSize: Number.parseInt(table.dataset.pageSize || '', 10) || 0,
            visibleLimit: 0,
            countNote: document.querySelector(`[data-list-count="${tableId}"]`),
            showMoreButton: document.querySelector(`[data-list-show-more="${tableId}"]`),
            emptyNote: document.querySelector(`[data-list-empty="${tableId}"]`),
            deepToggle: document.querySelector(`[data-list-search-deep="${tableId}"]`),
            viewport: null
        };
        controller.visibleLimit = controller.pageSize;

        const viewport = document.createElement('div');
        viewport.className = 'list-viewport';
        table.parentNode.insertBefore(viewport, table);
        viewport.appendChild(table);
        controller.viewport = viewport;

        table.addEventListener('sortable-list:sorted', () => refreshListController(controller));
        controller.showMoreButton?.addEventListener('click', () => {
            controller.visibleLimit += controller.pageSize;
            refreshListController(controller);
        });

        listControllers.set(table, controller);
        return controller;
    }

    function refreshListController(controller) {
        const groups = new Map();
        controller.filterInputs.filter((input) => input.checked).forEach((input) => {
            const groupKey = input.closest('[data-filter-group]')?.getAttribute('data-filter-group')
                || input.dataset.filterColumn
                || input.dataset.filterRowAttr;
            if (!groups.has(groupKey)) {
                groups.set(groupKey, []);
            }

            groups.get(groupKey).push(input);
        });

        const rowGroups = getListRowGroups(controller.tbody);
        const limit = controller.pageSize > 0 ? controller.visibleLimit : Number.POSITIVE_INFINITY;
        let matchingCount = 0;
        let shownCount = 0;

        // Deep search extends the term to the nested lists inside follow rows:
        // a group whose own row misses still matches when a nested tbody row
        // hits, and the nested rows are spotlighted/dimmed instead of hidden
        // so the hit is visible in its context once the group is expanded.
        const deepActive = !!(controller.deepToggle?.checked && controller.searchTerm);
        const getDeepRows = (rowGroup) => rowGroup
            .slice(1)
            .flatMap((followRow) => Array.from(followRow.querySelectorAll('tbody tr')))
            .filter((nestedRow) => !nestedRow.hasAttribute('data-list-ignore'));

        rowGroups.forEach((rowGroup) => {
            const row = rowGroup[0];
            const deepRows = getDeepRows(rowGroup);
            const deepHit = (nestedRow) => nestedRow.textContent.toLocaleLowerCase().includes(controller.searchTerm);
            const matchesSearch = !controller.searchTerm
                || `${row.getAttribute('data-search') || ''} ${row.textContent}`.toLocaleLowerCase().includes(controller.searchTerm)
                || (deepActive && deepRows.some(deepHit));
            const matches = matchesSearch && Array.from(groups.values()).every((groupInputs) =>
                groupInputs.some((input) => rowMatchesFilter(row, input)));

            // Pinned rows (rows being edited or otherwise interacted with)
            // always stay visible, no matter what the search or filters say.
            // The contract is the explicit value "true": Razor conditional
            // attributes can render an empty value instead of omitting, and
            // an empty value must not pin.
            const isPinnedRow = (candidate) => candidate.getAttribute('data-list-pinned') === 'true';
            const pinned = rowGroup.some(isPinnedRow);

            let show = false;
            if (pinned) {
                show = true;
                matchingCount += 1;
            } else if (matches) {
                show = matchingCount < limit;
                matchingCount += 1;
            }

            rowGroup.forEach((groupRow) => {
                groupRow.hidden = !show;
            });
            if (show) {
                shownCount += 1;
            }

            deepRows.forEach((nestedRow) => {
                const spotlight = show && deepActive && !isPinnedRow(nestedRow);
                clearDeepMarks(nestedRow);
                nestedRow.classList.remove('list-deep-miss');
                const hit = spotlight && deepHit(nestedRow);
                nestedRow.classList.toggle('list-deep-hit', hit);
                if (hit) {
                    markDeepMatches(nestedRow, controller.searchTerm);
                }
            });
        });

        const activeFilterCount = Array.from(groups.values()).reduce((sum, groupInputs) => sum + groupInputs.length, 0);
        if (controller.filterElement) {
            controller.filterElement.classList.toggle('list-filter--active', activeFilterCount > 0);
        }

        if (controller.filterBadge) {
            controller.filterBadge.hidden = activeFilterCount === 0;
            controller.filterBadge.textContent = String(activeFilterCount);
        }

        if (controller.countNote) {
            const template = controller.countNote.dataset.template || '{0} / {1}';
            controller.countNote.textContent = template
                .replace('{0}', String(shownCount))
                .replace('{1}', String(rowGroups.length));
        }

        if (controller.showMoreButton) {
            controller.showMoreButton.hidden = matchingCount <= shownCount;
        }

        // Release the load-time height lock. initAll() freezes the viewport at its initial height
        // (see the minHeight write there) so the page does not jitter while the lists render. That
        // lock was never lifted, so filtering 22 rows down to 0 left the surface as tall as it was
        // when the page loaded -- the list shrank, the box did not. Clearing it here means the lock
        // only survives until the row set actually changes, which is exactly as long as it is useful.
        if (controller.viewport && controller.viewport.style.minHeight) {
            controller.viewport.style.minHeight = '';
        }

        if (controller.emptyNote) {
            controller.emptyNote.hidden = matchingCount > 0;
        }

        // While this table has a local search term, an outer list's deep-search
        // spotlight inside it is suppressed via CSS: the local search is the
        // user's active intent, so dimming its results would misread as
        // "wrong row". Clearing the local term brings the spotlight back.
        controller.table.classList.toggle('list-locally-searched', !!controller.searchTerm);

        controller.table.dispatchEvent(new CustomEvent('sortable-list:updated', { bubbles: true }));
    }

    function initListSelection(root) {
        root.querySelectorAll('table[data-list-selection]').forEach((table) => {
            if (table.dataset.listSelectionInitialized === 'true') {
                return;
            }

            const tbody = table.tBodies[0];
            const selectAll = table.tHead?.querySelector('input[data-list-select-all]');
            if (!tbody || !selectAll) {
                return;
            }

            table.dataset.listSelectionInitialized = 'true';
            const selectionKey = table.dataset.listSelection || table.id;
            const actions = selectionKey
                ? document.querySelector(`[data-list-selection-actions="${CSS.escape(selectionKey)}"]`)
                : null;
            const selectedCount = actions?.querySelector('[data-list-selected-count]');
            const actionButtons = Array.from(actions?.querySelectorAll('[data-list-selection-action]') || []);

            const rowCheckboxes = () => Array.from(
                tbody.querySelectorAll(':scope > tr > td input[data-list-select-row]'));
            const isSelectable = (checkbox) => {
                const row = checkbox.closest('tr');
                return !checkbox.disabled && !!row && !row.hidden;
            };

            const refreshSelection = () => {
                const checkboxes = rowCheckboxes();
                checkboxes
                    .filter((checkbox) => !isSelectable(checkbox))
                    .forEach((checkbox) => {
                        checkbox.checked = false;
                    });

                const selectable = checkboxes.filter(isSelectable);
                const selected = selectable.filter((checkbox) => checkbox.checked);

                selectAll.disabled = selectable.length === 0;
                selectAll.checked = selectable.length > 0 && selected.length === selectable.length;
                selectAll.indeterminate = selected.length > 0 && selected.length < selectable.length;

                checkboxes.forEach((checkbox) => {
                    checkbox.closest('tr')?.classList.toggle('list-row-selected', checkbox.checked);
                });

                if (selectedCount) {
                    const template = selectedCount.dataset.template || '{0} selected';
                    selectedCount.textContent = template.replace('{0}', String(selected.length));
                }

                actionButtons.forEach((button) => {
                    button.disabled = selected.length === 0;
                });

                table.dispatchEvent(new CustomEvent('sortable-list:selection-changed', {
                    bubbles: true,
                    detail: { selectedCount: selected.length, totalCount: selectable.length }
                }));
            };

            table.addEventListener('change', (event) => {
                const checkbox = event.target;
                if (!(checkbox instanceof HTMLInputElement) || checkbox.type !== 'checkbox') {
                    return;
                }

                if (checkbox.matches('[data-list-select-all]')) {
                    rowCheckboxes()
                        .filter(isSelectable)
                        .forEach((rowCheckbox) => {
                            rowCheckbox.checked = checkbox.checked;
                        });
                } else if (!checkbox.matches('[data-list-select-row]')) {
                    return;
                }

                refreshSelection();
            });

            table.addEventListener('sortable-list:updated', refreshSelection);
            refreshSelection();
        });
    }

    function initListFilters(root) {
        root.querySelectorAll('[data-list-filter]').forEach((filter) => {
            if (filter.dataset.listFilterInitialized === 'true') {
                return;
            }

            const controller = getListController(filter.dataset.listFilter);
            if (!controller) {
                return;
            }

            filter.dataset.listFilterInitialized = 'true';

            controller.filterElement = filter;
            controller.filterBadge = filter.querySelector('[data-filter-count]');
            const filterInputs = Array.from(filter.querySelectorAll('input[type="checkbox"][data-filter-column]'));
            controller.filterInputs.push(...filterInputs);

            filterInputs.forEach((input) => input.addEventListener('change', () => {
                controller.visibleLimit = controller.pageSize;
                refreshListController(controller);
            }));

            filter.querySelector('[data-filter-clear]')?.addEventListener('click', () => {
                controller.filterInputs.forEach((input) => {
                    input.checked = false;
                });
                controller.visibleLimit = controller.pageSize;
                refreshListController(controller);
            });

            document.addEventListener('click', (event) => {
                if (filter.open && !filter.contains(event.target)) {
                    filter.open = false;
                }
            });

            document.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') {
                    filter.open = false;
                }
            });
        });
    }

    function initListEnhancements(root) {
        root.querySelectorAll('[data-list-search]').forEach((input) => {
            if (input.dataset.listSearchInitialized === 'true') {
                return;
            }

            const controller = getListController(input.dataset.listSearch);
            if (!controller) {
                return;
            }

            input.dataset.listSearchInitialized = 'true';
            input.addEventListener('input', () => {
                controller.searchTerm = input.value.trim().toLocaleLowerCase();
                controller.visibleLimit = controller.pageSize;
                refreshListController(controller);
            });
        });

        // A one-shot copy of another list's search term into this list's search
        // box (e.g. carrying the main search down into a sublist). After the
        // copy it is plain text: editing or clearing it has no further link to
        // the source. Clicking with an empty source clears the target.
        root.querySelectorAll('[data-list-search-inherit]').forEach((button) => {
            if (button.dataset.listSearchInheritInitialized === 'true') {
                return;
            }

            const target = document.querySelector(`[data-list-search="${button.dataset.listSearchInherit}"]`);
            const source = document.querySelector(`[data-list-search="${button.dataset.listSearchInheritFrom}"]`);
            if (!target || !source) {
                return;
            }

            button.dataset.listSearchInheritInitialized = 'true';
            button.addEventListener('click', () => {
                target.value = source.value;
                target.dispatchEvent(new Event('input', { bubbles: true }));
                target.focus();
            });
        });

        root.querySelectorAll('input[type="checkbox"][data-list-search-deep]').forEach((toggle) => {
            if (toggle.dataset.listSearchDeepInitialized === 'true') {
                return;
            }

            const controller = getListController(toggle.dataset.listSearchDeep);
            if (!controller) {
                return;
            }

            toggle.dataset.listSearchDeepInitialized = 'true';
            controller.deepToggle = toggle;
            toggle.addEventListener('change', () => {
                controller.visibleLimit = controller.pageSize;
                refreshListController(controller);
            });
        });

        root.querySelectorAll('input[type="checkbox"][data-list-toggle]').forEach((toggle) => {
            if (toggle.dataset.listToggleInitialized === 'true') {
                return;
            }

            const controller = getListController(toggle.dataset.listToggle);
            if (!controller) {
                return;
            }

            toggle.dataset.listToggleInitialized = 'true';
            controller.filterInputs.push(toggle);
            toggle.addEventListener('change', () => {
                controller.visibleLimit = controller.pageSize;
                refreshListController(controller);
            });
        });

        root.querySelectorAll('table[data-page-size]').forEach((table) => {
            getListController(table.id);
        });

        root.querySelectorAll('[data-list-count]').forEach((element) => {
            getListController(element.getAttribute('data-list-count'));
        });
    }

    function rowMatchesFilter(row, input) {
        let rawValue;
        if (input.dataset.filterRowAttr !== undefined) {
            rawValue = (row.getAttribute(`data-${input.dataset.filterRowAttr}`) || '').trim();
        } else {
            const columnIndex = Number.parseInt(input.dataset.filterColumn, 10);
            const cell = row.cells[columnIndex];
            rawValue = (cell?.getAttribute('data-sort-value') || cell?.textContent || '').trim();
        }

        if (input.dataset.filterEquals !== undefined) {
            return rawValue === input.dataset.filterEquals;
        }

        if (input.dataset.filterIncludes !== undefined) {
            return rawValue.split(';')
                .map((token) => token.trim())
                .filter((token) => token.length > 0)
                .includes(input.dataset.filterIncludes);
        }

        const parsedDate = Date.parse(rawValue);
        if (!Number.isFinite(parsedDate)) {
            return false;
        }

        if (input.dataset.filterBefore !== undefined) {
            return parsedDate < Date.parse(input.dataset.filterBefore);
        }

        if (input.dataset.filterAfter !== undefined) {
            return parsedDate > Date.parse(input.dataset.filterAfter);
        }

        if (input.dataset.filterMaxAgeDays !== undefined) {
            return parsedDate >= Date.now() - (Number.parseFloat(input.dataset.filterMaxAgeDays) * 86400000);
        }

        if (input.dataset.filterMinAgeDays !== undefined) {
            return parsedDate < Date.now() - (Number.parseFloat(input.dataset.filterMinAgeDays) * 86400000);
        }

        return false;
    }

    let activeInfoPopover = null;
    let activeInfoBadge = null;

    function closeInfoPopover() {
        activeInfoPopover?.remove();
        activeInfoBadge?.classList.remove('is-open');
        activeInfoPopover = null;
        activeInfoBadge = null;
    }

    function openInfoPopover(badge, text) {
        closeInfoPopover();

        const popover = document.createElement('div');
        popover.className = 'info-popover';
        // A click inside the popover is not a click outside it: only outside
        // clicks (the document listener) or the opener itself close it.
        popover.addEventListener('click', (event) => event.stopPropagation());

        const copy = document.createElement('button');
        copy.type = 'button';
        copy.className = 'info-popover__copy';
        copy.title = 'Copy';
        copy.setAttribute('aria-label', 'Copy');
        copy.addEventListener('click', () => {
            const done = () => {
                copy.classList.add('is-copied');
                window.setTimeout(() => copy.classList.remove('is-copied'), 1200);
            };
            if (navigator.clipboard?.writeText) {
                navigator.clipboard.writeText(text).then(done, () => { /* keep quiet */ });
            } else {
                const scratch = document.createElement('textarea');
                scratch.value = text;
                document.body.appendChild(scratch);
                scratch.select();
                try { document.execCommand('copy'); done(); } finally { scratch.remove(); }
            }
        });
        popover.appendChild(copy);

        const body = document.createElement('div');
        body.className = 'info-popover__body';
        body.textContent = text;
        popover.appendChild(body);
        // Inside a modal <dialog> the popover must live in the dialog's
        // top layer - a body-appended element would render beneath it.
        (badge.closest('dialog') || document.body).appendChild(popover);

        const rect = badge.getBoundingClientRect();
        const maxLeft = window.innerWidth - popover.offsetWidth - 8;
        popover.style.top = `${rect.bottom + 6}px`;
        popover.style.left = `${Math.max(8, Math.min(rect.left, maxLeft))}px`;

        badge.classList.add('is-open');
        activeInfoPopover = popover;
        activeInfoBadge = badge;
    }

    function initInfoBadges(root) {
        root.querySelectorAll('.info-badge').forEach((badge) => {
            if (badge.dataset.infoBadgeInitialized === 'true') {
                return;
            }

            const text = (badge.getAttribute('title') || badge.getAttribute('aria-label') || '').trim();
            if (!text) {
                return;
            }

            badge.dataset.infoBadgeInitialized = 'true';
            badge.addEventListener('click', (event) => {
                event.stopPropagation();
                if (activeInfoBadge === badge) {
                    closeInfoPopover();
                } else {
                    openInfoPopover(badge, text);
                }
            });

            badge.addEventListener('keydown', (event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    badge.click();
                }
            });
        });
    }

    function markListMessageTruncation() {
        document.querySelectorAll('.list-message').forEach((message) => {
            message.classList.toggle('list-message--truncated', message.scrollWidth > message.clientWidth);
        });
    }

    function initListMessages(root) {
        root.querySelectorAll('.list-message').forEach((message) => {
            if (message.dataset.listMessageInitialized === 'true') {
                return;
            }

            message.dataset.listMessageInitialized = 'true';
            // Localized by the page via data-list-message-hint on any
            // ancestor; the English text is the neutral fallback.
            message.title = message.closest('[data-list-message-hint]')?.getAttribute('data-list-message-hint')
                || 'Click to show the full message';
            message.addEventListener('click', (event) => {
                const isOwnPopover = activeInfoBadge === message;
                event.stopPropagation();
                if (isOwnPopover) {
                    closeInfoPopover();
                } else {
                    openInfoPopover(message, (message.textContent || '').trim());
                }
            });
        });

        markListMessageTruncation();
    }

    let listMessageResizeTimer = 0;
    window.addEventListener('resize', () => {
        window.clearTimeout(listMessageResizeTimer);
        listMessageResizeTimer = window.setTimeout(markListMessageTruncation, 150);
    });

    function initColumnResize(root) {
        root.querySelectorAll('table[data-sortable-list], table[data-page-size]').forEach((table) => {
            if (!table.id || table.dataset.columnResizeInitialized === 'true') {
                return;
            }

            const headerRow = table.tHead?.rows[0];
            if (!headerRow) {
                return;
            }

            table.dataset.columnResizeInitialized = 'true';
            const columnCount = headerRow.cells.length;
            const storageKey = `omp.list-columns.${table.id}`;

            const readStoredWidths = () => {
                try {
                    const parsed = JSON.parse(window.localStorage.getItem(storageKey) || 'null');
                    return Array.isArray(parsed)
                        && parsed.length === columnCount
                        && parsed.every((value) => Number.isFinite(value) && value > 0 && value <= 100)
                        ? parsed
                        : null;
                } catch (error) {
                    return null;
                }
            };

            const storeWidths = (widths) => {
                try {
                    if (widths === null) {
                        window.localStorage.removeItem(storageKey);
                    } else {
                        window.localStorage.setItem(storageKey, JSON.stringify(widths));
                    }
                } catch (error) {
                    // Storage may be unavailable; resizing still works for the current page.
                }
            };

            const ensureCols = () => {
                let colgroup = table.querySelector(':scope > colgroup');
                if (!colgroup) {
                    colgroup = document.createElement('colgroup');
                    for (let i = 0; i < columnCount; i += 1) {
                        colgroup.appendChild(document.createElement('col'));
                    }

                    table.insertBefore(colgroup, table.firstChild);
                }

                return Array.from(colgroup.children);
            };

            // Column widths are managed as percentages of the table width so the
            // table itself never grows or shrinks; neighbours trade space instead.
            // Hidden cells (collapsed detail columns) produce no boxes, and in
            // fixed layout the visible cells then consume columns strictly in
            // order - so the visible percents go to the leading cols and the
            // rest zero out, or the last real column would map to a phantom
            // col and a blank band would appear at the right edge (measured).
            const applyPercents = (percents) => {
                const cols = ensureCols();
                const cells = Array.from(headerRow.cells);
                const visible = [];
                cells.forEach((cell, index) => {
                    if (getComputedStyle(cell).display !== 'none') {
                        visible.push(index);
                    }
                });
                const visibleTotal = visible.reduce((sum, index) => sum + percents[index], 0);
                cols.forEach((col, index) => {
                    col.style.width = index < visible.length && visibleTotal > 0
                        ? `${(percents[visible[index]] / visibleTotal) * 100}%`
                        : '0%';
                });
                table.style.tableLayout = 'fixed';
                table.style.width = '100%';
                // Lets page-level px caps on .list-message yield to the cell
                // width while the user is managing widths manually.
                table.classList.add('list-columns-resized');
            };

            const currentPixelWidths = () => Array.from(headerRow.cells).map((cell) => cell.getBoundingClientRect().width);

            const toPercents = (pixelWidths) => {
                const total = pixelWidths.reduce((sum, value) => sum + value, 0);
                return total > 0 ? pixelWidths.map((value) => (value / total) * 100) : null;
            };

            const resetWidths = () => {
                table.querySelectorAll(':scope > colgroup > col').forEach((col) => {
                    col.style.removeProperty('width');
                });
                table.style.removeProperty('table-layout');
                table.style.removeProperty('width');
                table.classList.remove('list-columns-resized');
                storeWidths(null);
                markListMessageTruncation();
            };

            // Tables with expandable column groups never persist widths: a
            // stored percent array bakes in detail columns that may be hidden
            // on the next visit, and the fixed layout then reserves blank
            // space for them (collapsed) or squeezes them to zero (expanded).
            // Resizing still works within the current visit; a group toggle
            // resets it (see initColumnGroups).
            const hasColumnGroups = !!table.querySelector('[data-column-expand]');
            if (hasColumnGroups) {
                storeWidths(null);
            }

            table.ompListsResetColumnWidths = resetWidths;

            const storedWidths = hasColumnGroups ? null : readStoredWidths();
            if (storedWidths) {
                applyPercents(storedWidths);
            }

            const minColumnWidth = 60;

            Array.from(headerRow.cells).forEach((cell, index) => {
                // The last column has no right-hand neighbour to trade space with.
                if (index >= columnCount - 1 || cell.matches('.list-selection-cell, [data-list-no-resize]')) {
                    return;
                }

                cell.classList.add('list-column-resize-host');
                const handle = document.createElement('span');
                handle.className = 'list-column-resize';
                handle.setAttribute('aria-hidden', 'true');
                cell.appendChild(handle);

                let startX = 0;
                let startWidths = null;

                handle.addEventListener('click', (event) => event.stopPropagation());

                handle.addEventListener('pointerdown', (event) => {
                    if (!event.isPrimary) {
                        return;
                    }

                    event.preventDefault();
                    event.stopPropagation();
                    startX = event.clientX;
                    startWidths = currentPixelWidths();
                    const startPercents = toPercents(startWidths);
                    if (!startPercents) {
                        startWidths = null;
                        return;
                    }

                    applyPercents(startPercents);
                    handle.setPointerCapture(event.pointerId);
                    handle.classList.add('is-resizing');
                    document.body.classList.add('list-column-resizing');
                });

                handle.addEventListener('pointermove', (event) => {
                    if (!startWidths || !handle.hasPointerCapture(event.pointerId)) {
                        return;
                    }

                    const pairTotal = startWidths[index] + startWidths[index + 1];
                    if (pairTotal < minColumnWidth * 2) {
                        return;
                    }

                    const widths = startWidths.slice();
                    widths[index] = Math.min(
                        pairTotal - minColumnWidth,
                        Math.max(minColumnWidth, startWidths[index] + (event.clientX - startX)));
                    widths[index + 1] = pairTotal - widths[index];

                    const percents = toPercents(widths);
                    if (percents) {
                        applyPercents(percents);
                    }
                });

                const endDrag = (event) => {
                    if (!startWidths || !handle.hasPointerCapture(event.pointerId)) {
                        return;
                    }

                    handle.releasePointerCapture(event.pointerId);
                    handle.classList.remove('is-resizing');
                    document.body.classList.remove('list-column-resizing');
                    startWidths = null;
                    const percents = toPercents(currentPixelWidths());
                    if (percents && !hasColumnGroups) {
                        storeWidths(percents);
                    }

                    markListMessageTruncation();
                };

                handle.addEventListener('pointerup', endDrag);
                handle.addEventListener('pointercancel', endDrag);

                handle.addEventListener('dblclick', (event) => {
                    event.stopPropagation();
                    resetWidths();
                });
            });
        });
    }

    document.addEventListener('click', closeInfoPopover);
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            closeInfoPopover();
        }
    });

    // Column groups: a summary column can reveal pre-rendered detail columns.
    // The server always renders every cell; expansion only toggles a
    // visibility class, so filter column indexes, sorting and host-swap
    // refreshes all see one stable DOM. Markup: the summary <th> carries a
    // <button data-column-expand="key"> and detail cells (<th> and <td>)
    // carry data-column-detail="key". Expanded groups persist per table id
    // in sessionStorage.
    function initColumnGroups(root) {
        root.querySelectorAll('table').forEach((table) => {
            if (!table.querySelector('[data-column-expand]')) {
                return;
            }
            if (table.dataset.columnGroupsInitialized === 'true') {
                return;
            }
            table.dataset.columnGroupsInitialized = 'true';

            const storageKey = table.id ? `omp-list-columns:${table.id}` : null;
            const readOpen = () => {
                if (!storageKey) { return []; }
                try { return JSON.parse(sessionStorage.getItem(storageKey) || '[]'); } catch { return []; }
            };
            const writeOpen = (keys) => {
                if (!storageKey) { return; }
                try { sessionStorage.setItem(storageKey, JSON.stringify(keys)); } catch { /* private mode */ }
            };
            const apply = (key, open) => {
                table.querySelectorAll(`[data-column-detail="${CSS.escape(key)}"]`)
                    .forEach((cell) => cell.classList.toggle('list-column-detail--open', open));
                const button = table.querySelector(`[data-column-expand="${CSS.escape(key)}"]`);
                button?.setAttribute('aria-expanded', open ? 'true' : 'false');
                button?.classList.toggle('is-open', open);
            };

            // Expanding must not reshuffle the columns that are already on
            // screen: before revealing detail columns, the visible columns'
            // current widths are frozen in the colgroup, and the open table
            // is released from the fit-width cap so the new columns extend
            // it to the right (scrolling inside the wrapper) instead of
            // squeezing their neighbours.
            const groupCols = () => {
                const headerCells = Array.from(table.tHead?.rows[0]?.cells || []);
                let colgroup = table.querySelector(':scope > colgroup');
                if (!colgroup) {
                    colgroup = document.createElement('colgroup');
                    headerCells.forEach(() => colgroup.appendChild(document.createElement('col')));
                    table.insertBefore(colgroup, table.firstChild);
                }
                return { headerCells, cols: Array.from(colgroup.children) };
            };
            const freezeVisibleColumns = () => {
                const { headerCells, cols } = groupCols();
                const widths = headerCells.map((cell) => cell.getBoundingClientRect().width);
                headerCells.forEach((cell, index) => {
                    if (!cols[index]) { return; }
                    const hidden = cell.hasAttribute('data-column-detail')
                        && !cell.classList.contains('list-column-detail--open');
                    cols[index].style.width = hidden ? '' : `${widths[index]}px`;
                });
            };
            const releaseColumns = () => {
                groupCols().cols.forEach((col) => col.style.removeProperty('width'));
            };
            const syncOpenState = (anyOpen) => {
                table.classList.toggle('list-column-group-open', anyOpen);
            };

            let open = readOpen();
            open.forEach((key) => apply(key, true));
            // Restored-open tables render at natural widths (no freeze: there
            // is no previous on-screen state to keep stable).
            syncOpenState(open.length > 0);

            table.querySelectorAll('[data-column-expand]').forEach((button) => {
                button.setAttribute('aria-expanded', open.includes(button.dataset.columnExpand) ? 'true' : 'false');
                button.addEventListener('click', () => {
                    const key = button.dataset.columnExpand;
                    const isOpen = !open.includes(key);
                    const wasOpen = open.length > 0;
                    open = isOpen ? [...open, key] : open.filter((other) => other !== key);
                    writeOpen(open);
                    // Any manually resized widths (fixed layout + per-column
                    // percents) describe the wrong column set now.
                    table.ompListsResetColumnWidths?.();
                    if (isOpen && !wasOpen) {
                        freezeVisibleColumns();
                    }
                    apply(key, isOpen);
                    if (open.length === 0) {
                        releaseColumns();
                    }
                    syncOpenState(open.length > 0);
                    // Revealed or hidden detail columns may sit inside a
                    // column band; the band edges must follow.
                    table.ompListsRefreshColumnBands?.();
                });
            });
        });
    }

    // Column bands: a thin colored line above header columns that belong
    // together conceptually (declared with data-column-band on the th).
    // Purely presentational and fixed by the page - the user cannot change
    // it. The band label (data-column-band-label on any member) is always
    // visible in the band's start column. Colors come from
    // data-column-band-color or a fixed palette by order of appearance.
    const COLUMN_BAND_PALETTE = ['#4c8dd6', '#58a668', '#d6a54c', '#9273d1', '#4ca8a3'];

    function initColumnBands(root) {
        root.querySelectorAll('table').forEach((table) => {
            if (!table.querySelector('[data-column-band]') || table.dataset.columnBandsInitialized === 'true') {
                return;
            }
            table.dataset.columnBandsInitialized = 'true';
            // The class buys the header row a top strip where the band line
            // and the label chip live, above the column titles.
            table.classList.add('list-has-column-bands');

            const headerCells = () => Array.from(table.tHead?.rows[0]?.cells || []);

            const bandColors = new Map();
            const bandLabels = new Map();
            headerCells().forEach((cell) => {
                const band = cell.dataset.columnBand;
                if (!band) {
                    return;
                }
                if (!bandColors.has(band)) {
                    bandColors.set(band, cell.dataset.columnBandColor
                        || COLUMN_BAND_PALETTE[bandColors.size % COLUMN_BAND_PALETTE.length]);
                }
                if (!bandLabels.has(band) && cell.dataset.columnBandLabel) {
                    bandLabels.set(band, cell.dataset.columnBandLabel);
                }
                cell.style.setProperty('--column-band-color', bandColors.get(band));
            });

            const refresh = () => {
                const cells = headerCells();
                cells.forEach((cell) => cell.classList.remove('list-column-band-start', 'list-column-band-end'));
                const visible = cells.filter((cell) => getComputedStyle(cell).display !== 'none');
                let runBand = null;
                let runLast = null;
                const starts = new Map();
                visible.forEach((cell) => {
                    const band = cell.dataset.columnBand || null;
                    if (band !== runBand) {
                        runLast?.classList.add('list-column-band-end');
                        runBand = band;
                        runLast = null;
                        if (band) {
                            cell.classList.add('list-column-band-start');
                            starts.set(band, cell);
                        }
                    }
                    if (band) {
                        runLast = cell;
                    }
                });
                runLast?.classList.add('list-column-band-end');
                // The label chip lives in the band's current start cell.
                starts.forEach((startCell, band) => {
                    const label = bandLabels.get(band);
                    if (!label) {
                        return;
                    }
                    let chip = table.querySelector(`.list-column-band-label[data-column-band-for="${CSS.escape(band)}"]`);
                    if (!chip) {
                        chip = document.createElement('span');
                        chip.className = 'list-column-band-label';
                        chip.setAttribute('data-column-band-for', band);
                        chip.setAttribute('aria-hidden', 'true');
                        chip.textContent = label;
                    }
                    if (chip.parentElement !== startCell) {
                        startCell.appendChild(chip);
                    }
                });
            };

            refresh();
            table.ompListsRefreshColumnBands = refresh;
        });
    }

    function initAll() {
        initSortableLists(document);
        initListFilters(document);
        initListEnhancements(document);
        initListSelection(document);
        initColumnResize(document);
        initInfoBadges(document);
        initListMessages(document);
        initColumnGroups(document);
        initColumnBands(document);
        listControllers.forEach((controller) => {
            refreshListController(controller);
            if (controller.viewport && controller.viewport.offsetHeight > 0) {
                controller.viewport.style.minHeight = `${controller.viewport.offsetHeight}px`;
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }

    // Pages that swap list markup in dynamically (e.g. push-triggered
    // refreshes) call this to wire the new elements; every init function
    // guards against double initialization, so re-running is safe.
    window.ompLists = Object.assign(window.ompLists || {}, { init: initAll });
})();
