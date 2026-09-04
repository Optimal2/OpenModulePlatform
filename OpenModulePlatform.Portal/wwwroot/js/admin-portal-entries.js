// File: OpenModulePlatform.Portal/wwwroot/js/admin-portal-entries.js
(() => {
    'use strict';

    const createRow = document.querySelector("[data-portal-entry-create-row]");
    const createToggle = document.querySelector("[data-portal-entry-create-toggle]");
    const createClose = document.querySelector("[data-portal-entry-create-close]");
    const list = document.querySelector("[data-portal-entry-layout-list]");

    const setPanelOpen = (panel, toggle, open) => {
        if (!panel) {
            return;
        }

        panel.hidden = !open;
        toggle?.setAttribute("aria-expanded", String(open));
    };

    createToggle?.addEventListener("click", () => {
        const open = createRow?.hidden ?? true;
        setPanelOpen(createRow, createToggle, open);
        if (open) {
            createRow?.querySelector("input, select, textarea, button")?.focus();
        }
    });

    createClose?.addEventListener("click", () => setPanelOpen(createRow, createToggle, false));

    document.querySelectorAll("[data-iframe-standalone-helper]").forEach(helper => {
        const toggle = helper.querySelector("[data-iframe-standalone-toggle]");
        const options = helper.querySelector("[data-iframe-standalone-options]");
        const sync = () => {
            if (options) {
                options.hidden = !(toggle?.checked ?? false);
            }

            helper.classList.toggle("is-active", toggle?.checked ?? false);
        };

        toggle?.addEventListener("change", sync);
        sync();
    });

    if (!list) {
        return;
    }

    let draggedRow = null;

    const rows = () => Array.from(list.querySelectorAll("[data-portal-entry-layout-row]"));
    const rowId = row => row?.dataset.entryId ?? "";
    const parentId = row => row?.querySelector("[data-portal-entry-parent-input]")?.value ?? "";
    const findRow = id => rows().find(row => rowId(row) === id) ?? null;
    const isChecked = row => row?.querySelector("[data-portal-entry-visible-checkbox]")?.checked ?? false;

    const isDescendantOf = (row, ancestorRow) => {
        const ancestorId = rowId(ancestorRow);
        let currentParentId = parentId(row);
        const visited = new Set([rowId(row)]);

        while (currentParentId) {
            if (currentParentId === ancestorId) {
                return true;
            }

            if (visited.has(currentParentId)) {
                return false;
            }

            visited.add(currentParentId);
            currentParentId = parentId(findRow(currentParentId));
        }

        return false;
    };

    const subtreeRows = row => rows().filter(candidate => candidate === row || isDescendantOf(candidate, row));
    const lastSubtreeRow = row => {
        const group = subtreeRows(row);
        return group[group.length - 1] ?? row;
    };

    const nearestPreviousTopRow = (row, currentRows = rows()) => {
        const rowIndex = currentRows.indexOf(row);
        for (let index = rowIndex - 1; index >= 0; index--) {
            const candidate = currentRows[index];
            if (!parentId(candidate)) {
                return candidate;
            }
        }

        return null;
    };

    const depthFor = row => {
        let depth = 0;
        let currentParentId = parentId(row);
        const visited = new Set([rowId(row)]);

        while (currentParentId) {
            if (visited.has(currentParentId)) {
                break;
            }

            visited.add(currentParentId);
            const parentRow = findRow(currentParentId);
            if (!parentRow) {
                break;
            }

            depth++;
            currentParentId = parentId(parentRow);
        }

        return Math.min(depth, 3);
    };

    const applyVisibilityFromAncestors = () => {
        rows().forEach(row => {
            const checkbox = row.querySelector("[data-portal-entry-visible-checkbox]");
            if (!checkbox) {
                return;
            }

            let currentParentId = parentId(row);
            const visited = new Set([rowId(row)]);
            while (currentParentId) {
                if (visited.has(currentParentId)) {
                    break;
                }

                visited.add(currentParentId);
                const parentRow = findRow(currentParentId);
                if (!parentRow) {
                    break;
                }

                if (!isChecked(parentRow)) {
                    checkbox.checked = false;
                    break;
                }

                currentParentId = parentId(parentRow);
            }
        });
    };

    const syncRows = () => {
        applyVisibilityFromAncestors();

        const siblingIndexes = new Map();
        const currentRows = rows();
        currentRows.forEach(row => {
            const parent = parentId(row);
            const sortInput = row.querySelector("[data-portal-entry-sort-input]");
            const depth = depthFor(row);
            const checkbox = row.querySelector("[data-portal-entry-visible-checkbox]");
            const visibilityToggle = row.querySelector(".portal-entry-layout-row__visibility-toggle");
            const makeTopButton = row.querySelector("[data-portal-entry-clear-parent]");
            const makeChildButton = row.querySelector("[data-portal-entry-make-child]");
            const hasChildren = currentRows.some(candidate => parentId(candidate) === rowId(row));
            const siblings = parent
                ? currentRows.filter(candidate => parentId(candidate) === parent)
                : [];
            const hasNextSibling = parent && siblings[siblings.length - 1] !== row;

            row.dataset.parentId = parent;
            row.dataset.depth = String(depth);
            row.dataset.hasChildren = hasChildren ? "true" : "false";
            row.style.setProperty("--portal-entry-depth", String(depth));
            row.classList.toggle("is-child-entry", depth > 0);
            row.classList.toggle("is-tree-continuation", Boolean(hasNextSibling));
            row.classList.toggle("is-disabled", checkbox ? !checkbox.checked : false);
            visibilityToggle?.classList.toggle("admin-icon-button--visible", checkbox?.checked ?? false);
            visibilityToggle?.classList.toggle("admin-icon-button--visible-off", !(checkbox?.checked ?? false));

            if (makeTopButton) {
                makeTopButton.disabled = !parent;
            }

            if (makeChildButton) {
                makeChildButton.disabled = Boolean(parent) || hasChildren || !nearestPreviousTopRow(row, currentRows);
            }

            if (sortInput) {
                const nextIndex = (siblingIndexes.get(parent) ?? 0) + 1;
                siblingIndexes.set(parent, nextIndex);
                sortInput.value = String(nextIndex * 10);
            }
        });
    };

    const clearDropState = () => {
        rows().forEach(row => row.classList.remove("is-drop-target", "is-drop-before", "is-drop-after", "is-dragging"));
    };

    const dropMode = (event, targetRow) => {
        const rect = targetRow.getBoundingClientRect();
        const edgeZone = Math.min(18, Math.max(10, rect.height * 0.28));

        if (event.clientY < rect.top + edgeZone) {
            return "before";
        }

        if (event.clientY > rect.bottom - edgeZone) {
            return "after";
        }

        return "child";
    };

    const canDrop = (targetRow, mode) => {
        if (!draggedRow || !targetRow || targetRow === draggedRow) {
            return false;
        }

        if (subtreeRows(draggedRow).includes(targetRow)) {
            return false;
        }

        if (mode === "child") {
            return !parentId(targetRow) && !subtreeRows(draggedRow).some(row => row !== draggedRow);
        }

        return !parentId(targetRow) || !subtreeRows(draggedRow).some(row => row !== draggedRow);
    };

    const moveDraggedRows = (targetRow, mode) => {
        const parentInput = draggedRow.querySelector("[data-portal-entry-parent-input]");
        const newParentId = mode === "child"
            ? rowId(targetRow)
            : parentId(targetRow);

        if (parentInput) {
            parentInput.value = newParentId;
        }

        const fragment = document.createDocumentFragment();
        subtreeRows(draggedRow).forEach(row => fragment.appendChild(row));

        if (mode === "before") {
            list.insertBefore(fragment, targetRow);
            return;
        }

        lastSubtreeRow(targetRow).after(fragment);
    };

    list.addEventListener("dragstart", event => {
        if (!event.target.closest("[data-portal-entry-drag-handle]")) {
            return;
        }

        const row = event.target.closest("[data-portal-entry-layout-row]");
        if (!row) {
            return;
        }

        draggedRow = row;
        row.classList.add("is-dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", row.dataset.entryId ?? "");
    });

    list.addEventListener("dragover", event => {
        const targetRow = event.target.closest("[data-portal-entry-layout-row]");
        if (!draggedRow || !targetRow) {
            return;
        }

        const mode = dropMode(event, targetRow);
        clearDropState();

        if (!canDrop(targetRow, mode)) {
            event.dataTransfer.dropEffect = "none";
            return;
        }

        event.preventDefault();
        targetRow.classList.add(mode === "child" ? "is-drop-target" : `is-drop-${mode}`);
        event.dataTransfer.dropEffect = "move";
    });

    list.addEventListener("drop", event => {
        const targetRow = event.target.closest("[data-portal-entry-layout-row]");
        if (!draggedRow || !targetRow) {
            return;
        }

        const mode = dropMode(event, targetRow);
        if (!canDrop(targetRow, mode)) {
            clearDropState();
            draggedRow = null;
            return;
        }

        event.preventDefault();
        moveDraggedRows(targetRow, mode);
        syncRows();
        clearDropState();
        draggedRow = null;
    });

    list.addEventListener("dragend", () => {
        clearDropState();
        draggedRow = null;
    });

    list.addEventListener("click", event => {
        // R8-P5-16: the confirmation moved to the shared [data-omp-confirm]
        // wiring, which intercepts the click before this handler runs.
        const editorToggle = event.target.closest("[data-portal-entry-editor-toggle]");
        if (editorToggle) {
            const panel = document.getElementById(editorToggle.getAttribute("aria-controls"));
            const open = panel?.hidden ?? true;
            setPanelOpen(panel, editorToggle, open);
            if (open) {
                panel?.querySelector("input, select, textarea, button")?.focus();
            }
            return;
        }

        const editorClose = event.target.closest("[data-portal-entry-editor-close]");
        if (editorClose) {
            const panel = editorClose.closest(".portal-entry-inline-editor");
            const toggle = panel?.id
                ? list.querySelector(`[data-portal-entry-editor-toggle][aria-controls="${panel.id}"]`)
                : null;
            setPanelOpen(panel, toggle, false);
            return;
        }

        const clearButton = event.target.closest("[data-portal-entry-clear-parent]");
        if (clearButton) {
            const row = clearButton.closest("[data-portal-entry-layout-row]");
            const parentInput = row?.querySelector("[data-portal-entry-parent-input]");
            if (row && parentInput) {
                const parentRow = findRow(row.dataset.parentId);
                parentInput.value = "";
                if (parentRow) {
                    lastSubtreeRow(parentRow).after(row);
                }

                syncRows();
            }

            return;
        }

        const childButton = event.target.closest("[data-portal-entry-make-child]");
        if (childButton) {
            const row = childButton.closest("[data-portal-entry-layout-row]");
            const parentInput = row?.querySelector("[data-portal-entry-parent-input]");
            const parentRow = row ? nearestPreviousTopRow(row) : null;
            if (row && parentInput && parentRow) {
                parentInput.value = parentRow.dataset.entryId ?? "";
                lastSubtreeRow(parentRow).after(row);
                syncRows();
            }
        }
    });

    list.addEventListener("change", event => {
        if (!event.target.matches("[data-portal-entry-visible-checkbox]")) {
            return;
        }

        syncRows();
    });

    syncRows();
})();
