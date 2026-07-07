// File: OpenModulePlatform.Portal/wwwroot/js/site.js
(() => {
    'use strict';

    function initAppHeaderOffset() {
        const header = document.querySelector('.app-header');
        if (!header) {
            return;
        }

        const update = () => {
            document.documentElement.style.setProperty('--app-header-height', `${Math.ceil(header.getBoundingClientRect().height)}px`);
        };

        update();
        window.addEventListener('resize', update);
        if (window.ResizeObserver) {
            new ResizeObserver(update).observe(header);
        }
    }

    function initPortalEntries(root) {
        const list = root.querySelector('[data-portal-pinned-list]');
        const form = root.querySelector('[data-portal-entry-sort-form]');
        if (!list || !form) {
            return;
        }

        let dragged = null;

        list.querySelectorAll('[data-portal-entry-card]').forEach((card) => {
            card.addEventListener('dragstart', (event) => {
                dragged = card;
                card.classList.add('is-dragging');
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', card.getAttribute('data-portal-entry-id') || '');
            });

            card.addEventListener('dragend', () => {
                card.classList.remove('is-dragging');
                dragged = null;
                savePinnedOrder(list, form);
            });
        });

        list.addEventListener('dragover', (event) => {
            if (!dragged) {
                return;
            }

            event.preventDefault();
            const afterElement = getDragAfterElement(list, event.clientY);
            if (!afterElement) {
                list.appendChild(dragged);
            } else if (afterElement !== dragged) {
                list.insertBefore(dragged, afterElement);
            }
        });
    }

    function getDragAfterElement(list, y) {
        const cards = Array.from(list.querySelectorAll('[data-portal-entry-card]:not(.is-dragging)'));
        return cards.reduce((closest, child) => {
            const box = child.getBoundingClientRect();
            const offset = y - box.top - (box.height / 2);
            if (offset < 0 && offset > closest.offset) {
                return { offset, element: child };
            }

            return closest;
        }, { offset: Number.NEGATIVE_INFINITY, element: null }).element;
    }

    function savePinnedOrder(list, form) {
        const token = form.querySelector('input[name="__RequestVerificationToken"]');
        const ids = Array.from(list.querySelectorAll('[data-portal-entry-card]'))
            .map((card) => card.getAttribute('data-portal-entry-id'))
            .filter(Boolean);
        const body = new FormData();
        body.append('orderedPortalEntryIds', ids.join(','));
        if (token) {
            body.append('__RequestVerificationToken', token.value);
        }

        fetch(form.action, {
            method: 'POST',
            body,
            credentials: 'same-origin',
            headers: {
                'X-Requested-With': 'XMLHttpRequest'
            }
        }).catch((error) => {
            console.warn('Unable to save portal entry order.', error);
        });
    }

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

    function sortTableRows(table, tbody, columnIndex, sortType, direction) {
        const multiplier = direction === 'descending' ? -1 : 1;
        const rows = Array.from(tbody.rows).map((row, index) => ({ row, index }));

        rows.sort((left, right) => {
            const comparison = compareSortableValues(
                getSortableCellValue(left.row.cells[columnIndex], sortType),
                getSortableCellValue(right.row.cells[columnIndex], sortType),
                sortType);

            return comparison === 0
                ? left.index - right.index
                : comparison * multiplier;
        });

        const fragment = document.createDocumentFragment();
        rows.forEach(({ row }) => fragment.appendChild(row));
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
            const groupKey = input.closest('[data-filter-group]')?.getAttribute('data-filter-group') || input.dataset.filterColumn;
            if (!groups.has(groupKey)) {
                groups.set(groupKey, []);
            }

            groups.get(groupKey).push(input);
        });

        const rows = Array.from(controller.tbody.rows);
        const limit = controller.pageSize > 0 ? controller.visibleLimit : Number.POSITIVE_INFINITY;
        let matchingCount = 0;
        let shownCount = 0;

        rows.forEach((row) => {
            const matchesSearch = !controller.searchTerm
                || row.textContent.toLocaleLowerCase().includes(controller.searchTerm);
            const matches = matchesSearch && Array.from(groups.values()).every((groupInputs) =>
                groupInputs.some((input) => rowMatchesFilter(row, input)));

            let show = false;
            if (matches) {
                show = matchingCount < limit;
                matchingCount += 1;
            }

            row.hidden = !show;
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
                .replace('{1}', String(rows.length));
        }

        if (controller.showMoreButton) {
            controller.showMoreButton.hidden = matchingCount <= shownCount;
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
            controller.filterInputs = Array.from(filter.querySelectorAll('input[type="checkbox"][data-filter-column]'));

            controller.filterInputs.forEach((input) => input.addEventListener('change', () => {
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

        root.querySelectorAll('table[data-page-size]').forEach((table) => {
            getListController(table.id);
        });

        root.querySelectorAll('[data-list-count]').forEach((element) => {
            getListController(element.getAttribute('data-list-count'));
        });
    }

    function rowMatchesFilter(row, input) {
        const columnIndex = Number.parseInt(input.dataset.filterColumn, 10);
        const cell = row.cells[columnIndex];
        const rawValue = (cell?.getAttribute('data-sort-value') || cell?.textContent || '').trim();

        if (input.dataset.filterEquals !== undefined) {
            return rawValue === input.dataset.filterEquals;
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

    function initAll() {
        initAppHeaderOffset();
        document.querySelectorAll('[data-portal-entries-root]').forEach(initPortalEntries);
        initSortableLists(document);
        initListFilters(document);
        initListEnhancements(document);
        listControllers.forEach((controller) => {
            refreshListController(controller);
            if (controller.viewport) {
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
