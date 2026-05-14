// File: OpenModulePlatform.Portal/wwwroot/js/site.js
(() => {
    'use strict';

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
        }).catch(() => null);
    }

    function initAll() {
        document.querySelectorAll('[data-portal-entries-root]').forEach(initPortalEntries);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }
})();
