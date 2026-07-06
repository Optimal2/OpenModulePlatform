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

            Array.from(table.tHead?.querySelectorAll('th[data-sort-type]') || []).forEach((header) => {
                const button = header.querySelector('button[type="button"]');
                if (!button) {
                    return;
                }

                header.setAttribute('aria-sort', 'none');
                button.addEventListener('click', () => {
                    const currentDirection = header.dataset.sortDirection === 'ascending'
                        ? 'descending'
                        : 'ascending';

                    sortTableRows(table, tbody, header.cellIndex, header.dataset.sortType || 'text', currentDirection);

                    Array.from(table.tHead.querySelectorAll('th[data-sort-type]')).forEach((sortableHeader) => {
                        sortableHeader.dataset.sortDirection = '';
                        sortableHeader.setAttribute('aria-sort', sortableHeader === header ? currentDirection : 'none');
                    });

                    header.dataset.sortDirection = currentDirection;
                });
            });
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

    function initAll() {
        initAppHeaderOffset();
        document.querySelectorAll('[data-portal-entries-root]').forEach(initPortalEntries);
        initSortableLists(document);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
