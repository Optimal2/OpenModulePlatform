// File: OpenModulePlatform.Portal/wwwroot/js/admin-navigation-links.js
(() => {
    'use strict';

    // Flat drag-to-reorder for the link rows: dragging a handle moves
    // its row; dropping in a new position submits the new id order.
    const list = document.querySelector("[data-linkbox-list]");
    const orderForm = document.getElementById("link-reorder-form");
    const orderInput = orderForm?.querySelector("[data-linkbox-order]");
    if (!list || !orderForm || !orderInput) {
        return;
    }

    const idOrder = () =>
        Array.from(list.querySelectorAll("[data-linkbox-row]")).map(row => row.dataset.linkId).join(",");

    let draggedRow = null;
    let startOrder = "";

    list.querySelectorAll("[data-linkbox-drag-handle]").forEach(handle => {
        const row = handle.closest("[data-linkbox-row]");
        handle.addEventListener("dragstart", event => {
            draggedRow = row;
            startOrder = idOrder();
            row.classList.add("linkbox-editor-row--dragging");
            event.dataTransfer.effectAllowed = "move";
            event.dataTransfer.setData("text/plain", row.dataset.linkId);
            event.dataTransfer.setDragImage(row, 12, 12);
        });

        handle.addEventListener("dragend", () => {
            row.classList.remove("linkbox-editor-row--dragging");
            if (draggedRow && idOrder() !== startOrder) {
                orderInput.value = idOrder();
                orderForm.submit();
            }

            draggedRow = null;
        });
    });

    list.addEventListener("dragover", event => {
        if (!draggedRow) {
            return;
        }

        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        const target = event.target.closest("[data-linkbox-row]");
        if (!target || target === draggedRow) {
            return;
        }

        const rect = target.getBoundingClientRect();
        const insertBefore = event.clientY < rect.top + (rect.height / 2);
        target.parentNode.insertBefore(draggedRow, insertBefore ? target : target.nextSibling);
    });

    // Some browsers require a drop handler for the move to be allowed.
    list.addEventListener("drop", event => event.preventDefault());
})();
