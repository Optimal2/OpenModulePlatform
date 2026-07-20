// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-lists.js
// Shared OMP list component: sortable columns, combinable filters, search,
// row counter, client-side paging, viewport height lock, follow rows,
// resizable columns, truncated messages with popovers, and info badges.
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
            if (row.hasAttribute('data-list-follow') && groups.length > 0) {
                groups[groups.length - 1].push(row);
            } else {
                groups.push([row]);
            }
        });

        return groups;
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
            const normalized = rawValue.replace(/\s+/g, '').replace(',', '.');
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

        rowGroups.forEach((rowGroup) => {
            const row = rowGroup[0];
            const matchesSearch = !controller.searchTerm
                || `${row.getAttribute('data-search') || ''} ${row.textContent}`.toLocaleLowerCase().includes(controller.searchTerm);
            const matches = matchesSearch && Array.from(groups.values()).every((groupInputs) =>
                groupInputs.some((input) => rowMatchesFilter(row, input)));

            let show = false;
            if (matches) {
                show = matchingCount < limit;
                matchingCount += 1;
            }

            rowGroup.forEach((groupRow) => {
                groupRow.hidden = !show;
            });
            if (show) {
                shownCount += 1;
            }
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

        controller.table.dispatchEvent(new CustomEvent('sortable-list:updated', { bubbles: true }));
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
        popover.textContent = text;
        document.body.appendChild(popover);

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
            message.addEventListener('click', (event) => {
                const isOwnPopover = activeInfoBadge === message;
                if (!isOwnPopover && message.scrollWidth <= message.clientWidth) {
                    return;
                }

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
            const applyPercents = (percents) => {
                const cols = ensureCols();
                cols.forEach((col, index) => {
                    col.style.width = `${percents[index]}%`;
                });
                table.style.tableLayout = 'fixed';
                table.style.width = '100%';
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
                storeWidths(null);
                markListMessageTruncation();
            };

            const storedWidths = readStoredWidths();
            if (storedWidths) {
                applyPercents(storedWidths);
            }

            const minColumnWidth = 60;

            Array.from(headerRow.cells).forEach((cell, index) => {
                // The last column has no right-hand neighbour to trade space with.
                if (index >= columnCount - 1) {
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
                    if (percents) {
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

    function initAll() {
        initSortableLists(document);
        initListFilters(document);
        initListEnhancements(document);
        initColumnResize(document);
        initInfoBadges(document);
        initListMessages(document);
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
})();
