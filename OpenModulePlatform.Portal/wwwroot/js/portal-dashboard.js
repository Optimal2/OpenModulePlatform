// File: OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js
(() => {
    'use strict';

    const minWidth = 160;
    const minHeight = 96;
    const defaultGridSize = 32;
    const defaultMinCanvasHeight = 640;
    const defaultBottomPadding = 64;
    const defaultViewBottomPadding = 32;
    const defaultEmptyCanvasHeight = 220;
    const dashboardDraftStoragePrefix = 'omp.dashboardDraft.';
    const dashboardDraftSchemaVersion = 1;
    const dashboardDraftTtlMs = 24 * 60 * 60 * 1000;
    const dashboardDraftWriteDelayMs = 600;
    const favoriteChangedEvent = 'omp:navigation-favorite-changed';
    const notificationChangedEvent = 'omp:notification-state-changed';
    const sessionStatusWarningEvent = 'omp:session-status-warning';
    const musicObjectUrls = new Set();
    const blankImageObjectUrls = new Set();
    let activeWidgetPopup = null;
    let activeWidgetPopupDrag = null;
    let dashboardWidgetPopupId = 0;
    let blankWidgetImagesCache = null;

    function openDashboardWidgetPopup(options) {
        const anchor = options?.anchor || null;
        const content = options?.content || null;
        const ownerWidget = options?.ownerWidget || anchor?.closest?.('[data-dashboard-widget]') || null;
        const root = anchor?.closest?.('[data-dashboard-root]') || ownerWidget?.closest?.('[data-dashboard-root]') || null;
        const canvas = root?.querySelector?.('[data-dashboard-canvas]') || null;
        const layer = canvas?.querySelector?.('[data-dashboard-widget-popup-layer]') || null;

        if (!anchor || !content || !root || !canvas || !layer || (root.classList.contains('is-editing') && options?.allowInEditMode !== true)) {
            return false;
        }

        if (activeWidgetPopup?.content === content) {
            closeDashboardWidgetPopup({ restoreFocus: false });
        } else {
            closeDashboardWidgetPopup({ restoreFocus: false });
        }

        const placeholder = document.createComment('dashboard-widget-popup-placeholder');
        const originalParent = content.parentNode;
        const originalNextSibling = content.nextSibling;
        const originalState = {
            hidden: content.hidden,
            position: content.style.position,
            inset: content.style.inset,
            left: content.style.left,
            top: content.style.top,
            right: content.style.right,
            bottom: content.style.bottom,
            width: content.style.width,
            maxWidth: content.style.maxWidth,
            maxHeight: content.style.maxHeight,
            visibility: content.style.visibility
        };

        if (originalParent) {
            originalParent.insertBefore(placeholder, originalNextSibling);
        }

        if (!content.id) {
            dashboardWidgetPopupId += 1;
            content.id = `omp-dashboard-widget-popup-${dashboardWidgetPopupId}`;
        }

        activeWidgetPopup = {
            anchor,
            content,
            canvas,
            layer,
            dragHandle: null,
            ownerWidget,
            ownerWidgetId: options.ownerWidgetId || ownerWidget?.dataset?.userActiveWidgetId || '',
            onClose: typeof options.onClose === 'function' ? options.onClose : null,
            originalParent,
            placeholder,
            originalState,
            placement: options.placement || 'bottom-start'
        };

        content.classList.add('omp-dashboard-widget-popup');
        content.classList.add('is-dashboard-widget-popup-draggable');
        content.hidden = false;
        content.style.position = 'absolute';
        content.style.inset = 'auto';
        content.style.left = '0px';
        content.style.top = '0px';
        content.style.right = 'auto';
        content.style.bottom = 'auto';
        content.style.visibility = 'hidden';
        layer.appendChild(content);

        const dragHandle = document.createElement('span');
        dragHandle.className = 'omp-dashboard-widget-popup__drag-handle';
        dragHandle.setAttribute('aria-hidden', 'true');
        dragHandle.addEventListener('pointerdown', handleDashboardWidgetPopupDragStart);
        content.prepend(dragHandle);
        activeWidgetPopup.dragHandle = dragHandle;

        anchor.setAttribute('aria-expanded', 'true');
        anchor.setAttribute('aria-controls', content.id);

        bindDashboardWidgetPopupEvents();
        positionDashboardWidgetPopup();
        content.style.visibility = originalState.visibility || '';
        return true;
    }

    function closeDashboardWidgetPopup(options = {}) {
        const popup = activeWidgetPopup;
        if (!popup) {
            return;
        }

        activeWidgetPopup = null;
        unbindDashboardWidgetPopupEvents();
        cancelDashboardWidgetPopupDrag();

        const { anchor, content, placeholder, originalState, onClose } = popup;
        popup.dragHandle?.remove();
        content.classList.remove('omp-dashboard-widget-popup');
        content.classList.remove('is-dashboard-widget-popup-draggable');
        content.hidden = originalState.hidden;
        content.style.position = originalState.position;
        content.style.inset = originalState.inset;
        content.style.left = originalState.left;
        content.style.top = originalState.top;
        content.style.right = originalState.right;
        content.style.bottom = originalState.bottom;
        content.style.width = originalState.width;
        content.style.maxWidth = originalState.maxWidth;
        content.style.maxHeight = originalState.maxHeight;
        content.style.visibility = originalState.visibility;

        if (placeholder?.parentNode) {
            placeholder.parentNode.insertBefore(content, placeholder);
            placeholder.remove();
        } else {
            content.remove();
        }

        if (anchor?.isConnected) {
            anchor.setAttribute('aria-expanded', 'false');
            if (options.restoreFocus) {
                anchor.focus({ preventScroll: true });
            }
        }

        onClose?.();
    }

    function isDashboardWidgetPopupOpenFor(content) {
        return !!activeWidgetPopup && activeWidgetPopup.content === content;
    }

    function closeDashboardWidgetPopupForWidget(widget) {
        if (activeWidgetPopup?.ownerWidget === widget) {
            closeDashboardWidgetPopup({ restoreFocus: false });
        }
    }

    function positionDashboardWidgetPopup() {
        const popup = activeWidgetPopup;
        if (!popup || !popup.anchor.isConnected || !popup.content.isConnected) {
            closeDashboardWidgetPopup({ restoreFocus: false });
            return;
        }

        const { anchor, content, canvas, placement } = popup;
        const gap = 8;
        const viewportPadding = 12;
        const anchorRect = anchor.getBoundingClientRect();
        const canvasRect = canvas.getBoundingClientRect();

        content.style.maxWidth = `calc(100vw - ${viewportPadding * 2}px)`;
        content.style.maxHeight = `min(70vh, ${Math.max(180, window.innerHeight - viewportPadding * 2)}px)`;

        const popupRect = content.getBoundingClientRect();
        const openUpward = anchorRect.bottom + gap + popupRect.height > window.innerHeight - viewportPadding
            && anchorRect.top - gap - popupRect.height >= viewportPadding;

        let clientTop = openUpward
            ? anchorRect.top - popupRect.height - gap
            : anchorRect.bottom + gap;
        let clientLeft = placement.endsWith('end')
            ? anchorRect.right - popupRect.width
            : anchorRect.left;

        clientLeft = Math.min(
            Math.max(clientLeft, viewportPadding),
            Math.max(viewportPadding, window.innerWidth - popupRect.width - viewportPadding));
        clientTop = Math.min(
            Math.max(clientTop, viewportPadding),
            Math.max(viewportPadding, window.innerHeight - popupRect.height - viewportPadding));

        content.style.left = `${clientLeft - canvasRect.left}px`;
        content.style.top = `${clientTop - canvasRect.top}px`;
    }

    function handleDashboardWidgetPopupDragStart(event) {
        const popup = activeWidgetPopup;
        if (!popup || event.button !== 0) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();

        const contentRect = popup.content.getBoundingClientRect();
        const canvasRect = popup.canvas.getBoundingClientRect();
        activeWidgetPopupDrag = {
            pointerId: event.pointerId,
            popup,
            startClientX: event.clientX,
            startClientY: event.clientY,
            startLeft: contentRect.left - canvasRect.left,
            startTop: contentRect.top - canvasRect.top
        };

        popup.content.classList.add('is-dashboard-widget-popup-dragging');
        document.addEventListener('pointermove', handleDashboardWidgetPopupDragMove, true);
        document.addEventListener('pointerup', handleDashboardWidgetPopupDragEnd, true);
        document.addEventListener('pointercancel', handleDashboardWidgetPopupDragEnd, true);
        event.currentTarget?.setPointerCapture?.(event.pointerId);
    }

    function handleDashboardWidgetPopupDragMove(event) {
        const drag = activeWidgetPopupDrag;
        if (!drag || event.pointerId !== drag.pointerId || !drag.popup.content.isConnected) {
            return;
        }

        event.preventDefault();
        const nextLeft = drag.startLeft + event.clientX - drag.startClientX;
        const nextTop = drag.startTop + event.clientY - drag.startClientY;
        setDashboardWidgetPopupPosition(drag.popup, nextLeft, nextTop);
    }

    function handleDashboardWidgetPopupDragEnd(event) {
        const drag = activeWidgetPopupDrag;
        if (drag && event.pointerId === drag.pointerId) {
            event.preventDefault();
        }

        cancelDashboardWidgetPopupDrag();
    }

    function cancelDashboardWidgetPopupDrag() {
        if (activeWidgetPopupDrag?.popup?.content) {
            activeWidgetPopupDrag.popup.content.classList.remove('is-dashboard-widget-popup-dragging');
        }

        activeWidgetPopupDrag = null;
        document.removeEventListener('pointermove', handleDashboardWidgetPopupDragMove, true);
        document.removeEventListener('pointerup', handleDashboardWidgetPopupDragEnd, true);
        document.removeEventListener('pointercancel', handleDashboardWidgetPopupDragEnd, true);
    }

    function setDashboardWidgetPopupPosition(popup, left, top) {
        const canvasWidth = Math.max(popup.canvas.scrollWidth, popup.canvas.offsetWidth, popup.canvas.clientWidth);
        const canvasHeight = Math.max(popup.canvas.scrollHeight, popup.canvas.offsetHeight, popup.canvas.clientHeight);
        const popupRect = popup.content.getBoundingClientRect();
        const maxLeft = Math.max(0, canvasWidth - popupRect.width);
        const maxTop = Math.max(0, canvasHeight - popupRect.height);

        popup.content.style.left = `${Math.min(Math.max(0, left), maxLeft)}px`;
        popup.content.style.top = `${Math.min(Math.max(0, top), maxTop)}px`;
    }

    function bindDashboardWidgetPopupEvents() {
        document.addEventListener('pointerdown', handleDashboardWidgetPopupPointerDown, true);
        document.addEventListener('keydown', handleDashboardWidgetPopupKeyDown, true);
        window.addEventListener('resize', handleDashboardWidgetPopupResize, true);
        window.addEventListener('scroll', handleDashboardWidgetPopupScroll, true);
    }

    function unbindDashboardWidgetPopupEvents() {
        document.removeEventListener('pointerdown', handleDashboardWidgetPopupPointerDown, true);
        document.removeEventListener('keydown', handleDashboardWidgetPopupKeyDown, true);
        window.removeEventListener('resize', handleDashboardWidgetPopupResize, true);
        window.removeEventListener('scroll', handleDashboardWidgetPopupScroll, true);
    }

    function handleDashboardWidgetPopupPointerDown(event) {
        const popup = activeWidgetPopup;
        if (!popup) {
            return;
        }

        const target = event.target;
        if (popup.content.contains(target) || popup.anchor.contains(target)) {
            return;
        }

        closeDashboardWidgetPopup({ restoreFocus: false });
    }

    function handleDashboardWidgetPopupKeyDown(event) {
        if (event.key === 'Escape' && activeWidgetPopup) {
            closeDashboardWidgetPopup({ restoreFocus: true });
        }
    }

    function handleDashboardWidgetPopupResize() {
        closeDashboardWidgetPopup({ restoreFocus: false });
    }

    function handleDashboardWidgetPopupScroll(event) {
        if (event.target instanceof Node && activeWidgetPopup?.content.contains(event.target)) {
            return;
        }

        closeDashboardWidgetPopup({ restoreFocus: false });
    }

    window.ompDashboardWidgetPopups = {
        open: openDashboardWidgetPopup,
        close: closeDashboardWidgetPopup,
        isOpenFor: isDashboardWidgetPopupOpenFor
    };

    function initDashboard(root) {
        const canvas = root.querySelector('[data-dashboard-canvas]');
        const editToggle = root.querySelector('[data-dashboard-edit-toggle]');
        const editLabel = root.querySelector('[data-dashboard-edit-label]');
        const saveButton = root.querySelector('[data-dashboard-save]');
        const addButton = root.querySelector('[data-dashboard-add-open]');
        const resetChangesButton = root.querySelector('[data-dashboard-reset-changes]');
        const alignToggle = root.querySelector('[data-dashboard-align-toggle]');
        const picker = root.querySelector('[data-widget-picker]');
        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';

        bindRoleSwitchConfirmations(root);

        if (!canvas || !editToggle || root.dataset.canEdit !== 'true') {
            if (canvas) {
                updateCanvasHeight(root, canvas);
                window.addEventListener('resize', () => updateCanvasHeight(root, canvas));
            }
            bindDashboardMusicPlayers(root);
            bindEntryListFilters(root);
            bindDashboardNotifications(root);
            return;
        }

        let isEditing = false;
        let maxOrder = getMaxOrder(canvas);
        const state = {
            alignToGrid: root.dataset.alignToGrid !== 'false',
            expandedCanvas: root.dataset.expandedCanvas === 'true',
            gridSize: parsePositiveInteger(root.dataset.gridSize, defaultGridSize),
            minCanvasHeight: parsePositiveInteger(root.dataset.minCanvasHeight, defaultMinCanvasHeight),
            bottomPadding: parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding),
            viewBottomPadding: parsePositiveInteger(root.dataset.canvasViewBottomPadding, defaultViewBottomPadding),
            emptyCanvasHeight: parsePositiveInteger(root.dataset.emptyCanvasHeight, defaultEmptyCanvasHeight),
            addedWidgetIds: new Set(),
            pendingRemovedWidgetIds: new Set(),
            nextTemporaryWidgetId: -1,
            savedSnapshot: null,
            savedSignature: '',
            editCanvasWidth: 0,
            editCanvasHeight: 0,
            draftWriteTimer: 0
        };
        const updateDirtyState = () => {
            const isDirty = updateDashboardDirtyState(root, canvas, state, saveButton);
            updateDashboardDraftState(root, canvas, state, isDirty);
            return isDirty;
        };

        updateAlignToGridState(root, alignToggle, state.alignToGrid);
        updateExpandedCanvasState(root, null, state.expandedCanvas);
        updateCanvasHeight(root, canvas, state);
        window.addEventListener('resize', () => updateCanvasHeight(root, canvas, state));
        bindDashboardBoxSelection(root, canvas);

        editToggle.addEventListener('click', async () => {
            if (isEditing) {
                if (!updateDirtyState()) {
                    setEditing(false);
                    return;
                }

                const choice = await requestDashboardDoneChoice(root);
                if (choice === 'save') {
                    try {
                        await saveDashboardChanges(root, canvas, token, state);
                        maxOrder = getMaxOrder(canvas);
                        setEditing(false);
                    } catch (error) {
                        handleDashboardSaveError(root, error);
                    }
                } else if (choice === 'discard') {
                    const snapshot = state.savedSnapshot || captureDashboardSnapshot(canvas, state);
                    await resetDashboardChanges(root, canvas, token, state, snapshot, () => ++maxOrder, updateDirtyState);
                    maxOrder = snapshot.maxOrder;
                    setEditing(false);
                }

                return;
            }

            setEditing(true);
        });

        saveButton?.addEventListener('click', async () => {
            if (!updateDirtyState()) {
                return;
            }

            try {
                await saveDashboardChanges(root, canvas, token, state);
                maxOrder = getMaxOrder(canvas);
            } catch (error) {
                handleDashboardSaveError(root, error);
            }
        });

        addButton?.addEventListener('click', () => {
            if (!picker) {
                return;
            }

            openWidgetPicker(picker);
        });

        resetChangesButton?.addEventListener('click', async () => {
            const snapshot = state.savedSnapshot || captureDashboardSnapshot(canvas, state);
            await resetDashboardChanges(root, canvas, token, state, snapshot, () => ++maxOrder, updateDirtyState);
            maxOrder = snapshot.maxOrder;
            updateDirtyState();
        });

        alignToggle?.addEventListener('change', async () => {
            const next = alignToggle.checked;
            const previous = state.alignToGrid;
            updateAlignToGridState(root, alignToggle, next);
            state.alignToGrid = next;
            if (next) {
                snapAllWidgetsToGrid(canvas, state);
                updateCanvasHeight(root, canvas, state);
            }
            updateDirtyState();

            try {
                await saveDashboardPreferences(root, token, state);
            } catch (error) {
                state.alignToGrid = previous;
                updateAlignToGridState(root, alignToggle, state.alignToGrid);
                updateDirtyState();
                throw error;
            }
        });

        picker?.querySelector('[data-widget-picker-close]')?.addEventListener('click', () => {
            closePicker(picker);
        });

        picker?.addEventListener('close', () => {
            document.body.classList.remove('dashboard-widget-picker-open');
        });

        bindWidgetPickerFilter(picker);
        bindWidgetPickerCompactToggle(picker);
        window.addEventListener('resize', () => syncWidgetPickerCompactMode(picker));

        picker?.addEventListener('click', (event) => {
            if (event.target === picker) {
                closePicker(picker);
            }
        });

        const addSelectedWidget = async () => {
            const option = getSelectedWidgetPickerOption(picker);
            if (!option) {
                return;
            }

            const widgetId = parseInt(option.dataset.widgetId || '0', 10);
            if (!widgetId) {
                return;
            }

            const widget = await postForm(root.dataset.addUrl, token, { widgetId });
            if (!widget) {
                return;
            }

            widget.title = option.dataset.widgetTitle || widget.title || '';
            const temporaryWidgetId = state.nextTemporaryWidgetId--;
            widget.userActiveWidgetId = temporaryWidgetId;
            widget.orderPriority = ++maxOrder;
            const offset = getNextWidgetOffset(canvas, state);
            widget.offsetTop = offset;
            widget.offsetLeft = offset;

            const element = createWidgetElement(root, widget);
            canvas.appendChild(element);
            snapWidgetToGrid(element, state);
            state.addedWidgetIds.add(temporaryWidgetId);
            bindWidget(root, canvas, element, token, () => ++maxOrder, state, updateDirtyState);
            bindEntryFavoriteToggles(root, element, token);
            bindEntryListFilters(element);
            bindDashboardMusicPlayers(element);
            bindDashboardNotifications(element);
            updateEmptyState(canvas);
            updateCanvasHeight(root, canvas, state);
            updateDirtyState();
            closePicker(picker);
            if (!isEditing) {
                setEditing(true);
            }
        };

        picker?.querySelector('[data-widget-picker-add]')?.addEventListener('click', addSelectedWidget);

        picker?.querySelectorAll('[data-widget-option]').forEach((option) => {
            option.addEventListener('keydown', (event) => {
                if (event.key !== 'Enter' && event.key !== ' ') {
                    return;
                }

                event.preventDefault();
                selectWidgetPickerOption(root, picker, option);
            });
            option.addEventListener('dblclick', addSelectedWidget);
            option.addEventListener('click', () => selectWidgetPickerOption(root, picker, option));
        });

        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            bindWidget(root, canvas, widget, token, () => ++maxOrder, state, updateDirtyState);
        });
        bindEntryFavoriteToggles(root, root, token);
        bindEntryListFilters(root);
        bindDashboardMusicPlayers(root);
        bindDashboardNotifications(root);
        state.savedSnapshot = captureDashboardSnapshot(canvas, state);
        state.savedSignature = getDashboardSignature(canvas, state);
        updateDirtyState();
        initDashboardDraftPrompt(root, canvas, token, state, updateDirtyState, () => {
            if (!isEditing) {
                setEditing(true);
            }

            maxOrder = getMaxOrder(canvas);
            updateDirtyState();
        });

        function setEditing(next) {
            if (isEditing === next) {
                updateCanvasHeight(root, canvas, state);
                updateDirtyState();
                return;
            }

            if (next) {
                closeDashboardWidgetPopup({ restoreFocus: false });
            }

            isEditing = next;
            if (isEditing) {
                state.addedWidgetIds.clear();
                state.pendingRemovedWidgetIds.clear();
                state.savedSnapshot = captureDashboardSnapshot(canvas, state);
                state.savedSignature = getDashboardSignature(canvas, state);
                maxOrder = state.savedSnapshot.maxOrder;
            }

            root.classList.toggle('is-editing', isEditing);
            if (!isEditing) {
                clearWidgetSelection(root, canvas);
                state.editCanvasWidth = 0;
                state.editCanvasHeight = 0;
            }
            editToggle.setAttribute('aria-pressed', isEditing ? 'true' : 'false');
            if (editLabel) {
                editLabel.textContent = '';
            }
            const buttonLabel = isEditing
                ? root.dataset.doneLabel || 'Done'
                : root.dataset.editLabel || 'Edit dashboard';
            editToggle.setAttribute('aria-label', buttonLabel);
            editToggle.title = buttonLabel;
            updateCanvasHeight(root, canvas, state);
            updateDirtyState();
        }
    }

    function bindRoleSwitchConfirmations(root) {
        if (root.dataset.dashboardRoleSwitchBound === 'true') {
            return;
        }

        root.dataset.dashboardRoleSwitchBound = 'true';
        root.addEventListener('click', (event) => {
            const link = event.target.closest('[data-dashboard-role-switch]');
            if (!link || !root.contains(link)) {
                return;
            }

            const message = link.dataset.confirmMessage || '';
            if (message && !window.confirm(message)) {
                event.preventDefault();
            }
        });
    }

    function bindWidget(root, canvas, widget, token, nextOrder, state, onChange = () => {}) {
        const removeButton = widget.querySelector('[data-widget-remove]');
        const resizeHandle = widget.querySelector('[data-widget-resize]');

        bindWidgetSettings(root, widget, onChange);
        bindWidgetTitlebarToggle(widget, onChange);
        applyWidgetSettings(widget);
        bindBlankWidgetRuntimeControls(root, canvas, widget, token, state);

        widget.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            if (event.target.closest('button, a, input, textarea, select, label, summary, [data-widget-resize], [data-widget-settings-panel], [data-widget-zoom-panel], [data-blank-widget-admin]')) {
                return;
            }

            event.preventDefault();
            const selectedWidgets = getSelectedWidgets(canvas);
            const movesSelection = selectedWidgets.length > 1 && widget.classList.contains('is-selected');
            if (movesSelection) {
                selectedWidgets.forEach((selectedWidget) => bringToFront(selectedWidget, nextOrder()));
            } else {
                if (!(selectedWidgets.length === 1 && widget.classList.contains('is-selected'))) {
                    clearWidgetSelection(root, canvas);
                }
                bringToFront(widget, nextOrder());
            }

            startDrag(root, canvas, widget, event, state, onChange);
        });

        resizeHandle?.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            if (getSelectedWidgets(canvas).length > 1 && widget.classList.contains('is-selected')) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            bringToFront(widget, nextOrder());
            startResize(root, canvas, widget, event, state, onChange);
        });

        removeButton?.addEventListener('click', async (event) => {
            event.preventDefault();
            event.stopPropagation();

            const userActiveWidgetId = parseInt(widget.dataset.userActiveWidgetId || '0', 10);
            if (!userActiveWidgetId) {
                return;
            }

            if (userActiveWidgetId > 0) {
                state.pendingRemovedWidgetIds.add(userActiveWidgetId);
            }
            removeDashboardWidgetElement(widget);
            updateWidgetSelectionState(root, canvas);
            updateEmptyState(canvas);
            updateCanvasHeight(root, canvas, state);
            onChange();
        });
    }

    function bindDashboardBoxSelection(root, canvas) {
        if (canvas.dataset.dashboardBoxSelectionBound === 'true') {
            return;
        }

        canvas.dataset.dashboardBoxSelectionBound = 'true';
        canvas.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            if (event.target.closest('[data-dashboard-widget], button, a, input, textarea, select, summary')) {
                return;
            }

            event.preventDefault();

            const start = getCanvasPointerPosition(canvas, event);
            const selectionBox = document.createElement('div');
            selectionBox.className = 'dashboard-selection-box';
            selectionBox.setAttribute('aria-hidden', 'true');
            canvas.appendChild(selectionBox);
            canvas.setPointerCapture(event.pointerId);

            let moved = false;
            const move = (moveEvent) => {
                const current = getCanvasPointerPosition(canvas, moveEvent);
                moved = moved
                    || Math.abs(current.x - start.x) > 3
                    || Math.abs(current.y - start.y) > 3;
                const rect = normalizeSelectionRect(start, current);
                positionSelectionBox(selectionBox, rect);
                selectWidgetsInRect(root, canvas, rect);
            };

            const end = () => {
                selectionBox.remove();
                canvas.removeEventListener('pointermove', move);
                canvas.removeEventListener('pointerup', end);
                canvas.removeEventListener('pointercancel', end);

                if (!moved) {
                    clearWidgetSelection(root, canvas);
                } else {
                    updateWidgetSelectionState(root, canvas);
                }
            };

            canvas.addEventListener('pointermove', move);
            canvas.addEventListener('pointerup', end);
            canvas.addEventListener('pointercancel', end);
        });
    }

    function getCanvasPointerPosition(canvas, event) {
        const rect = canvas.getBoundingClientRect();
        return {
            x: event.clientX - rect.left + canvas.scrollLeft,
            y: event.clientY - rect.top + canvas.scrollTop
        };
    }

    function normalizeSelectionRect(start, current) {
        const left = Math.min(start.x, current.x);
        const top = Math.min(start.y, current.y);
        const right = Math.max(start.x, current.x);
        const bottom = Math.max(start.y, current.y);
        return {
            left,
            top,
            right,
            bottom,
            width: right - left,
            height: bottom - top
        };
    }

    function positionSelectionBox(selectionBox, rect) {
        selectionBox.style.left = `${rect.left}px`;
        selectionBox.style.top = `${rect.top}px`;
        selectionBox.style.width = `${rect.width}px`;
        selectionBox.style.height = `${rect.height}px`;
    }

    function selectWidgetsInRect(root, canvas, selectionRect) {
        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            const rect = getWidgetCanvasRect(widget);
            setWidgetSelected(widget, rectsIntersect(selectionRect, rect));
        });
        updateWidgetSelectionState(root, canvas);
    }

    function getWidgetCanvasRect(widget) {
        const left = parsePixel(widget.style.left);
        const top = parsePixel(widget.style.top);
        return {
            left,
            top,
            right: left + widget.offsetWidth,
            bottom: top + widget.offsetHeight
        };
    }

    function rectsIntersect(left, right) {
        return left.left < right.right
            && left.right > right.left
            && left.top < right.bottom
            && left.bottom > right.top;
    }

    function getSelectedWidgets(canvas) {
        return Array.from(canvas.querySelectorAll('[data-dashboard-widget].is-selected'));
    }

    function setWidgetSelected(widget, selected) {
        widget.classList.toggle('is-selected', selected);
        if (selected) {
            widget.setAttribute('aria-selected', 'true');
        } else {
            widget.removeAttribute('aria-selected');
        }
    }

    function clearWidgetSelection(root, canvas) {
        getSelectedWidgets(canvas).forEach((widget) => setWidgetSelected(widget, false));
        updateWidgetSelectionState(root, canvas);
    }

    function updateWidgetSelectionState(root, canvas) {
        const selectedCount = getSelectedWidgets(canvas).length;
        root.classList.toggle('has-selected-widgets', selectedCount > 0);
        root.classList.toggle('has-multi-selected-widgets', selectedCount > 1);
        canvas.dataset.selectedWidgetCount = String(selectedCount);
    }

    function bindWidgetSettings(root, widget, onChange = () => {}) {
        const zoomToggle = widget.querySelector('[data-widget-zoom-toggle]');
        const zoomPanel = widget.querySelector('[data-widget-zoom-panel]');
        const toggle = widget.querySelector('[data-widget-settings-toggle]');
        const panel = widget.querySelector('[data-widget-settings-panel]');
        const closeSettingsPanel = (restoreFocus = false) => {
            if (panel && window.ompDashboardWidgetPopups?.isOpenFor?.(panel)) {
                window.ompDashboardWidgetPopups.close({ restoreFocus });
                return;
            }

            if (panel) {
                panel.hidden = true;
            }

            toggle?.setAttribute('aria-expanded', 'false');
        };

        const openSettingsPanel = () => {
            if (!toggle || !panel) {
                return;
            }

            const opened = window.ompDashboardWidgetPopups?.open?.({
                anchor: toggle,
                content: panel,
                ownerWidget: widget,
                ownerWidgetId: widget.dataset.userActiveWidgetId || '',
                placement: 'bottom-end',
                allowInEditMode: true,
                onClose: () => {
                    panel.hidden = true;
                    toggle.setAttribute('aria-expanded', 'false');
                }
            });

            if (!opened) {
                panel.hidden = false;
                toggle.setAttribute('aria-expanded', 'true');
            }
        };

        if (zoomToggle && zoomPanel && zoomToggle.dataset.dashboardWidgetSettingsBound !== 'true') {
            zoomToggle.dataset.dashboardWidgetSettingsBound = 'true';
            zoomToggle.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();
                const isOpen = zoomPanel.hidden;
                if (panel && isOpen) {
                    closeSettingsPanel(false);
                }

                zoomPanel.hidden = !isOpen;
                zoomToggle.setAttribute('aria-expanded', String(isOpen));
            });
        }

        if (toggle && panel && toggle.dataset.dashboardWidgetSettingsBound !== 'true') {
            toggle.dataset.dashboardWidgetSettingsBound = 'true';
            toggle.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();
                const isOpen = window.ompDashboardWidgetPopups?.isOpenFor?.(panel) || !panel.hidden;
                if (zoomPanel && !isOpen) {
                    zoomPanel.hidden = true;
                    zoomToggle?.setAttribute('aria-expanded', 'false');
                }

                if (isOpen) {
                    closeSettingsPanel(false);
                } else {
                    openSettingsPanel();
                }
            });
        }

        widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
            if (control.dataset.dashboardWidgetSettingsBound === 'true') {
                return;
            }

            control.dataset.dashboardWidgetSettingsBound = 'true';
            control.addEventListener('change', () => {
                widget.dataset.widgetIntData = normalizeWidgetDataValue(control.value);
                applyWidgetSettings(widget);
                onChange();
            });
        });

        widget.querySelectorAll('[data-widget-entry-filter-control]').forEach((control) => {
            if (control.dataset.dashboardWidgetSettingsBound === 'true') {
                return;
            }

            control.dataset.dashboardWidgetSettingsBound = 'true';
            control.addEventListener('change', () => {
                widget.dataset.widgetStringData = normalizeMainModuleFilterValue(control.value);
                applyWidgetSettings(widget);
                onChange();
            });
        });

        widget.querySelectorAll('[data-widget-preset-zoom-control]').forEach((control) => {
            if (control.dataset.dashboardWidgetSettingsBound === 'true') {
                return;
            }

            control.dataset.dashboardWidgetSettingsBound = 'true';
            control.addEventListener('change', () => {
                const preset = normalizePresetZoom(control.value);
                widget.dataset.widgetStringData = preset === 'default' ? '' : preset;
                applyWidgetSettings(widget);
                onChange();
            });
        });

        widget.querySelectorAll('[data-blank-widget-style-control]').forEach((control) => {
            if (control.dataset.dashboardWidgetSettingsBound === 'true') {
                return;
            }

            control.dataset.dashboardWidgetSettingsBound = 'true';
            control.addEventListener('change', () => {
                applyBlankWidgetStyleValue(widget, control.value);
                applyWidgetSettings(widget);
                onChange();
            });
        });

        if (isBlankWidgetPayload(widget.dataset.widgetPayload)) {
            refreshBlankWidgetImageOptions(root).catch(() => {});
        }

        widget.querySelectorAll('[data-widget-content-scale-control]').forEach((container) => {
            if (container.dataset.dashboardWidgetSettingsBound === 'true') {
                return;
            }

            container.dataset.dashboardWidgetSettingsBound = 'true';
            const range = container.querySelector('[data-widget-content-scale-range]');
            const input = container.querySelector('[data-widget-content-scale-input]');
            const reset = container.querySelector('[data-widget-content-scale-reset]');
            const setScale = (value, commit) => {
                widget.dataset.widgetContentScale = String(normalizeContentScale(value));
                applyWidgetSettings(widget);
                if (commit) {
                    onChange();
                }
            };

            range?.addEventListener('input', () => setScale(range.value, false));
            range?.addEventListener('change', () => setScale(range.value, true));
            input?.addEventListener('input', () => {
                if (parseNullableInteger(input.value) !== null) {
                    setScale(input.value, false);
                }
            });
            input?.addEventListener('change', () => setScale(input.value, true));
            input?.addEventListener('keydown', (event) => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    setScale(input.value, true);
                    input.blur();
                }
            });
            reset?.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();
                setScale(100, true);
            });
        });
    }

    function bindWidgetTitlebarToggle(widget, onChange = () => {}) {
        const toggle = widget.querySelector('[data-widget-titlebar-toggle]');
        if (!toggle || toggle.dataset.dashboardWidgetTitlebarBound === 'true') {
            return;
        }

        toggle.dataset.dashboardWidgetTitlebarBound = 'true';
        toggle.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            const next = widget.dataset.widgetTitlebarHidden !== 'true';
            widget.dataset.widgetTitlebarHidden = next ? 'true' : 'false';
            applyWidgetSettings(widget);
            onChange();
        });
    }

    function applyWidgetSettings(widget) {
        const contentScale = normalizeContentScale(widget.dataset.widgetContentScale);
        widget.dataset.widgetContentScale = String(contentScale);
        const usesPresetZoom = isPresetZoomWidgetPayload(widget.dataset.widgetPayload);
        widget.style.setProperty('--dashboard-widget-content-scale-factor', formatScaleFactor(usesPresetZoom ? 100 : contentScale));
        syncWidgetContentScaleControls(widget, contentScale);
        if (usesPresetZoom) {
            const presetZoom = getWidgetPresetZoom(widget);
            widget.dataset.widgetPresetZoom = presetZoom;
            widget.dataset.widgetStringData = presetZoom === 'default' ? '' : presetZoom;
            if (widget.dataset.widgetPayload === 'admin-overview') {
                widget.dataset.widgetIntData = '0';
            }
            widget.querySelectorAll('[data-widget-preset-zoom-control]').forEach((control) => {
                control.value = presetZoom;
            });
        } else {
            widget.dataset.widgetPresetZoom = '';
        }
        const hideTitlebar = widget.dataset.widgetTitlebarHidden === 'true';
        widget.classList.toggle('is-titlebar-hidden', hideTitlebar);
        widget.querySelectorAll('[data-widget-titlebar-toggle]').forEach((button) => {
            button.setAttribute('aria-pressed', hideTitlebar ? 'true' : 'false');
        });

        if (widget.dataset.widgetPayload === 'weekday-date') {
            const hideWeekNumber = parseNullableInteger(widget.dataset.widgetIntData) === 1;
            widget.querySelectorAll('.dashboard-date-widget__week').forEach((week) => {
                week.hidden = hideWeekNumber;
            });
            widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
                control.value = hideWeekNumber ? '1' : '0';
            });
            return;
        }

        if (widget.dataset.widgetPayload === 'admin-overview') {
            const size = normalizeAdminOverviewSize(widget.dataset.widgetIntData);
            widget.dataset.widgetIntData = String(size);
            widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
                control.value = String(size);
            });
            return;
        }

        if (isColumnCountWidgetPayload(widget.dataset.widgetPayload)) {
            const columns = normalizeColumnCount(widget.dataset.widgetIntData);
            widget.dataset.widgetColumns = String(columns);
            widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
                control.value = String(columns);
            });
        }

        if (isMainModuleFilterWidgetPayload(widget.dataset.widgetPayload)) {
            const filterMode = normalizeMainModuleFilterValue(widget.dataset.widgetStringData);
            widget.dataset.widgetStringData = filterMode;
            widget.querySelectorAll('[data-widget-entry-filter-control]').forEach((control) => {
                control.value = filterMode;
            });
            widget.querySelectorAll('[data-dashboard-entry-list]').forEach(applyEntryListFilter);
            return;
        }

        if (isColumnCountWidgetPayload(widget.dataset.widgetPayload)) {
            return;
        }

        if (isBlankWidgetPayload(widget.dataset.widgetPayload)) {
            const imageId = normalizeBlankWidgetImageId(widget.dataset.widgetStringData);
            const variant = normalizeBlankWidgetVariant(widget.dataset.widgetIntData);
            widget.dataset.widgetStringData = imageId > 0 ? String(imageId) : '';
            widget.dataset.widgetIntData = imageId > 0 ? '' : variant > 0 ? String(variant) : '';
            syncBlankWidgetStyleControls(widget, imageId > 0 ? 0 : variant, imageId);
            renderBlankWidgetVariant(widget, imageId > 0 ? 0 : variant, imageId);
        }
    }

    function bindEntryFavoriteToggles(root, scope, token) {
        scope.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
            if (button.dataset.dashboardFavoriteBound === 'true') {
                return;
            }

            button.dataset.dashboardFavoriteBound = 'true';
            button.addEventListener('click', async (event) => {
                event.preventDefault();
                event.stopPropagation();

                const row = button.closest('[data-dashboard-entry-row]');
                const entryKey = row?.dataset.entryKey || '';
                if (!entryKey || button.disabled) {
                    return;
                }

                button.disabled = true;
                try {
                    const payload = await postForm(root.dataset.favoriteToggleUrl, token, { entryKey });
                    if (payload?.entryKey) {
                        const enrichedPayload = enrichFavoritePayloadFromRow(payload, row);
                        updateDashboardEntryFavoriteState(root, enrichedPayload.entryKey, enrichedPayload.isFavorite, row, enrichedPayload);
                        dispatchFavoriteChanged(enrichedPayload);
                    }
                } finally {
                    button.disabled = false;
                }
            });
        });
    }

    function updateDashboardEntryFavoriteState(root, entryKey, isFavorite, sourceRow, payload) {
        getEntryRows(root, entryKey).forEach((row) => {
            updateEntryRowFavoriteState(root, row, isFavorite);
        });

        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';
        const source = findSourceEntryRow(root, entryKey, sourceRow);

        [...getEntryLists(root, 'favorites'), ...getEntryLists(root, 'combo-favorites')].forEach((list) => {
            const existing = Array.from(list.querySelectorAll('[data-dashboard-entry-row]'))
                .find((row) => row.dataset.entryKey === entryKey);

            if (isFavorite && !existing) {
                if (source) {
                    const clone = source.cloneNode(true);
                    clone.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
                        delete button.dataset.dashboardFavoriteBound;
                    });
                    updateEntryRowFavoriteState(root, clone, true);
                    list.appendChild(clone);
                    bindEntryFavoriteToggles(root, clone, token);
                } else if (payload) {
                    const row = createEntryRowFromFavoritePayload(root, payload, true);
                    if (row) {
                        list.appendChild(row);
                        bindEntryFavoriteToggles(root, row, token);
                    }
                }
            }

            if (!isFavorite) {
                list.querySelectorAll('[data-dashboard-entry-row]').forEach((row) => {
                    if (row.dataset.entryKey === entryKey) {
                        row.remove();
                    }
                });
            }

            applyEntryListFilter(list);
            updateEntryListEmptyState(list);
        });

        getEntryLists(root, 'combo-all').forEach((list) => {
            const existing = Array.from(list.querySelectorAll('[data-dashboard-entry-row]'))
                .find((row) => row.dataset.entryKey === entryKey);

            if (isFavorite) {
                list.querySelectorAll('[data-dashboard-entry-row]').forEach((row) => {
                    if (row.dataset.entryKey === entryKey) {
                        row.remove();
                    }
                });
            } else if (!existing) {
                if (source) {
                    const clone = source.cloneNode(true);
                    clone.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
                        delete button.dataset.dashboardFavoriteBound;
                    });
                    updateEntryRowFavoriteState(root, clone, false);
                    list.appendChild(clone);
                    bindEntryFavoriteToggles(root, clone, token);
                } else if (payload) {
                    const row = createEntryRowFromFavoritePayload(root, payload, false);
                    if (row) {
                        list.appendChild(row);
                        bindEntryFavoriteToggles(root, row, token);
                    }
                }
            }

            applyEntryListFilter(list);
            updateEntryListEmptyState(list);
        });
    }

    function normalizeFavoritePayload(payload) {
        if (!payload?.entryKey) {
            return null;
        }

        return {
            source: payload.source || '',
            isFavorite: !!payload.isFavorite,
            entryKey: payload.entryKey || '',
            appInstanceId: payload.appInstanceId || '',
            href: payload.href || '',
            groupTitle: payload.groupTitle || '',
            entryTitle: payload.entryTitle || '',
            contextName: payload.contextName || payload.groupTitle || '',
            label: payload.label || '',
            description: payload.description || '',
            logoUrl: payload.logoUrl || '',
            logoFallback: payload.logoFallback || ''
        };
    }

    function enrichFavoritePayloadFromRow(payload, row) {
        const normalized = normalizeFavoritePayload(payload);
        if (!normalized || !row) {
            return normalized;
        }

        normalized.href = normalized.href || row.dataset.entryHref || '';
        normalized.entryTitle = normalized.entryTitle || row.dataset.entryTitle || '';
        normalized.contextName = normalized.contextName || row.dataset.entryContext || '';
        normalized.groupTitle = normalized.groupTitle || normalized.contextName;
        normalized.description = normalized.description || row.dataset.entryDescription || '';
        normalized.logoUrl = normalized.logoUrl || row.dataset.entryLogoUrl || '';
        normalized.logoFallback = normalized.logoFallback || row.dataset.entryLogoFallback || '';
        normalized.label = normalized.label || normalized.entryTitle || row.dataset.entryTitle || normalized.entryKey;
        return normalized;
    }

    function dispatchFavoriteChanged(payload) {
        const normalized = normalizeFavoritePayload(payload);
        if (!normalized) {
            return;
        }

        normalized.source = 'dashboard';
        window.dispatchEvent(new CustomEvent(favoriteChangedEvent, { detail: normalized }));
    }

    function createEntryRowFromFavoritePayload(root, payload, isFavorite = true) {
        const normalized = normalizeFavoritePayload(payload);
        if (!normalized) {
            return null;
        }

        const title = normalized.entryTitle || normalized.label || normalized.entryKey;
        const contextName = normalized.contextName || normalized.groupTitle || '';
        const href = normalized.href || '#';
        const logoFallback = normalized.logoFallback || buildLogoFallback(title);
        const row = document.createElement('div');
        row.className = 'dashboard-entry-list__row';
        row.setAttribute('data-dashboard-entry-row', '');
        row.dataset.entryKey = normalized.entryKey;
        row.dataset.entryTitle = title;
        row.dataset.entryContext = contextName;
        row.dataset.entryDescription = normalized.description;
        row.dataset.entryHref = href;
        row.dataset.entryLogoUrl = normalized.logoUrl;
        row.dataset.entryLogoFallback = logoFallback;

        const link = document.createElement('a');
        link.className = 'dashboard-entry-list__item';
        link.href = href;

        const logo = document.createElement('span');
        logo.className = 'dashboard-entry-list__logo';
        if (normalized.logoUrl) {
            const image = document.createElement('img');
            image.src = normalized.logoUrl;
            image.alt = '';
            logo.appendChild(image);
        } else {
            const fallback = document.createElement('span');
            fallback.textContent = logoFallback;
            logo.appendChild(fallback);
        }

        const text = document.createElement('span');
        text.className = 'dashboard-entry-list__text';

        const titleElement = document.createElement('span');
        titleElement.className = 'dashboard-entry-list__title';
        titleElement.textContent = title;
        text.appendChild(titleElement);

        if (contextName) {
            const context = document.createElement('span');
            context.className = 'dashboard-entry-list__context';
            context.textContent = contextName;
            text.appendChild(context);
        }

        if (normalized.description) {
            const description = document.createElement('span');
            description.className = 'dashboard-entry-list__description';
            description.textContent = normalized.description;
            text.appendChild(description);
        }

        link.appendChild(logo);
        link.appendChild(text);

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'dashboard-entry-list__favorite-toggle is-favorite';
        button.setAttribute('data-dashboard-entry-favorite-toggle', '');
        row.appendChild(link);
        row.appendChild(button);
        updateEntryRowFavoriteState(root, row, isFavorite);
        return row;
    }

    function buildLogoFallback(title) {
        const parts = String(title || '').trim().split(/\s+/).filter(Boolean);
        if (parts.length === 0) {
            return '?';
        }

        return parts.slice(0, 2).map((part) => part[0]).join('').toUpperCase();
    }

    function handleExternalFavoriteChanged(event) {
        const payload = normalizeFavoritePayload(event.detail);
        if (!payload || payload.source === 'dashboard') {
            return;
        }

        document.querySelectorAll('[data-dashboard-root]').forEach((root) => {
            updateDashboardEntryFavoriteState(root, payload.entryKey, payload.isFavorite, null, payload);
        });
    }

    function updateEntryRowFavoriteState(root, row, isFavorite) {
        row.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
            const label = isFavorite
                ? root.dataset.removeFavoriteLabel || 'Remove favorite'
                : root.dataset.addFavoriteLabel || 'Add favorite';
            button.classList.toggle('is-favorite', isFavorite);
            button.textContent = isFavorite ? '★' : '☆';
            button.title = label;
            button.setAttribute('aria-label', label);
        });
    }

    function findSourceEntryRow(root, entryKey, fallback) {
        const allRows = [...getEntryLists(root, 'all'), ...getEntryLists(root, 'combo-all')]
            .flatMap((list) => Array.from(list.querySelectorAll('[data-dashboard-entry-row]')));
        return allRows.find((row) => row.dataset.entryKey === entryKey)
            || fallback
            || getEntryRows(root, entryKey)[0]
            || null;
    }

    function getEntryRows(root, entryKey) {
        const selector = `[data-dashboard-entry-row][data-entry-key="${cssEscape(entryKey)}"]`;
        return [
            ...root.querySelectorAll(selector),
            ...getTemplateContentMatches(root, selector)
        ];
    }

    function getEntryLists(root, kind) {
        const selector = `[data-dashboard-entry-list][data-dashboard-entry-list-kind="${kind}"]`;
        return [
            ...root.querySelectorAll(selector),
            ...getTemplateContentMatches(root, selector)
        ];
    }

    function getTemplateContentMatches(root, selector) {
        return Array.from(root.querySelectorAll('template[data-dashboard-widget-template]'))
            .flatMap((template) => Array.from(template.content.querySelectorAll(selector)));
    }

    function updateEntryListEmptyState(list) {
        const empty = list.querySelector('[data-dashboard-entry-list-empty]');
        if (empty) {
            const combo = list.closest('[data-dashboard-entry-combo-list]');
            const comboFilter = combo?.querySelector('[data-dashboard-entry-filter]');
            if (combo && normalizeSearchText(comboFilter?.value || '').length > 0) {
                const comboLists = Array.from(combo.querySelectorAll('[data-dashboard-entry-list]'));
                const anyVisible = comboLists.some((comboList) => Array.from(comboList.querySelectorAll('[data-dashboard-entry-row]'))
                    .some((row) => !row.hidden));
                comboLists.forEach((comboList, index) => {
                    const comboEmpty = comboList.querySelector('[data-dashboard-entry-list-empty]');
                    if (comboEmpty) {
                        comboEmpty.hidden = anyVisible || index > 0;
                    }
                });
                return;
            }

            empty.hidden = Array.from(list.querySelectorAll('[data-dashboard-entry-row]'))
                .some((row) => !row.hidden);
        }
    }

    function bindEntryListFilters(scope) {
        scope.querySelectorAll('[data-dashboard-entry-filter]').forEach((input) => {
            if (input.dataset.dashboardEntryFilterBound === 'true') {
                return;
            }

            input.dataset.dashboardEntryFilterBound = 'true';
            input.addEventListener('input', () => {
                getFilterLists(input).forEach(applyEntryListFilter);
            });
            getFilterLists(input).forEach(applyEntryListFilter);
        });
    }

    function getFilterLists(input) {
        const combo = input.closest('[data-dashboard-entry-combo-list]');
        if (combo) {
            return Array.from(combo.querySelectorAll('[data-dashboard-entry-list]'));
        }

        const list = input.closest('[data-dashboard-entry-list]');
        return list ? [list] : [];
    }

    function applyEntryListFilter(list) {
        const filterInput = list.closest('[data-dashboard-entry-combo-list]')?.querySelector('[data-dashboard-entry-filter]')
            || list.querySelector('[data-dashboard-entry-filter]');
        const query = normalizeSearchText(filterInput?.value || '');
        const isFiltering = query.length > 0;
        const widget = list.closest('[data-dashboard-widget]');
        const mainOnly = isMainModuleFilterWidgetPayload(widget?.dataset?.widgetPayload)
            && normalizeMainModuleFilterValue(widget?.dataset?.widgetStringData) === 'main-only';

        list.querySelectorAll('[data-dashboard-entry-row]').forEach((row) => {
            const hiddenBySearch = isFiltering && !getEntryRowSearchText(row).includes(query);
            const hiddenByMainOnly = mainOnly && row.dataset.entryIsChild === 'true';
            row.hidden = hiddenBySearch || hiddenByMainOnly;
        });

        list.querySelectorAll('[data-dashboard-entry-list-section]').forEach((section) => {
            section.hidden = isFiltering;
        });
        updateEntryListEmptyState(list);
    }

    function getEntryRowSearchText(row) {
        return normalizeSearchText([
            row.dataset.entryTitle,
            row.dataset.entryContext,
            row.dataset.entryDescription,
            row.dataset.entryHref,
            row.dataset.entryKey,
            row.textContent
        ].join(' '));
    }

    function bindDashboardNotifications(scope) {
        const feeds = [];
        if (scope?.matches?.('[data-dashboard-notification-feed]')) {
            feeds.push(scope);
        }

        scope?.querySelectorAll?.('[data-dashboard-notification-feed]').forEach((feed) => {
            feeds.push(feed);
        });

        feeds.forEach(initDashboardNotificationFeed);
    }

    function initDashboardNotificationFeed(feed) {
        if (!feed || feed.dataset.dashboardNotificationFeedBound === 'true') {
            return;
        }

        feed.dataset.dashboardNotificationFeedBound = 'true';
        bindDashboardNotificationForms(feed);
        updateDashboardNotificationEmptyState(feed);

        if (feed.dataset.canUseNotifications !== 'true') {
            return;
        }

        feed.addEventListener('scroll', () => {
            if (feed.scrollTop + feed.clientHeight >= feed.scrollHeight - 80) {
                loadMoreDashboardNotifications(feed);
            }
        });

        const list = feed.querySelector('[data-dashboard-notification-list]');
        if (list && !list.querySelector('[data-dashboard-notification-form]')) {
            loadMoreDashboardNotifications(feed);
        }
    }

    function bindDashboardNotificationForms(scope) {
        scope.querySelectorAll('[data-dashboard-notification-form]').forEach((form) => {
            if (form.dataset.dashboardNotificationFormBound === 'true') {
                return;
            }

            form.dataset.dashboardNotificationFormBound = 'true';
            form.addEventListener('submit', async (event) => {
                event.preventDefault();
                const root = form.closest('[data-dashboard-root]');
                const token = root?.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';
                const notificationId = form.dataset.notificationId || form.querySelector('input[name="notificationId"]')?.value || '';
                if (!root || !notificationId) {
                    return;
                }

                try {
                    const payload = await postForm(form.action, token, { notificationId });
                    markDashboardNotificationRead(root, notificationId, payload?.unreadCount);
                    const destinationUrl = payload?.destinationUrl || form.querySelector('[data-destination-url]')?.dataset.destinationUrl || '';
                    if (destinationUrl) {
                        window.location.href = destinationUrl;
                    }
                } catch (error) {
                    handleDashboardSaveError(root, error);
                }
            });
        });
    }

    function markDashboardNotificationRead(root, notificationId, unreadCount) {
        setDashboardNotificationRowsRead(root, notificationId, false);

        window.dispatchEvent(new CustomEvent(notificationChangedEvent, {
            detail: {
                source: 'dashboard',
                notificationId: String(notificationId),
                unreadCount: Number.isFinite(Number(unreadCount)) ? Number(unreadCount) : null
            }
        }));
    }

    function setDashboardNotificationRowsRead(root, notificationId, allRead) {
        root.querySelectorAll('[data-dashboard-notification-form]').forEach((form) => {
            if (!allRead && form.dataset.notificationId !== String(notificationId)) {
                return;
            }

            form.classList.remove('is-unread');
            form.classList.add('is-read');
        });
    }

    function handleExternalNotificationChanged(event) {
        const detail = event.detail || {};
        const notificationId = String(detail.notificationId || '');
        document.querySelectorAll('[data-dashboard-root]').forEach((root) => {
            if (detail.allRead) {
                setDashboardNotificationRowsRead(root, '', true);
                return;
            }

            if (notificationId) {
                setDashboardNotificationRowsRead(root, notificationId, false);
            }
        });
    }

    function getDashboardNotificationCursor(feed) {
        const rows = feed.querySelectorAll('[data-dashboard-notification-form]');
        if (rows.length === 0) {
            return null;
        }

        const last = rows[rows.length - 1];
        return {
            beforeCreatedAt: last.dataset.notificationCreatedAt || '',
            beforeNotificationId: last.dataset.notificationId || ''
        };
    }

    async function loadMoreDashboardNotifications(feed) {
        const list = feed.querySelector('[data-dashboard-notification-list]');
        const loadUrl = feed.dataset.loadUrl || '';
        if (!list || !loadUrl || feed.dataset.notificationLoading === 'true' || feed.dataset.notificationEnd === 'true') {
            return;
        }

        const url = new URL(loadUrl, window.location.href);
        url.searchParams.set('limit', feed.dataset.pageSize || '20');
        const cursor = getDashboardNotificationCursor(feed);
        if (cursor && cursor.beforeCreatedAt && cursor.beforeNotificationId) {
            url.searchParams.set('beforeCreatedAt', cursor.beforeCreatedAt);
            url.searchParams.set('beforeNotificationId', cursor.beforeNotificationId);
        }

        feed.dataset.notificationLoading = 'true';
        setDashboardNotificationLazyState(feed, true, false);
        try {
            const response = await fetch(url.toString(), {
                credentials: 'same-origin',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403 || isDashboardLoginRedirect(response)) {
                throw createDashboardRequestError('auth', `Notification list failed with status ${response.status}.`, response.status);
            }

            if (!response.ok) {
                throw createDashboardRequestError('server', `Notification list failed with status ${response.status}.`, response.status);
            }

            const payload = await response.json();
            const items = Array.isArray(payload?.items) ? payload.items : [];
            items.forEach((item) => {
                list.appendChild(createDashboardNotificationForm(feed, item));
            });
            bindDashboardNotificationForms(feed);
            if (!payload?.hasMore || items.length === 0) {
                feed.dataset.notificationEnd = 'true';
            }
            updateDashboardNotificationEmptyState(feed);
            setDashboardNotificationLazyState(feed, false, feed.dataset.notificationEnd === 'true' && !!list.querySelector('[data-dashboard-notification-form]'));
        } catch (error) {
            const root = feed.closest('[data-dashboard-root]');
            if (root) {
                handleDashboardSaveError(root, error);
            }
            setDashboardNotificationLazyState(feed, false, false);
        } finally {
            feed.dataset.notificationLoading = 'false';
        }
    }

    function createDashboardNotificationForm(feed, item) {
        const form = document.createElement('form');
        form.method = 'post';
        form.action = feed.dataset.markReadUrl || '/notifications/mark-read';
        form.className = `dashboard-notification-feed__form ${item?.isUnread ? 'is-unread' : 'is-read'}`;
        form.dataset.dashboardNotificationForm = '';
        form.dataset.notificationId = String(item?.notificationId || '');
        form.dataset.notificationCreatedAt = item?.createdAt || '';

        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = 'notificationId';
        input.value = String(item?.notificationId || '');
        form.appendChild(input);

        const button = document.createElement('button');
        button.type = 'submit';
        button.className = `dashboard-notification-feed__row dashboard-notification-feed__row--${notificationLevelClass(item?.level)}`;
        button.dataset.destinationUrl = item?.destinationUrl || '';

        const icon = document.createElement('span');
        icon.className = 'dashboard-notification-feed__icon';
        icon.setAttribute('aria-hidden', 'true');
        if (item?.callerIcon) {
            const image = document.createElement('img');
            image.src = item.callerIcon;
            image.alt = '';
            icon.appendChild(image);
        } else {
            const fallback = document.createElement('span');
            fallback.textContent = notificationCallerText(item);
            icon.appendChild(fallback);
        }

        const copy = document.createElement('span');
        copy.className = 'dashboard-notification-feed__copy';
        const title = document.createElement('span');
        title.className = 'dashboard-notification-feed__title';
        title.textContent = item?.title || '';
        const content = document.createElement('span');
        content.className = 'dashboard-notification-feed__content';
        content.textContent = item?.content || '';
        const meta = document.createElement('span');
        meta.className = 'dashboard-notification-feed__meta';
        meta.textContent = formatNotificationTime(item?.createdAt);
        copy.append(title, content, meta);
        button.append(icon, copy);
        form.appendChild(button);
        return form;
    }

    function notificationCallerText(item) {
        const source = String(item?.callerDisplayName || item?.callerKey || '').trim();
        return source ? source[0].toUpperCase() : '!';
    }

    function notificationLevelClass(level) {
        const normalized = String(level || 'info').toLowerCase();
        return /^(info|success|warning|error)$/.test(normalized) ? normalized : 'info';
    }

    function formatNotificationTime(value) {
        if (!value) {
            return '';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '';
        }

        return date.toLocaleString();
    }

    function setDashboardNotificationLazyState(feed, loading, ended) {
        const loadingEl = feed.querySelector('[data-dashboard-notification-loading]');
        const endEl = feed.querySelector('[data-dashboard-notification-end]');
        if (loadingEl) {
            loadingEl.hidden = !loading;
        }

        if (endEl) {
            endEl.hidden = !ended;
        }
    }

    function updateDashboardNotificationEmptyState(feed) {
        const empty = feed.querySelector('[data-dashboard-notification-empty]');
        const list = feed.querySelector('[data-dashboard-notification-list]');
        if (empty && list) {
            empty.hidden = !!list.querySelector('[data-dashboard-notification-form]');
        }
    }

    function bindDashboardMusicPlayers(scope) {
        getDashboardMusicPlayers(scope).forEach((player) => {
            if (player.dataset.dashboardMusicBound === 'true') {
                return;
            }

            player.dataset.dashboardMusicBound = 'true';
            initDashboardMusicPlayer(player);
        });
    }

    async function initDashboardMusicPlayer(player) {
        const audio = player.querySelector('[data-music-audio]');
        const title = player.querySelector('[data-music-title]');
        const artist = player.querySelector('[data-music-artist]');
        const attribution = player.querySelector('[data-music-attribution]');
        const status = player.querySelector('[data-music-status]');
        const seek = player.querySelector('[data-music-seek]');
        const current = player.querySelector('[data-music-current]');
        const duration = player.querySelector('[data-music-duration]');
        const play = player.querySelector('[data-music-play]');
        const previous = player.querySelector('[data-music-previous]');
        const next = player.querySelector('[data-music-next]');
        const shuffle = player.querySelector('[data-music-shuffle]');
        const loop = player.querySelector('[data-music-loop]');
        const files = player.querySelector('[data-music-files]');
        const adminOpen = player.querySelector('[data-music-admin-open]');
        if (!audio || !play) {
            return;
        }

        const state = {
            tracks: [],
            index: 0,
            shuffle: false,
            loop: false,
            seeking: false,
            errorAdvanceTimer: null
        };

        const setStatus = (message) => {
            if (status) {
                status.textContent = message || '';
            }
        };

        const setTrackText = (track) => {
            if (title) {
                title.textContent = track?.title || player.dataset.noTracksLabel || 'No tracks available';
            }
            if (artist) {
                artist.textContent = track?.artist || player.dataset.localArtistLabel || '';
            }
            if (attribution) {
                setMusicAttribution(attribution, track);
            }
        };

        const updatePlayState = () => {
            const isPlaying = !audio.paused && !audio.ended;
            player.classList.toggle('is-playing', isPlaying);
            const label = isPlaying
                ? play.dataset.pauseLabel || 'Pause'
                : play.dataset.playLabel || 'Play';
            play.setAttribute('aria-label', label);
            play.title = label;
        };

        const updateProgress = () => {
            const total = Number.isFinite(audio.duration) ? audio.duration : 0;
            const position = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
            if (current) {
                current.textContent = formatMusicTime(position);
            }
            if (duration) {
                duration.textContent = formatMusicTime(total);
            }
            if (seek && !state.seeking) {
                seek.value = total > 0 ? String((position / total) * 100) : '0';
            }
        };

        const setTrack = (index, autoplay = false) => {
            if (state.errorAdvanceTimer) {
                window.clearTimeout(state.errorAdvanceTimer);
                state.errorAdvanceTimer = null;
            }

            if (state.tracks.length === 0) {
                audio.removeAttribute('src');
                setTrackText(null);
                setStatus(player.dataset.noTracksLabel || 'No tracks available');
                updateProgress();
                updatePlayState();
                return;
            }

            state.index = normalizeTrackIndex(index, state.tracks.length);
            const track = state.tracks[state.index];
            audio.src = track.src;
            setTrackText(track);
            setStatus(`${state.index + 1} / ${state.tracks.length}`);
            updateProgress();

            if (autoplay) {
                playAudio(audio, player);
            }
        };

        const move = (direction, autoplay = true) => {
            if (state.tracks.length === 0) {
                return;
            }

            if (state.shuffle && state.tracks.length > 1) {
                let nextIndex = state.index;
                while (nextIndex === state.index) {
                    nextIndex = Math.floor(Math.random() * state.tracks.length);
                }
                setTrack(nextIndex, autoplay);
                return;
            }

            const nextIndex = state.index + direction;
            if (nextIndex >= state.tracks.length && !state.loop) {
                setTrack(state.tracks.length - 1, false);
                return;
            }
            if (nextIndex < 0 && !state.loop) {
                setTrack(0, false);
                return;
            }

            setTrack(nextIndex, autoplay);
        };

        const refreshServerPlaylist = async (preferredIndex = 0, autoplay = false) => {
            state.tracks = await loadMusicPlaylist(player.dataset.playlistUrl || '');
            const nextIndex = Math.min(Math.max(preferredIndex, 0), Math.max(state.tracks.length - 1, 0));
            setTrack(nextIndex, autoplay);
        };

        play.addEventListener('click', () => {
            if (!audio.src && state.tracks.length > 0) {
                setTrack(state.index, false);
            }

            if (audio.paused || audio.ended) {
                playAudio(audio, player);
            } else {
                audio.pause();
            }
        });

        previous?.addEventListener('click', () => move(-1));
        next?.addEventListener('click', () => move(1));
        shuffle?.addEventListener('click', () => {
            state.shuffle = !state.shuffle;
            shuffle.setAttribute('aria-pressed', String(state.shuffle));
        });
        loop?.addEventListener('click', () => {
            state.loop = !state.loop;
            loop.setAttribute('aria-pressed', String(state.loop));
        });

        seek?.addEventListener('input', () => {
            state.seeking = true;
        });
        seek?.addEventListener('change', () => {
            const total = Number.isFinite(audio.duration) ? audio.duration : 0;
            if (total > 0) {
                audio.currentTime = total * ((parseFloat(seek.value || '0') || 0) / 100);
            }
            state.seeking = false;
            updateProgress();
        });

        audio.addEventListener('play', updatePlayState);
        audio.addEventListener('pause', updatePlayState);
        audio.addEventListener('ended', () => {
            updatePlayState();
            move(1, true);
        });
        audio.addEventListener('loadedmetadata', updateProgress);
        audio.addEventListener('timeupdate', updateProgress);
        audio.addEventListener('error', () => {
            const failedSource = audio.currentSrc || audio.src;
            setStatus(player.dataset.errorLabel || 'Could not play track');
            updatePlayState();
            if (state.tracks.length <= 1) {
                return;
            }

            if (state.errorAdvanceTimer) {
                window.clearTimeout(state.errorAdvanceTimer);
            }

            state.errorAdvanceTimer = window.setTimeout(() => {
                state.errorAdvanceTimer = null;
                const track = state.tracks[state.index];
                const currentSource = audio.currentSrc || audio.src;
                if (!track || track.src !== failedSource || currentSource !== failedSource) {
                    return;
                }

                if (state.index >= state.tracks.length - 1 && !state.loop) {
                    return;
                }

                move(1, true);
            }, 1500);
        });

        bindMusicPlayerAdmin(player, state, setTrack, refreshServerPlaylist, setStatus);

        files?.addEventListener('change', () => {
            const previousCount = state.tracks.length;
            addLocalMusicFiles(state, files.files, player);
            if (state.tracks.length > previousCount) {
                setTrack(previousCount, true);
            }
            files.value = '';
        });

        player.addEventListener('dragover', (event) => {
            if (!hasMusicTransfer(event.dataTransfer)) {
                return;
            }
            event.preventDefault();
            player.classList.add('is-dragover');
        });
        player.addEventListener('dragleave', () => {
            player.classList.remove('is-dragover');
        });
        player.addEventListener('drop', (event) => {
            if (!hasMusicTransfer(event.dataTransfer)) {
                return;
            }
            event.preventDefault();
            player.classList.remove('is-dragover');
            const previousCount = state.tracks.length;
            addLocalMusicFiles(state, event.dataTransfer?.files, player);
            if (state.tracks.length > previousCount) {
                setTrack(previousCount, true);
            }
        });

        setStatus(player.dataset.loadingLabel || 'Loading playlist');
        await refreshServerPlaylist(0, false);
        if (!adminOpen) {
            player.classList.add('dashboard-music-player--no-admin');
        }
    }

    async function playAudio(audio, player) {
        try {
            await audio.play();
        } catch {
            const status = player.querySelector('[data-music-status]');
            if (status) {
                status.textContent = player.dataset.errorLabel || 'Could not play track';
            }
        }
    }

    async function loadMusicPlaylist(playlistUrl) {
        if (!playlistUrl) {
            return [];
        }

        try {
            const response = await fetch(playlistUrl, { credentials: 'same-origin' });
            if (!response.ok) {
                return [];
            }

            const json = await response.json();
            const baseUrl = new URL(response.url || playlistUrl, document.baseURI);
            return Array.isArray(json.tracks)
                ? json.tracks.map((track) => normalizePlaylistTrack(track, baseUrl)).filter(Boolean)
                : [];
        } catch {
            return [];
        }
    }

    function normalizePlaylistTrack(track, baseUrl) {
        if (!track || typeof track !== 'object') {
            return null;
        }

        const rawSource = String(track.src || track.url || '').trim();
        if (!rawSource) {
            return null;
        }

        let source;
        try {
            source = new URL(rawSource, baseUrl).toString();
        } catch {
            return null;
        }

        return {
            src: source,
            title: String(track.title || getFileStem(rawSource) || 'Track').trim(),
            artist: String(track.artist || '').trim(),
            attribution: String(track.attribution || '').trim(),
            source: String(track.source || track.sourceUrl || '').trim(),
            description: String(track.description || '').trim()
        };
    }

    function addLocalMusicFiles(state, fileList, player) {
        const files = Array.from(fileList || [])
            .filter(isMusicFile);
        files.forEach((file) => {
            const source = URL.createObjectURL(file);
            registerMusicObjectUrl(player, source);
            state.tracks.push({
                src: source,
                title: getFileStem(file.name) || file.name,
                artist: player.dataset.localArtistLabel || 'Local file',
                attribution: '',
                source: '',
                description: ''
            });
        });
    }

    function bindMusicPlayerAdmin(player, state, setTrack, refreshServerPlaylist, setStatus) {
        const open = player.querySelector('[data-music-admin-open]');
        const panel = player.querySelector('[data-music-admin-panel]');
        const close = player.querySelector('[data-music-admin-close]');
        const upload = player.querySelector('[data-music-admin-upload]');
        const zipUpload = player.querySelector('[data-music-admin-zip-upload]');
        const zip = player.querySelector('[data-music-admin-zip]');
        const file = player.querySelector('[data-music-admin-file]');
        if (!open || !panel) {
            return;
        }

        player.__ompMusicAdminPanel = panel;
        if (!panel.id) {
            dashboardWidgetPopupId += 1;
            panel.id = `dashboard-music-admin-${dashboardWidgetPopupId}`;
        }
        open.setAttribute('aria-controls', panel.id);
        open.setAttribute('aria-expanded', 'false');

        const isPanelOpen = () => window.ompDashboardWidgetPopups?.isOpenFor?.(panel) || !panel.hidden;
        const closePanel = (restoreFocus = false) => {
            if (window.ompDashboardWidgetPopups?.isOpenFor?.(panel)) {
                window.ompDashboardWidgetPopups.close({ restoreFocus });
                return;
            }

            panel.hidden = true;
            open.setAttribute('aria-expanded', 'false');
            if (restoreFocus && open.isConnected) {
                open.focus({ preventScroll: true });
            }
        };
        const openPanel = () => {
            const opened = window.ompDashboardWidgetPopups?.open?.({
                anchor: open,
                content: panel,
                ownerWidget: player.closest('[data-dashboard-widget]'),
                ownerWidgetId: player.closest('[data-dashboard-widget]')?.dataset?.userActiveWidgetId || '',
                placement: 'bottom-end',
                onClose: () => {
                    panel.hidden = true;
                    open.setAttribute('aria-expanded', 'false');
                }
            });

            if (!opened) {
                panel.hidden = false;
                open.setAttribute('aria-expanded', 'true');
            }
        };

        bindMusicAdminTabs(panel);
        open.addEventListener('click', (event) => {
            event.preventDefault();
            if (isPanelOpen()) {
                closePanel(false);
                return;
            }

            openPanel();
        });
        close?.addEventListener('click', (event) => {
            event.preventDefault();
            closePanel(true);
        });

        upload?.addEventListener('click', async () => {
            const selectedFile = file?.files?.[0] || null;
            if (!isMusicFile(selectedFile)) {
                setStatus(player.dataset.selectMp3Label || 'Select an MP3 file.');
                return;
            }

            if (isMusicUploadTooLarge(player, selectedFile)) {
                setStatus(player.dataset.uploadTooLargeLabel || 'The selected file is too large for this upload surface.');
                return;
            }

            const formData = new FormData();
            formData.append('file', selectedFile);
            appendMusicAdminField(formData, player, 'title');
            appendMusicAdminField(formData, player, 'artist');
            appendMusicAdminField(formData, player, 'attribution');
            appendMusicAdminField(formData, player, 'source');
            appendMusicAdminField(formData, player, 'description');

            await submitMusicAdminForm(player, player.dataset.uploadUrl, formData, async () => {
                const previousCount = state.tracks.length;
                await refreshServerPlaylist(previousCount, false);
                clearMusicAdminTrackFields(player);
            }, setStatus, [upload]);
        });

        zipUpload?.addEventListener('click', async () => {
            const selectedZip = zip.files?.[0] || null;
            if (!selectedZip) {
                setStatus(player.dataset.selectZipLabel || 'Select a ZIP package.');
                return;
            }

            if (isMusicUploadTooLarge(player, selectedZip)) {
                setStatus(player.dataset.uploadTooLargeLabel || 'The selected file is too large for this upload surface.');
                return;
            }

            const formData = new FormData();
            formData.append('zipFile', selectedZip);
            await submitMusicAdminForm(player, player.dataset.zipUploadUrl, formData, async () => {
                await refreshServerPlaylist(state.index, false);
                zip.value = '';
                if (state.tracks.length > 0) {
                    setTrack(state.index, false);
                }
            }, setStatus, [zipUpload]);
        });
    }

    function bindMusicAdminTabs(panel) {
        const tabs = Array.from(panel.querySelectorAll('[data-music-admin-tab]'));
        const panes = Array.from(panel.querySelectorAll('[data-music-admin-pane]'));
        tabs.forEach((tab) => {
            tab.addEventListener('click', () => {
                const mode = tab.dataset.musicAdminTab || 'mp3';
                tabs.forEach((candidate) => {
                    const selected = candidate === tab;
                    candidate.classList.toggle('is-active', selected);
                    candidate.setAttribute('aria-selected', selected ? 'true' : 'false');
                });
                panes.forEach((pane) => {
                    pane.hidden = pane.dataset.musicAdminPane !== mode;
                });
            });
        });
    }

    function isMusicUploadTooLarge(player, file) {
        const maxBytes = parsePositiveInteger(player.dataset.maxUploadBytes, 0);
        return maxBytes > 0 && !!file && file.size > maxBytes;
    }

    function appendMusicAdminField(formData, player, field) {
        const input = getMusicAdminInput(player, field);
        formData.append(field, input?.value || '');
    }

    async function submitMusicAdminForm(player, url, formData, onSuccess, setStatus, busyControls = []) {
        const root = player.closest('[data-dashboard-root]');
        const token = root?.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';
        busyControls.forEach((control) => {
            if (control) {
                control.disabled = true;
            }
        });
        setStatus(player.dataset.uploadingLabel || 'Uploading music...');
        try {
            await postFormData(url, token, formData);
            setStatus(player.dataset.uploadSuccessLabel || 'Music library updated.');
            await onSuccess();
        } catch (error) {
            const message = error?.status === 413
                ? player.dataset.uploadTooLargeLabel || 'The selected file is too large for this upload surface.'
                : error?.message || player.dataset.uploadErrorLabel || 'Could not update music library.';
            setStatus(message);
        } finally {
            busyControls.forEach((control) => {
                if (control) {
                    control.disabled = false;
                }
            });
        }
    }

    function clearMusicAdminTrackFields(player) {
        ['file', 'title', 'artist', 'attribution', 'source', 'description'].forEach((field) => {
            const input = getMusicAdminInput(player, field);
            if (input) {
                input.value = '';
            }
        });
    }

    function getMusicAdminInput(player, field) {
        return player.__ompMusicAdminPanel?.querySelector(`[data-music-admin-${field}]`)
            || player.querySelector(`[data-music-admin-${field}]`);
    }

    async function loadBlankWidgetImages(root, force = false) {
        if (!force && Array.isArray(blankWidgetImagesCache)) {
            return blankWidgetImagesCache;
        }

        const url = root?.dataset?.blankWidgetImagesUrl || '';
        if (!url) {
            blankWidgetImagesCache = [];
            return blankWidgetImagesCache;
        }

        try {
            const response = await fetch(url, { credentials: 'same-origin' });
            if (!response.ok) {
                blankWidgetImagesCache = [];
                return blankWidgetImagesCache;
            }

            const payload = await response.json();
            blankWidgetImagesCache = Array.isArray(payload?.images)
                ? payload.images.map(normalizeBlankWidgetImage).filter(Boolean)
                : [];
            return blankWidgetImagesCache;
        } catch {
            blankWidgetImagesCache = [];
            return blankWidgetImagesCache;
        }
    }

    function normalizeBlankWidgetImage(image) {
        const id = normalizeBlankWidgetImageId(image?.id ?? image?.binaryDataId);
        if (id <= 0) {
            return null;
        }

        return {
            id,
            displayName: String(image.displayName || image.fileName || `Image ${id}`).trim(),
            src: String(image.src || image.url || '').trim(),
            fileName: String(image.fileName || '').trim(),
            contentType: String(image.contentType || '').trim(),
            contentHash: String(image.contentHash || image.binaryDataHash || '').trim()
        };
    }

    function bindBlankWidgetRuntimeControls(root, canvas, widget, token, state) {
        if (!isBlankWidgetPayload(widget.dataset.widgetPayload)) {
            return;
        }

        ensureBlankWidgetRuntimeMarkup(root, widget);
        const blank = widget.querySelector('[data-blank-widget]');
        if (!blank || blank.dataset.blankWidgetRuntimeBound === 'true') {
            return;
        }

        blank.dataset.blankWidgetRuntimeBound = 'true';
        const previous = blank.querySelector('[data-blank-widget-previous]');
        const next = blank.querySelector('[data-blank-widget-next]');
        const localFile = blank.querySelector('[data-blank-widget-local-file]');

        previous?.addEventListener('click', async (event) => {
            event.preventDefault();
            event.stopPropagation();
            await moveBlankWidgetChoice(root, canvas, widget, token, state, -1);
        });

        next?.addEventListener('click', async (event) => {
            event.preventDefault();
            event.stopPropagation();
            await moveBlankWidgetChoice(root, canvas, widget, token, state, 1);
        });

        localFile?.addEventListener('change', () => {
            const choices = addLocalBlankWidgetImages(root, widget, localFile.files);
            if (choices.length > 0) {
                selectBlankWidgetChoice(root, canvas, widget, token, state, choices[0], false);
            }

            localFile.value = '';
        });

        blank.addEventListener('dragover', (event) => {
            if (!hasBlankWidgetImageTransfer(event.dataTransfer)) {
                return;
            }

            event.preventDefault();
            blank.classList.add('is-dragover');
        });

        blank.addEventListener('dragleave', () => {
            blank.classList.remove('is-dragover');
        });

        blank.addEventListener('drop', (event) => {
            if (!hasBlankWidgetImageTransfer(event.dataTransfer)) {
                return;
            }

            event.preventDefault();
            blank.classList.remove('is-dragover');
            const choices = addLocalBlankWidgetImages(root, widget, event.dataTransfer?.files);
            if (choices.length > 0) {
                selectBlankWidgetChoice(root, canvas, widget, token, state, choices[0], false);
            }
        });

        bindBlankWidgetAdmin(root, widget, () => {});
        refreshBlankWidgetImageOptions(root).catch(() => {});
    }

    function ensureBlankWidgetRuntimeMarkup(root, widget) {
        const blank = widget.querySelector('[data-blank-widget]');
        if (!blank) {
            return;
        }

        let media = blank.querySelector('[data-blank-widget-media]');
        if (!media) {
            media = document.createElement('div');
            media.className = 'dashboard-widget__blank-media';
            media.dataset.blankWidgetMedia = '';
            const existingChildren = Array.from(blank.childNodes);
            existingChildren.forEach((child) => {
                if (child.nodeType === Node.ELEMENT_NODE
                    && child.matches?.('[data-blank-widget-controls], [data-blank-widget-admin]')) {
                    return;
                }

                media.appendChild(child);
            });
            blank.prepend(media);
        }

        if (!blank.querySelector('[data-blank-widget-controls]') && root.dataset.canEdit === 'true') {
            const controls = document.createElement('div');
            controls.className = 'dashboard-widget__blank-controls';
            controls.dataset.blankWidgetControls = '';
            controls.appendChild(createBlankWidgetControlButton(root.dataset.blankWidgetPreviousLabel || 'Previous image', 'previous'));
            controls.appendChild(createBlankWidgetControlButton(root.dataset.blankWidgetNextLabel || 'Next image', 'next'));

            const local = document.createElement('label');
            local.className = 'dashboard-widget__blank-button dashboard-widget__blank-local';
            local.title = root.dataset.blankWidgetAddLocalLabel || 'Add local image';
            local.setAttribute('aria-label', root.dataset.blankWidgetAddLocalLabel || 'Add local image');
            const input = document.createElement('input');
            input.type = 'file';
            input.accept = 'image/gif,image/png,image/jpeg,.gif,.png,.jpg,.jpeg';
            input.dataset.blankWidgetLocalFile = '';
            const icon = document.createElement('span');
            icon.className = 'dashboard-widget__blank-icon dashboard-widget__blank-icon--add';
            icon.setAttribute('aria-hidden', 'true');
            local.append(input, icon);
            controls.appendChild(local);

            if (root.dataset.isPortalAdmin === 'true') {
                controls.appendChild(createBlankWidgetControlButton(root.dataset.blankWidgetManageLibraryLabel || 'Manage image library', 'library'));
            }

            blank.appendChild(controls);
        }

        if (root.dataset.isPortalAdmin === 'true' && !blank.querySelector('[data-blank-widget-admin]')) {
            blank.appendChild(createBlankWidgetAdminControls(root));
        }
    }

    function createBlankWidgetControlButton(label, kind) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'dashboard-widget__blank-button';
        button.title = label;
        button.setAttribute('aria-label', label);
        if (kind === 'previous') {
            button.dataset.blankWidgetPrevious = '';
        } else if (kind === 'next') {
            button.dataset.blankWidgetNext = '';
        } else if (kind === 'library') {
            button.dataset.blankWidgetAdminOpen = '';
        }

        const icon = document.createElement('span');
        icon.className = `dashboard-widget__blank-icon dashboard-widget__blank-icon--${kind}`;
        icon.setAttribute('aria-hidden', 'true');
        button.appendChild(icon);
        return button;
    }

    async function moveBlankWidgetChoice(root, canvas, widget, token, state, direction) {
        const choices = await getBlankWidgetChoices(root, widget);
        if (choices.length === 0) {
            return;
        }

        const currentValue = getCurrentBlankWidgetChoiceValue(widget);
        const currentIndex = Math.max(0, choices.findIndex((choice) => choice.value === currentValue));
        const nextIndex = normalizeTrackIndex(currentIndex + direction, choices.length);
        await selectBlankWidgetChoice(root, canvas, widget, token, state, choices[nextIndex], true);
    }

    async function getBlankWidgetChoices(root, widget) {
        const images = await loadBlankWidgetImages(root);
        const choices = [
            {
                kind: 'static',
                value: 'static:0',
                label: root.dataset.blankWidgetDefaultLabel || 'Default',
                variant: 0,
                src: ''
            },
            {
                kind: 'static',
                value: 'static:1',
                label: root.dataset.blankWidgetOneLabel || 'Variant 1',
                variant: 1,
                src: '/img/blank-widget/1.gif'
            },
            {
                kind: 'static',
                value: 'static:2',
                label: root.dataset.blankWidgetTwoLabel || 'Variant 2',
                variant: 2,
                src: '/img/blank-widget/2.gif'
            }
        ];

        images.forEach((image) => {
            choices.push({
                kind: 'server',
                value: `image:${image.id}`,
                label: image.displayName || `Image ${image.id}`,
                imageId: image.id,
                src: image.src
            });
        });

        getBlankWidgetLocalImages(widget).forEach((image) => choices.push(image));
        return choices;
    }

    function getCurrentBlankWidgetChoiceValue(widget) {
        const selectedLocal = widget.__ompBlankWidgetSelectedLocalValue || '';
        return selectedLocal || getBlankWidgetStyleValue(widget);
    }

    async function selectBlankWidgetChoice(root, canvas, widget, token, state, choice, persistServerChoice) {
        if (!choice) {
            return;
        }

        if (choice.kind === 'local') {
            widget.__ompBlankWidgetSelectedLocalValue = choice.value;
            widget.__ompBlankWidgetSelectedLocalUrl = choice.src;
            widget.dataset.widgetStringData = '';
            widget.dataset.widgetIntData = '';
            renderBlankWidgetVariant(widget, 0, 0);
            return;
        }

        widget.__ompBlankWidgetSelectedLocalValue = '';
        widget.__ompBlankWidgetSelectedLocalUrl = '';
        applyBlankWidgetStyleValue(widget, choice.value);
        applyWidgetSettings(widget);
        if (persistServerChoice) {
            await persistDashboardWidgetSelection(root, canvas, token, state);
        }
    }

    async function persistDashboardWidgetSelection(root, canvas, token, state) {
        if (!root || !canvas || !state || root.dataset.canEdit !== 'true') {
            return;
        }

        try {
            await saveDashboardChanges(root, canvas, token, state);
        } catch (error) {
            handleDashboardSaveError(root, error);
        }
    }

    function addLocalBlankWidgetImages(root, widget, fileList) {
        const images = getBlankWidgetLocalImages(widget);
        const added = [];
        Array.from(fileList || []).filter(isBlankWidgetImageFile).forEach((file) => {
            const source = URL.createObjectURL(file);
            registerBlankWidgetObjectUrl(widget, source);
            const choice = {
                kind: 'local',
                value: `local:${Date.now().toString(36)}:${images.length}`,
                label: getFileStem(file.name) || root.dataset.blankWidgetLocalLabel || 'Local image',
                src: source
            };
            images.push(choice);
            added.push(choice);
        });

        return added;
    }

    function getBlankWidgetLocalImages(widget) {
        if (!widget.__ompBlankWidgetLocalImages) {
            Object.defineProperty(widget, '__ompBlankWidgetLocalImages', {
                value: [],
                configurable: true
            });
        }

        return widget.__ompBlankWidgetLocalImages;
    }

    function hasBlankWidgetImageTransfer(dataTransfer) {
        return Array.from(dataTransfer?.items || []).some((item) => {
            const file = typeof item.getAsFile === 'function' ? item.getAsFile() : null;
            return item.type?.startsWith('image/') || isBlankWidgetImageFile(file);
        }) || Array.from(dataTransfer?.files || []).some(isBlankWidgetImageFile);
    }

    async function refreshBlankWidgetImageOptions(root, selectedWidget = null, selectedValue = '') {
        const images = await loadBlankWidgetImages(root);
        root.querySelectorAll('[data-blank-widget-style-control]').forEach((select) => {
            const widget = select.closest('[data-dashboard-widget]');
            const desired = selectedWidget && widget === selectedWidget
                ? selectedValue
                : getBlankWidgetStyleValue(widget);
            updateBlankWidgetStyleOptions(root, select, images, desired);
        });
    }

    function updateBlankWidgetStyleOptions(root, select, images, selectedValue) {
        const selected = String(selectedValue || 'static:0');
        select.replaceChildren(
            createBlankWidgetOption('static:0', root.dataset.blankWidgetDefaultLabel || 'Default'),
            createBlankWidgetOption('static:1', root.dataset.blankWidgetOneLabel || 'Variant 1'),
            createBlankWidgetOption('static:2', root.dataset.blankWidgetTwoLabel || 'Variant 2'));

        const group = document.createElement('optgroup');
        group.label = root.dataset.blankWidgetCustomLabel || 'Custom image';
        group.dataset.blankWidgetCustomGroup = '';
        if (images.length === 0) {
            const empty = createBlankWidgetOption('', root.dataset.blankWidgetNoCustomImagesLabel || 'No custom images');
            empty.disabled = true;
            group.appendChild(empty);
        } else {
            images.forEach((image) => {
                group.appendChild(createBlankWidgetOption(`image:${image.id}`, image.displayName || `Image ${image.id}`));
            });
        }
        select.appendChild(group);

        if (selected.startsWith('image:')
            && !Array.from(select.options).some((option) => option.value === selected)) {
            const fallback = createBlankWidgetOption(selected, `${root.dataset.blankWidgetCustomLabel || 'Custom image'} ${selected.slice('image:'.length)}`);
            group.appendChild(fallback);
        }

        select.value = Array.from(select.options).some((option) => option.value === selected)
            ? selected
            : 'static:0';
    }

    function createBlankWidgetOption(value, label) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = label;
        return option;
    }

    function bindBlankWidgetAdmin(root, widget, onChange) {
        const panel = widget.querySelector('[data-blank-widget-admin]');
        if (!panel || panel.dataset.blankWidgetAdminBound === 'true') {
            return;
        }

        panel.dataset.blankWidgetAdminBound = 'true';
        const blank = widget.querySelector('[data-blank-widget]');
        const open = blank?.querySelector('[data-blank-widget-admin-open]');
        const close = panel.querySelector('[data-blank-widget-admin-close]');
        const file = panel.querySelector('[data-blank-widget-file]');
        const displayName = panel.querySelector('[data-blank-widget-display-name]');
        const upload = panel.querySelector('[data-blank-widget-upload]');
        const zip = panel.querySelector('[data-blank-widget-zip]');
        const zipUpload = panel.querySelector('[data-blank-widget-zip-upload]');
        const status = panel.querySelector('[data-blank-widget-status]');

        bindBlankWidgetAdminTabs(panel);
        open?.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            panel.hidden = !panel.hidden;
            open.setAttribute('aria-expanded', String(!panel.hidden));
        });
        close?.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            panel.hidden = true;
            open?.setAttribute('aria-expanded', 'false');
        });

        upload?.addEventListener('click', async () => {
            const selectedFile = file?.files?.[0] || null;
            if (!isBlankWidgetImageFile(selectedFile)) {
                setBlankWidgetStatus(status, root.dataset.blankWidgetSelectImageLabel || 'Select an image or GIF file.');
                return;
            }

            if (isBlankWidgetUploadTooLarge(root, selectedFile, 'blankWidgetMaxImageBytes')) {
                setBlankWidgetStatus(status, root.dataset.blankWidgetUploadTooLargeLabel || 'The selected image is too large.');
                return;
            }

            const formData = new FormData();
            formData.append('file', selectedFile);
            formData.append('displayName', displayName?.value || '');
            await submitBlankWidgetAdminForm(
                root,
                root.dataset.uploadBlankWidgetImageUrl,
                formData,
                [upload],
                status,
                async (result) => {
                    await applyBlankWidgetUploadResult(root, widget, result, onChange, false);
                    if (file) {
                        file.value = '';
                    }
                    if (displayName) {
                        displayName.value = '';
                    }
                });
        });

        zipUpload?.addEventListener('click', async () => {
            const selectedZip = zip.files?.[0] || null;
            if (!selectedZip) {
                setBlankWidgetStatus(status, root.dataset.blankWidgetSelectZipLabel || 'Select a ZIP package.');
                return;
            }

            if (isBlankWidgetUploadTooLarge(root, selectedZip, 'blankWidgetMaxZipBytes')) {
                setBlankWidgetStatus(status, root.dataset.blankWidgetUploadTooLargeLabel || 'The selected image is too large.');
                return;
            }

            const formData = new FormData();
            formData.append('zipFile', selectedZip);
            await submitBlankWidgetAdminForm(
                root,
                root.dataset.uploadBlankWidgetImagesZipUrl,
                formData,
                [zipUpload],
                status,
                async (result) => {
                    await applyBlankWidgetUploadResult(root, widget, result, onChange, false);
                    zip.value = '';
                });
        });
    }

    function bindBlankWidgetAdminTabs(panel) {
        const tabs = Array.from(panel.querySelectorAll('[data-blank-widget-admin-tab]'));
        const panes = Array.from(panel.querySelectorAll('[data-blank-widget-admin-pane]'));
        tabs.forEach((tab) => {
            tab.addEventListener('click', () => {
                const mode = tab.dataset.blankWidgetAdminTab || 'image';
                tabs.forEach((candidate) => {
                    const selected = candidate === tab;
                    candidate.classList.toggle('is-active', selected);
                    candidate.setAttribute('aria-selected', selected ? 'true' : 'false');
                });
                panes.forEach((pane) => {
                    pane.hidden = pane.dataset.blankWidgetAdminPane !== mode;
                });
            });
        });
    }

    function isBlankWidgetImageFile(file) {
        if (!file) {
            return false;
        }

        const name = String(file.name || '').toLowerCase();
        return file.type === 'image/gif'
            || file.type === 'image/png'
            || file.type === 'image/jpeg'
            || name.endsWith('.gif')
            || name.endsWith('.png')
            || name.endsWith('.jpg')
            || name.endsWith('.jpeg');
    }

    function isBlankWidgetUploadTooLarge(root, file, dataKey) {
        const maxBytes = parsePositiveInteger(root?.dataset?.[dataKey], 0);
        return maxBytes > 0 && !!file && file.size > maxBytes;
    }

    async function submitBlankWidgetAdminForm(root, url, formData, busyControls, status, onSuccess) {
        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';
        busyControls.forEach((control) => {
            if (control) {
                control.disabled = true;
            }
        });
        setBlankWidgetStatus(status, root.dataset.blankWidgetUploadingLabel || 'Uploading image...');

        try {
            const result = await postFormData(url, token, formData);
            setBlankWidgetStatus(status, root.dataset.blankWidgetUploadSuccessLabel || 'Blank widget image library updated.');
            await onSuccess(result);
        } catch (error) {
            const message = error?.status === 413
                ? root.dataset.blankWidgetUploadTooLargeLabel || 'The selected image is too large.'
                : error?.message || root.dataset.blankWidgetUploadErrorLabel || 'Could not update blank widget image library.';
            setBlankWidgetStatus(status, message);
        } finally {
            busyControls.forEach((control) => {
                if (control) {
                    control.disabled = false;
                }
            });
        }
    }

    async function applyBlankWidgetUploadResult(root, widget, result, onChange, selectUploadedImage = false) {
        if (Array.isArray(result?.images)) {
            blankWidgetImagesCache = result.images.map(normalizeBlankWidgetImage).filter(Boolean);
        } else {
            blankWidgetImagesCache = null;
        }

        const selectedImageId = normalizeBlankWidgetImageId(result?.selectedImageId ?? result?.SelectedImageId);
        const selectedValue = selectedImageId > 0 ? `image:${selectedImageId}` : getBlankWidgetStyleValue(widget);
        await refreshBlankWidgetImageOptions(root, widget, selectedValue);
        if (selectUploadedImage && selectedImageId > 0) {
            applyBlankWidgetStyleValue(widget, selectedValue);
            applyWidgetSettings(widget);
            onChange();
        }
    }

    function setBlankWidgetStatus(status, message) {
        if (status) {
            status.textContent = message || '';
        }
    }

    function setMusicAttribution(container, track) {
        container.replaceChildren();
        if (!track) {
            return;
        }

        const text = [track.attribution, track.description].filter(Boolean).join(' - ');
        if (text) {
            container.appendChild(document.createTextNode(text));
        }

        if (track.source) {
            if (container.childNodes.length > 0) {
                container.appendChild(document.createTextNode(' - '));
            }

            const link = document.createElement('a');
            link.href = track.source;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.textContent = getMusicSourceLabel(track.source);
            container.appendChild(link);
        }
    }

    function registerMusicObjectUrl(player, source) {
        const playerUrls = getMusicPlayerObjectUrls(player);
        playerUrls.add(source);
        musicObjectUrls.add(source);
    }

    function getMusicPlayerObjectUrls(player) {
        if (!player.__ompMusicObjectUrls) {
            Object.defineProperty(player, '__ompMusicObjectUrls', {
                value: new Set(),
                configurable: true
            });
        }

        return player.__ompMusicObjectUrls;
    }

    function revokeDashboardMusicPlayerObjectUrls(scope) {
        getDashboardMusicPlayers(scope).forEach((player) => {
            const playerUrls = player.__ompMusicObjectUrls;
            if (!playerUrls) {
                return;
            }

            playerUrls.forEach(revokeMusicObjectUrl);
            playerUrls.clear();
        });
    }

    function revokeMusicObjectUrl(source) {
        URL.revokeObjectURL(source);
        musicObjectUrls.delete(source);
    }

    function registerBlankWidgetObjectUrl(widget, source) {
        const widgetUrls = getBlankWidgetObjectUrls(widget);
        widgetUrls.add(source);
        blankImageObjectUrls.add(source);
    }

    function getBlankWidgetObjectUrls(widget) {
        if (!widget.__ompBlankWidgetObjectUrls) {
            Object.defineProperty(widget, '__ompBlankWidgetObjectUrls', {
                value: new Set(),
                configurable: true
            });
        }

        return widget.__ompBlankWidgetObjectUrls;
    }

    function revokeBlankWidgetObjectUrls(widget) {
        const widgetUrls = widget.__ompBlankWidgetObjectUrls;
        if (!widgetUrls) {
            return;
        }

        widgetUrls.forEach((source) => {
            URL.revokeObjectURL(source);
            blankImageObjectUrls.delete(source);
        });
        widgetUrls.clear();
    }

    function getMusicSourceLabel(source) {
        try {
            return new URL(source, document.baseURI).hostname || source;
        } catch {
            return source;
        }
    }

    function hasMusicTransfer(dataTransfer) {
        return Array.from(dataTransfer?.items || []).some((item) => {
            const file = typeof item.getAsFile === 'function' ? item.getAsFile() : null;
            return item.type === 'audio/mpeg' || isMusicFile(file);
        }) || Array.from(dataTransfer?.files || []).some(isMusicFile);
    }

    function isMusicFile(file) {
        return !!file && (file.type === 'audio/mpeg' || file.name.toLowerCase().endsWith('.mp3'));
    }

    function normalizeTrackIndex(index, count) {
        if (count <= 0) {
            return 0;
        }

        return ((index % count) + count) % count;
    }

    function formatMusicTime(seconds) {
        if (!Number.isFinite(seconds) || seconds <= 0) {
            return '0:00';
        }

        const rounded = Math.floor(seconds);
        const minutes = Math.floor(rounded / 60);
        const remainingSeconds = rounded % 60;
        return `${minutes}:${String(remainingSeconds).padStart(2, '0')}`;
    }

    function getFileStem(path) {
        const name = String(path || '').split(/[\\/]/).pop() || '';
        if (name.startsWith('.') && name.indexOf('.', 1) < 0) {
            return name;
        }

        return name.replace(/\.[^.]+$/, '');
    }

    function normalizeSearchText(value) {
        return String(value || '').trim().toLocaleLowerCase();
    }

    function startDrag(root, canvas, widget, event, state, onChange = () => {}) {
        const canvasRect = canvas.getBoundingClientRect();
        const widgetRect = widget.getBoundingClientRect();
        const startX = event.clientX;
        const startY = event.clientY;
        const startScrollX = window.scrollX || window.pageXOffset || 0;
        const startScrollY = window.scrollY || window.pageYOffset || 0;
        const startLeft = widgetRect.left - canvasRect.left + canvas.scrollLeft;
        const startTop = widgetRect.top - canvasRect.top + canvas.scrollTop;
        const selectedWidgets = getSelectedWidgets(canvas);
        const dragWidgets = selectedWidgets.length > 1 && widget.classList.contains('is-selected')
            ? selectedWidgets
            : [widget];
        const widgetPositions = dragWidgets.map((dragWidget) => ({
            widget: dragWidget,
            left: parsePixel(dragWidget.style.left),
            top: parsePixel(dragWidget.style.top)
        }));
        const activeStart = widgetPositions.find((item) => item.widget === widget) || { left: startLeft, top: startTop };
        const minStartLeft = widgetPositions.reduce((min, item) => Math.min(min, item.left), Number.POSITIVE_INFINITY);
        const minStartTop = widgetPositions.reduce((min, item) => Math.min(min, item.top), Number.POSITIVE_INFINITY);

        widget.setPointerCapture(event.pointerId);
        dragWidgets.forEach((dragWidget) => dragWidget.classList.add('is-moving'));

        const move = (moveEvent) => {
            autoScrollPageNearEdges(moveEvent);
            const scrollDeltaX = (window.scrollX || window.pageXOffset || 0) - startScrollX;
            const nextLeft = Math.max(0, startLeft + moveEvent.clientX - startX + scrollDeltaX);
            const scrollDeltaY = (window.scrollY || window.pageYOffset || 0) - startScrollY;
            const nextTop = Math.max(0, startTop + moveEvent.clientY - startY + scrollDeltaY);
            const deltaLeft = Math.max(snapIfNeeded(nextLeft, state) - activeStart.left, -minStartLeft);
            const deltaTop = Math.max(snapIfNeeded(nextTop, state) - activeStart.top, -minStartTop);

            widgetPositions.forEach((item) => {
                item.widget.style.left = `${item.left + deltaLeft}px`;
                item.widget.style.top = `${item.top + deltaTop}px`;
            });
            updateCanvasHeight(root, canvas, state);
        };

        const end = () => {
            dragWidgets.forEach((dragWidget) => dragWidget.classList.remove('is-moving'));
            widget.removeEventListener('pointermove', move);
            widget.removeEventListener('pointerup', end);
            widget.removeEventListener('pointercancel', end);
            updateCanvasHeight(root, canvas, state);
            onChange();
        };

        widget.addEventListener('pointermove', move);
        widget.addEventListener('pointerup', end);
        widget.addEventListener('pointercancel', end);
    }

    function autoScrollPageNearEdges(event) {
        const edgeSize = 90;
        const maxStep = 28;
        let scrollDelta = 0;
        let scrollDeltaX = 0;

        if (event.clientY > window.innerHeight - edgeSize) {
            scrollDelta = Math.ceil(((event.clientY - (window.innerHeight - edgeSize)) / edgeSize) * maxStep);
        } else if (event.clientY < edgeSize) {
            scrollDelta = -Math.ceil(((edgeSize - event.clientY) / edgeSize) * maxStep);
        }

        if (event.clientX > window.innerWidth - edgeSize) {
            scrollDeltaX = Math.ceil(((event.clientX - (window.innerWidth - edgeSize)) / edgeSize) * maxStep);
        } else if (event.clientX < edgeSize) {
            scrollDeltaX = -Math.ceil(((edgeSize - event.clientX) / edgeSize) * maxStep);
        }

        if (scrollDelta !== 0 || scrollDeltaX !== 0) {
            window.scrollBy({ top: scrollDelta, left: scrollDeltaX, behavior: 'auto' });
        }
    }

    function startResize(root, canvas, widget, event, state, onChange = () => {}) {
        const startX = event.clientX;
        const startY = event.clientY;
        const startScrollX = window.scrollX || window.pageXOffset || 0;
        const startWidth = widget.offsetWidth;
        const startHeight = widget.offsetHeight;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-resizing');

        const move = (moveEvent) => {
            autoScrollPageNearEdges(moveEvent);
            const scrollDeltaX = (window.scrollX || window.pageXOffset || 0) - startScrollX;
            const nextWidth = Math.max(minWidth, startWidth + moveEvent.clientX - startX + scrollDeltaX);
            const nextHeight = Math.max(minHeight, startHeight + moveEvent.clientY - startY);
            widget.style.width = `${snapSizeIfNeeded(nextWidth, minWidth, state)}px`;
            widget.style.height = `${snapSizeIfNeeded(nextHeight, minHeight, state)}px`;
            updateCanvasHeight(root, canvas, state);
        };

        const end = () => {
            widget.classList.remove('is-resizing');
            widget.removeEventListener('pointermove', move);
            widget.removeEventListener('pointerup', end);
            widget.removeEventListener('pointercancel', end);
            updateCanvasHeight(root, canvas, state);
            onChange();
        };

        widget.addEventListener('pointermove', move);
        widget.addEventListener('pointerup', end);
        widget.addEventListener('pointercancel', end);
    }

    function snapWidgetToGrid(widget, state) {
        if (!state?.alignToGrid) {
            return;
        }

        widget.style.left = `${snapIfNeeded(parsePixel(widget.style.left), state)}px`;
        widget.style.top = `${snapIfNeeded(parsePixel(widget.style.top), state)}px`;
        widget.style.width = `${snapSizeIfNeeded(widget.offsetWidth, minWidth, state)}px`;
        widget.style.height = `${snapSizeIfNeeded(widget.offsetHeight, minHeight, state)}px`;
    }

    function snapAllWidgetsToGrid(canvas, state) {
        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            snapWidgetToGrid(widget, state);
        });
    }

    function snapIfNeeded(value, state) {
        if (!state?.alignToGrid) {
            return Math.round(value);
        }

        const gridSize = state.gridSize || defaultGridSize;
        return Math.round(value / gridSize) * gridSize;
    }

    function snapSizeIfNeeded(value, min, state) {
        const snapped = snapIfNeeded(value, state);
        return Math.max(min, snapped);
    }

    function updateAlignToGridState(root, alignToggle, enabled) {
        root.dataset.alignToGrid = enabled ? 'true' : 'false';
        root.classList.toggle('is-aligning-to-grid', enabled);
        root.classList.toggle('is-free-placement', !enabled);
        if (alignToggle) {
            alignToggle.checked = enabled;
        }
    }

    function updateCanvasHeight(root, canvas, state) {
        const widgets = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'));
        const lowestWidgetBottom = widgets
            .reduce((max, widget) => Math.max(max, parsePixel(widget.style.top) + widget.offsetHeight), 0);
        const isEditing = root.classList.contains('is-editing');
        let nextHeight = isEditing
            ? Math.max(
                getMinCanvasHeight(root, canvas, state),
                lowestWidgetBottom + (state?.bottomPadding ?? parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding)))
            : widgets.length > 0
                ? lowestWidgetBottom + (state?.viewBottomPadding ?? parsePositiveInteger(root.dataset.canvasViewBottomPadding, defaultViewBottomPadding))
                : (state?.emptyCanvasHeight ?? parsePositiveInteger(root.dataset.emptyCanvasHeight, defaultEmptyCanvasHeight));
        if (isEditing && state) {
            state.editCanvasHeight = Math.max(
                state.editCanvasHeight || 0,
                nextHeight,
                Math.ceil(canvas.getBoundingClientRect().height));
            nextHeight = state.editCanvasHeight;
        } else if (state) {
            state.editCanvasHeight = 0;
        }
        canvas.style.setProperty('--dashboard-canvas-height', `${Math.ceil(nextHeight)}px`);
        updateCanvasWidth(root, canvas, state);
    }

    function updateCanvasWidth(root, canvas, state) {
        const widgets = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'));
        const lowestWidgetRight = widgets
            .reduce((max, widget) => Math.max(max, parsePixel(widget.style.left) + widget.offsetWidth), 0);
        const isEditing = root.classList.contains('is-editing');
        if (lowestWidgetRight <= 0) {
            canvas.style.setProperty('--dashboard-canvas-width', '100%');
            document.documentElement.style.setProperty('--portal-page-chrome-width', '100%');
            if (!isEditing && state) {
                state.editCanvasWidth = 0;
            }
            return;
        }

        const canvasLeft = Math.max(0, canvas.getBoundingClientRect().left);
        const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
        const padding = isEditing
            ? state?.bottomPadding ?? parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding)
            : state?.viewBottomPadding ?? parsePositiveInteger(root.dataset.canvasViewBottomPadding, defaultViewBottomPadding);
        let canvasWidth = Math.max(0, Math.ceil(lowestWidgetRight + padding));
        if (isEditing && state) {
            state.editCanvasWidth = Math.max(
                state.editCanvasWidth || 0,
                canvasWidth,
                Math.ceil(canvas.getBoundingClientRect().width));
            canvasWidth = state.editCanvasWidth;
        } else if (state) {
            state.editCanvasWidth = 0;
        }
        const pageWidth = Math.max(viewportWidth, Math.ceil(canvasLeft + canvasWidth));

        canvas.style.setProperty('--dashboard-canvas-width', `${canvasWidth}px`);
        document.documentElement.style.setProperty('--portal-page-chrome-width', `${pageWidth}px`);
    }

    function getMinCanvasHeight(root, canvas, state) {
        const configuredMin = state?.minCanvasHeight ?? parsePositiveInteger(root.dataset.minCanvasHeight, defaultMinCanvasHeight);
        const available = window.innerHeight - canvas.getBoundingClientRect().top - 32;
        return Math.max(configuredMin, available);
    }

    function parsePositiveInteger(value, fallback) {
        const parsed = parseInt(value || '', 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
    }

    function parseNullableInteger(value) {
        if (value === null || value === undefined || String(value).trim() === '') {
            return null;
        }

        const parsed = parseInt(value, 10);
        return Number.isFinite(parsed) ? parsed : null;
    }

    function normalizeColumnCount(value) {
        const parsed = parseNullableInteger(value);
        return parsed && parsed >= 1 && parsed <= 5 ? parsed : 1;
    }

    function normalizeAdminOverviewSize(value) {
        const parsed = parseNullableInteger(value);
        return parsed === 1 || parsed === 2 ? parsed : 0;
    }

    function normalizePresetZoom(value) {
        const normalized = String(value || '').trim().toLowerCase();
        return normalized === 'small' || normalized === 'large' ? normalized : 'default';
    }

    function getWidgetPresetZoom(widget) {
        const preset = normalizePresetZoom(widget?.dataset?.widgetStringData);
        if (preset !== 'default' || String(widget?.dataset?.widgetStringData || '').trim()) {
            return preset;
        }

        if (widget?.dataset?.widgetPayload === 'admin-overview') {
            const legacySize = normalizeAdminOverviewSize(widget.dataset.widgetIntData);
            if (legacySize === 1) {
                return 'small';
            }

            if (legacySize === 2) {
                return 'large';
            }
        }

        return 'default';
    }

    function normalizeContentScale(value) {
        const parsed = parseNullableInteger(value);
        if (parsed === null) {
            return 100;
        }

        return Math.max(25, Math.min(200, parsed));
    }

    function formatScaleFactor(contentScale) {
        const factor = normalizeContentScale(contentScale) / 100;
        return factor.toFixed(3);
    }

    function syncWidgetContentScaleControls(widget, contentScale) {
        widget.querySelectorAll('[data-widget-content-scale-control]').forEach((container) => {
            const value = String(contentScale);
            const range = container.querySelector('[data-widget-content-scale-range]');
            const input = container.querySelector('[data-widget-content-scale-input]');
            if (range) {
                range.value = value;
            }
            if (input && input.value !== value) {
                input.value = value;
            }
        });
    }

    function normalizeBlankWidgetVariant(value) {
        const parsed = parseNullableInteger(value);
        return parsed && parsed >= 1 && parsed <= 2 ? parsed : 0;
    }

    function normalizeBlankWidgetImageId(value) {
        const parsed = parseNullableInteger(value);
        return parsed && parsed > 0 ? parsed : 0;
    }

    function isBlankWidgetPayload(payload) {
        return !payload || payload === 'blank-rectangle';
    }

    function isColumnCountWidgetPayload(payload) {
        return payload === 'portal-entry-favorites'
            || payload === 'portal-entry-list'
            || payload === 'portal-entry-combolist'
            || payload === 'content-pages';
    }

    function isPresetZoomWidgetPayload(payload) {
        return payload === 'admin-overview'
            || payload === 'weekday-date'
            || payload === 'user-roles'
            || payload === 'content-pages';
    }

    function isMainModuleFilterWidgetPayload(payload) {
        return payload === 'portal-entry-list'
            || payload === 'portal-entry-combolist';
    }

    function normalizeMainModuleFilterValue(value) {
        return String(value || '').trim().toLowerCase() === 'main-only'
            ? 'main-only'
            : '';
    }

    function getBlankWidgetStyleValue(widget) {
        const imageId = normalizeBlankWidgetImageId(widget?.dataset?.widgetStringData);
        if (imageId > 0) {
            return `image:${imageId}`;
        }

        return `static:${normalizeBlankWidgetVariant(widget?.dataset?.widgetIntData)}`;
    }

    function applyBlankWidgetStyleValue(widget, value) {
        const raw = String(value || '').trim();
        widget.__ompBlankWidgetSelectedLocalValue = '';
        widget.__ompBlankWidgetSelectedLocalUrl = '';
        if (raw.startsWith('image:')) {
            const imageId = normalizeBlankWidgetImageId(raw.slice('image:'.length));
            widget.dataset.widgetStringData = imageId > 0 ? String(imageId) : '';
            widget.dataset.widgetIntData = '';
            return;
        }

        const rawVariant = raw.startsWith('static:') ? raw.slice('static:'.length) : raw;
        const variant = normalizeBlankWidgetVariant(rawVariant);
        widget.dataset.widgetStringData = '';
        widget.dataset.widgetIntData = variant > 0 ? String(variant) : '';
    }

    function syncBlankWidgetStyleControls(widget, variant, imageId) {
        const value = imageId > 0 ? `image:${imageId}` : `static:${variant}`;
        widget.querySelectorAll('[data-blank-widget-style-control]').forEach((control) => {
            if (control.value !== value) {
                control.value = value;
            }
        });
    }

    function getBlankWidgetImageUrl(widget, imageId) {
        const root = widget?.closest?.('[data-dashboard-root]');
        const template = root?.dataset?.blankWidgetImageUrlTemplate || '';
        return template
            ? template.replace('__id__', encodeURIComponent(String(imageId)))
            : '';
    }

    function renderBlankWidgetVariant(widget, variant, imageId = 0) {
        const blank = widget.querySelector('[data-blank-widget]');
        if (!blank) {
            return;
        }

        let media = blank.querySelector('[data-blank-widget-media]');
        if (!media) {
            media = document.createElement('div');
            media.className = 'dashboard-widget__blank-media';
            media.dataset.blankWidgetMedia = '';
            const existingImage = blank.querySelector(':scope > img');
            if (existingImage) {
                media.appendChild(existingImage);
            }
            blank.prepend(media);
        }

        const imageSource = widget.__ompBlankWidgetSelectedLocalUrl
            || (imageId > 0
            ? getBlankWidgetImageUrl(widget, imageId)
            : variant > 0
                ? `/img/blank-widget/${variant}.gif`
                : '');
        blank.classList.toggle('dashboard-widget__blank--image', !!imageSource);
        blank.classList.toggle('dashboard-widget__blank--local', !!widget.__ompBlankWidgetSelectedLocalUrl);
        blank.dataset.blankWidgetVariant = variant > 0 ? String(variant) : '';
        blank.dataset.blankWidgetImageId = imageId > 0 ? String(imageId) : '';

        if (!imageSource) {
            media.replaceChildren();
            return;
        }

        let image = media.querySelector('img');
        if (!image) {
            image = document.createElement('img');
            image.alt = '';
            media.replaceChildren(image);
        }

        if (image.getAttribute('src') !== imageSource && image.src !== imageSource) {
            image.src = imageSource;
        }
    }

    function normalizeWidgetDataValue(value) {
        if (value === null || value === undefined) {
            return '';
        }

        return String(value).trim();
    }

    function canUseDashboardDraft(root) {
        return root?.dataset.canEdit === 'true' && !!root.dataset.dashboardDraftKey && storageAvailable();
    }

    function storageAvailable() {
        try {
            return typeof window.localStorage !== 'undefined';
        } catch {
            return false;
        }
    }

    function getDashboardDraftStorageKey(root) {
        if (!canUseDashboardDraft(root)) {
            return '';
        }

        return `${dashboardDraftStoragePrefix}${root.dataset.dashboardDraftKey}`;
    }

    function updateDashboardDraftState(root, canvas, state, isDirty) {
        if (!canUseDashboardDraft(root) || !root.classList.contains('is-editing')) {
            return;
        }

        if (!isDirty) {
            clearDashboardDraft(root, state);
            return;
        }

        scheduleDashboardDraftWrite(root, canvas, state);
    }

    function scheduleDashboardDraftWrite(root, canvas, state) {
        if (!canUseDashboardDraft(root)) {
            return;
        }

        if (state.draftWriteTimer) {
            window.clearTimeout(state.draftWriteTimer);
        }

        state.draftWriteTimer = window.setTimeout(() => {
            state.draftWriteTimer = 0;
            writeDashboardDraft(root, canvas, state);
        }, dashboardDraftWriteDelayMs);
    }

    function writeDashboardDraft(root, canvas, state) {
        const key = getDashboardDraftStorageKey(root);
        if (!key) {
            return;
        }

        const draft = {
            schemaVersion: dashboardDraftSchemaVersion,
            timestamp: Date.now(),
            dashboardKey: root.dataset.dashboardDraftKey,
            serverSignature: state.savedSignature || '',
            layoutSignature: getDashboardSignature(canvas, state),
            alignToGrid: !!state.alignToGrid,
            expandedCanvas: !!state.expandedCanvas,
            pendingRemovedWidgetIds: Array.from(state.pendingRemovedWidgetIds || []),
            widgets: Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
                .map((widget) => ({
                    userActiveWidgetId: parseInt(widget.dataset.userActiveWidgetId || '0', 10),
                    widgetId: parseInt(widget.dataset.widgetId || '0', 10),
                    widgetType: widget.dataset.widgetType || '',
                    payload: widget.dataset.widgetPayload || '',
                    title: widget.querySelector('[data-widget-titlebar]')?.textContent?.trim() || '',
                    offsetTop: parsePixel(widget.style.top),
                    offsetLeft: parsePixel(widget.style.left),
                    width: Math.round(widget.offsetWidth),
                    height: Math.round(widget.offsetHeight),
                    orderPriority: parseInt(widget.style.zIndex || '0', 10) || 0,
                    intData: parseNullableInteger(widget.dataset.widgetIntData),
                    stringData: normalizeWidgetDataValue(widget.dataset.widgetStringData),
                    contentScale: normalizeContentScale(widget.dataset.widgetContentScale),
                    hideTitlebarWhenViewing: widget.dataset.widgetTitlebarHidden === 'true'
                }))
        };

        try {
            window.localStorage.setItem(key, JSON.stringify(draft));
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('Could not write dashboard draft.', error);
            }
        }
    }

    function clearDashboardDraft(root, state) {
        if (state?.draftWriteTimer) {
            window.clearTimeout(state.draftWriteTimer);
            state.draftWriteTimer = 0;
        }

        const key = getDashboardDraftStorageKey(root);
        if (!key) {
            return;
        }

        try {
            window.localStorage.removeItem(key);
        } catch {
            // localStorage can be disabled by browser policy; draft support is best effort.
        }
    }

    function readDashboardDraft(root) {
        const key = getDashboardDraftStorageKey(root);
        if (!key) {
            return null;
        }

        try {
            const raw = window.localStorage.getItem(key);
            if (!raw) {
                return null;
            }

            const draft = JSON.parse(raw);
            if (!draft
                || draft.schemaVersion !== dashboardDraftSchemaVersion
                || draft.dashboardKey !== root.dataset.dashboardDraftKey
                || !Array.isArray(draft.widgets)
                || Date.now() - Number(draft.timestamp || 0) > dashboardDraftTtlMs) {
                window.localStorage.removeItem(key);
                return null;
            }

            return draft;
        } catch {
            try {
                window.localStorage.removeItem(key);
            } catch {
                // Ignore cleanup failures.
            }

            return null;
        }
    }

    function initDashboardDraftPrompt(root, canvas, token, state, onChange, afterRestore) {
        const draft = readDashboardDraft(root);
        if (!draft || draft.layoutSignature === getDashboardSignature(canvas, state)) {
            if (draft) {
                clearDashboardDraft(root, state);
            }
            return;
        }

        showDashboardDraftPrompt(root, () => {
            hideDashboardDraftPrompt(root);
            afterRestore?.();
            restoreDashboardDraft(root, canvas, token, state, draft, onChange);
            onChange();
        }, () => {
            clearDashboardDraft(root, state);
            hideDashboardDraftPrompt(root);
        });
    }

    function ensureDashboardDraftBanner(root) {
        let banner = root.querySelector('[data-dashboard-draft-banner]');
        if (banner) {
            return banner;
        }

        banner = document.createElement('div');
        banner.className = 'dashboard-draft-banner';
        banner.setAttribute('data-dashboard-draft-banner', '');
        banner.setAttribute('role', 'status');
        banner.setAttribute('aria-live', 'polite');

        const message = document.createElement('span');
        message.className = 'dashboard-draft-banner__message';
        message.setAttribute('data-dashboard-draft-message', '');
        banner.appendChild(message);

        const actions = document.createElement('span');
        actions.className = 'dashboard-draft-banner__actions';
        actions.setAttribute('data-dashboard-draft-actions', '');
        banner.appendChild(actions);

        const canvas = root.querySelector('[data-dashboard-canvas]');
        root.insertBefore(banner, canvas || root.firstChild);
        return banner;
    }

    function showDashboardDraftPrompt(root, restore, discard) {
        const banner = ensureDashboardDraftBanner(root);
        banner.classList.remove('dashboard-draft-banner--error');
        banner.querySelector('[data-dashboard-draft-message]').textContent =
            root.dataset.draftAvailableLabel || 'Unsaved local dashboard changes were found. Restore them?';

        const actions = banner.querySelector('[data-dashboard-draft-actions]');
        actions.replaceChildren(
            createDraftButton(root.dataset.draftRestoreLabel || 'Restore draft', restore, true),
            createDraftButton(root.dataset.draftDiscardLabel || 'Discard draft', discard, false));
        banner.hidden = false;
    }

    function showDashboardStatus(root, message) {
        const banner = ensureDashboardDraftBanner(root);
        banner.classList.add('dashboard-draft-banner--error');
        banner.querySelector('[data-dashboard-draft-message]').textContent = message;
        banner.querySelector('[data-dashboard-draft-actions]').replaceChildren();
        banner.hidden = false;
    }

    function hideDashboardDraftPrompt(root) {
        const banner = root.querySelector('[data-dashboard-draft-banner]');
        if (banner) {
            banner.hidden = true;
        }
    }

    function createDraftButton(text, handler, primary) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = primary ? 'btn btn-primary' : 'btn';
        button.textContent = text;
        button.addEventListener('click', handler);
        return button;
    }

    function restoreDashboardDraft(root, canvas, token, state, draft, onChange) {
        canvas.querySelectorAll('[data-dashboard-widget]').forEach(removeDashboardWidgetElement);
        state.pendingRemovedWidgetIds.clear();
        if (Array.isArray(draft.pendingRemovedWidgetIds)) {
            draft.pendingRemovedWidgetIds.forEach((id) => {
                const parsed = parseInt(id || '0', 10);
                if (parsed > 0) {
                    state.pendingRemovedWidgetIds.add(parsed);
                }
            });
        }
        state.addedWidgetIds.clear();
        state.alignToGrid = draft.alignToGrid !== false;
        state.expandedCanvas = draft.expandedCanvas !== false;
        updateAlignToGridState(root, root.querySelector('[data-dashboard-align-toggle]'), state.alignToGrid);
        updateExpandedCanvasState(root, null, state.expandedCanvas);

        let nextTemporaryId = state.nextTemporaryWidgetId;
        draft.widgets.forEach((item) => {
            let userActiveWidgetId = Number.isFinite(Number(item.userActiveWidgetId))
                ? Number(item.userActiveWidgetId)
                : 0;
            if (userActiveWidgetId === 0) {
                userActiveWidgetId = nextTemporaryId--;
            }

            const widget = createWidgetElement(root, {
                userActiveWidgetId,
                widgetId: item.widgetId,
                title: item.title,
                widgetType: item.widgetType,
                payload: item.payload,
                offsetTop: item.offsetTop,
                offsetLeft: item.offsetLeft,
                width: item.width,
                height: item.height,
                orderPriority: item.orderPriority,
                intData: item.intData,
                stringData: item.stringData || null,
                contentScale: item.contentScale,
                hideTitlebarWhenViewing: item.hideTitlebarWhenViewing === true
            });

            canvas.appendChild(widget);
            if (userActiveWidgetId <= 0) {
                state.addedWidgetIds.add(userActiveWidgetId);
                nextTemporaryId = Math.min(nextTemporaryId, userActiveWidgetId - 1);
            }

            bindWidget(root, canvas, widget, token, () => getMaxOrder(canvas) + 1, state, onChange);
            bindEntryFavoriteToggles(root, widget, token);
            bindEntryListFilters(widget);
            bindDashboardMusicPlayers(widget);
            bindDashboardNotifications(widget);
        });

        state.nextTemporaryWidgetId = nextTemporaryId;
        updateWidgetSelectionState(root, canvas);
        updateEmptyState(canvas);
        updateCanvasHeight(root, canvas, state);
    }

    function handleDashboardSaveError(root, error) {
        const kind = error?.ompKind === 'network' ? 'network' : error?.ompKind === 'auth' ? 'auth' : 'server';
        if (kind === 'auth' || kind === 'network') {
            window.dispatchEvent(new CustomEvent(sessionStatusWarningEvent, { detail: { kind } }));
        }

        const message = kind === 'auth'
            ? root.dataset.draftSessionLostLabel || 'Your session appears to have expired. Sign in again before saving dashboard changes.'
            : kind === 'network'
                ? root.dataset.draftNetworkLostLabel || 'The server could not be reached. Your local dashboard draft was kept.'
                : root.dataset.draftSaveFailedLabel || 'Dashboard changes could not be saved. Your local draft was kept.';
        showDashboardStatus(root, message);
    }

    async function saveDashboardChanges(root, canvas, token, state) {
        if (getDashboardSignature(canvas, state) !== (state?.savedSignature || '')) {
            writeDashboardDraft(root, canvas, state);
        }

        await saveLayout(root, canvas, token, state);
        state.pendingRemovedWidgetIds.clear();
        state.addedWidgetIds.clear();
        state.savedSnapshot = captureDashboardSnapshot(canvas, state);
        state.savedSignature = getDashboardSignature(canvas, state);
        clearDashboardDraft(root, state);
        hideDashboardDraftPrompt(root);
        updateDashboardDirtyState(root, canvas, state);
    }

    function updateDashboardDirtyState(root, canvas, state, saveButton = root.querySelector('[data-dashboard-save]')) {
        const isDirty = getDashboardSignature(canvas, state) !== (state?.savedSignature || '');
        root.classList.toggle('has-dashboard-changes', isDirty);
        if (saveButton) {
            saveButton.disabled = !isDirty;
            saveButton.setAttribute('aria-disabled', String(!isDirty));
        }

        return isDirty;
    }

    function getDashboardSignature(canvas, state) {
        const widgets = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
            .map((widget) => ({
                userActiveWidgetId: parseInt(widget.dataset.userActiveWidgetId || '0', 10),
                widgetId: parseInt(widget.dataset.widgetId || '0', 10),
                offsetTop: parsePixel(widget.style.top),
                offsetLeft: parsePixel(widget.style.left),
                width: Math.round(widget.offsetWidth),
                height: Math.round(widget.offsetHeight),
                orderPriority: parseInt(widget.style.zIndex || '0', 10) || 0,
                title: '',
                intData: parseNullableInteger(widget.dataset.widgetIntData),
                stringData: normalizeWidgetDataValue(widget.dataset.widgetStringData),
                contentScale: normalizeContentScale(widget.dataset.widgetContentScale),
                hideTitlebarWhenViewing: widget.dataset.widgetTitlebarHidden === 'true'
            }))
            .sort((left, right) => {
                const idCompare = left.userActiveWidgetId - right.userActiveWidgetId;
                return idCompare !== 0 ? idCompare : left.widgetId - right.widgetId;
            });

        return JSON.stringify({
            alignToGrid: !!state?.alignToGrid,
            expandedCanvas: !!state?.expandedCanvas,
            widgets
        });
    }

    function requestDashboardDoneChoice(root) {
        const dialog = root.querySelector('[data-dashboard-unsaved-dialog]');
        if (!dialog || typeof dialog.showModal !== 'function') {
            return Promise.resolve(window.confirm(root.dataset.doneFallbackConfirm || 'Save changes?') ? 'save' : 'cancel');
        }

        const saveButton = dialog.querySelector('[data-dashboard-unsaved-save]');
        const discardButton = dialog.querySelector('[data-dashboard-unsaved-discard]');
        const cancelButton = dialog.querySelector('[data-dashboard-unsaved-cancel]');

        return new Promise((resolve) => {
            const finish = (choice) => {
                saveButton?.removeEventListener('click', save);
                discardButton?.removeEventListener('click', discard);
                cancelButton?.removeEventListener('click', cancel);
                dialog.removeEventListener('cancel', cancel);
                if (dialog.open) {
                    dialog.close();
                }

                resolve(choice);
            };
            const save = () => finish('save');
            const discard = () => finish('discard');
            const cancel = (event) => {
                event?.preventDefault();
                finish('cancel');
            };

            saveButton?.addEventListener('click', save);
            discardButton?.addEventListener('click', discard);
            cancelButton?.addEventListener('click', cancel);
            dialog.addEventListener('cancel', cancel);
            dialog.showModal();
            window.requestAnimationFrame(() => {
                saveButton?.focus();
            });
        });
    }

    async function saveLayout(root, canvas, token, state) {
        if (state?.alignToGrid) {
            snapAllWidgetsToGrid(canvas, state);
            updateCanvasHeight(root, canvas, state);
        }

        const widgets = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
            .map((widget) => ({
                userActiveWidgetId: parseInt(widget.dataset.userActiveWidgetId || '0', 10),
                widgetId: parseInt(widget.dataset.widgetId || '0', 10),
                offsetTop: parsePixel(widget.style.top),
                offsetLeft: parsePixel(widget.style.left),
                width: Math.round(widget.offsetWidth),
                height: Math.round(widget.offsetHeight),
                orderPriority: parseInt(widget.style.zIndex || '0', 10) || 0,
                title: null,
                intData: parseNullableInteger(widget.dataset.widgetIntData),
                stringData: normalizeWidgetDataValue(widget.dataset.widgetStringData) || null,
                contentScale: normalizeContentScale(widget.dataset.widgetContentScale),
                hideTitlebarWhenViewing: widget.dataset.widgetTitlebarHidden === 'true'
            }))
            .filter((widget) => widget.widgetId > 0);

        const result = await postForm(root.dataset.saveUrl, token, {
            widgetsJson: JSON.stringify(widgets)
        });
        applySavedWidgetIds(canvas, result);

        const removedIds = Array.from(state?.pendingRemovedWidgetIds || []);
        for (const userActiveWidgetId of removedIds) {
            if (userActiveWidgetId <= 0) {
                continue;
            }

            await postForm(root.dataset.removeUrl, token, { userActiveWidgetId });
        }
    }

    async function resetDashboardChanges(root, canvas, token, state, snapshot, nextOrder, onChange = () => {}) {
        const addedIds = Array.from(state.addedWidgetIds);
        for (const userActiveWidgetId of addedIds) {
            if (userActiveWidgetId <= 0) {
                continue;
            }

            await postForm(root.dataset.removeUrl, token, { userActiveWidgetId });
        }

        if (state.alignToGrid !== snapshot.alignToGrid || state.expandedCanvas !== snapshot.expandedCanvas) {
            state.alignToGrid = snapshot.alignToGrid;
            state.expandedCanvas = snapshot.expandedCanvas;
            updateAlignToGridState(root, root.querySelector('[data-dashboard-align-toggle]'), state.alignToGrid);
            updateExpandedCanvasState(root, null, state.expandedCanvas);
            await saveDashboardPreferences(root, token, state);
        }

        restoreDashboardSnapshot(root, canvas, token, state, snapshot, nextOrder, onChange);
        state.pendingRemovedWidgetIds.clear();
        state.addedWidgetIds.clear();
        state.savedSnapshot = captureDashboardSnapshot(canvas, state);
        state.savedSignature = getDashboardSignature(canvas, state);
        updateWidgetSelectionState(root, canvas);
        updateEmptyState(canvas);
        updateCanvasHeight(root, canvas, state);
    }

    function applySavedWidgetIds(canvas, result) {
        const addedWidgets = Array.isArray(result?.addedWidgets)
            ? result.addedWidgets
            : [];

        addedWidgets.forEach((item) => {
            const temporaryId = String(item.temporaryUserActiveWidgetId || '');
            const userActiveWidgetId = String(item.userActiveWidgetId || '');
            if (!temporaryId || !userActiveWidgetId) {
                return;
            }

            const widget = canvas.querySelector(`[data-dashboard-widget][data-user-active-widget-id="${cssEscape(temporaryId)}"]`);
            if (widget) {
                widget.dataset.userActiveWidgetId = userActiveWidgetId;
            }
        });
    }

    function captureDashboardSnapshot(canvas, state) {
        return {
            alignToGrid: !!state?.alignToGrid,
            expandedCanvas: !!state?.expandedCanvas,
            maxOrder: getMaxOrder(canvas),
            widgets: Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
                .map((widget) => {
                    const clone = widget.cloneNode(true);
                    clearDashboardBindingMarkers(clone);
                    return clone;
                })
        };
    }

    function restoreDashboardSnapshot(root, canvas, token, state, snapshot, nextOrder, onChange = () => {}) {
        canvas.querySelectorAll('[data-dashboard-widget]').forEach(removeDashboardWidgetElement);

        snapshot.widgets.forEach((savedWidget) => {
            const widget = savedWidget.cloneNode(true);
            clearDashboardBindingMarkers(widget);
            canvas.appendChild(widget);
            bindWidget(root, canvas, widget, token, nextOrder, state, onChange);
            bindEntryFavoriteToggles(root, widget, token);
            bindEntryListFilters(widget);
            bindDashboardMusicPlayers(widget);
        });
    }

    function clearDashboardBindingMarkers(element) {
        element.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
            delete button.dataset.dashboardFavoriteBound;
        });
        element.querySelectorAll('[data-widget-settings-toggle]').forEach((button) => {
            delete button.dataset.dashboardWidgetSettingsBound;
        });
        element.querySelectorAll('[data-widget-zoom-toggle]').forEach((button) => {
            delete button.dataset.dashboardWidgetSettingsBound;
        });
        element.querySelectorAll('[data-widget-titlebar-toggle]').forEach((button) => {
            delete button.dataset.dashboardWidgetTitlebarBound;
        });
        element.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
            delete control.dataset.dashboardWidgetSettingsBound;
        });
        element.querySelectorAll('[data-widget-content-scale-control]').forEach((control) => {
            delete control.dataset.dashboardWidgetSettingsBound;
        });
        getDashboardMusicPlayers(element).forEach((player) => {
            delete player.dataset.dashboardMusicBound;
        });
    }

    function getDashboardMusicPlayers(scope) {
        if (!scope) {
            return [];
        }

        const players = scope.matches?.('[data-dashboard-music-player]') ? [scope] : [];
        scope.querySelectorAll?.('[data-dashboard-music-player]').forEach((player) => {
            if (!players.includes(player)) {
                players.push(player);
            }
        });

        return players;
    }

    async function saveDashboardPreferences(root, token, state) {
        await postForm(root.dataset.preferenceUrl, token, {
            alignToGrid: !!state.alignToGrid,
            expandedCanvas: !!state.expandedCanvas
        });
    }

    function updateExpandedCanvasState(root, expandedToggle, enabled) {
        root.dataset.expandedCanvas = enabled ? 'true' : 'false';
        root.classList.toggle('is-expanded-canvas', enabled);
        root.closest('.content-shell')?.classList.toggle('content-shell--expanded-dashboard', enabled);
        if (expandedToggle) {
            expandedToggle.checked = enabled;
        }
    }

    async function postForm(url, token, values) {
        if (!url) {
            return null;
        }

        const body = new FormData();
        if (token) {
            body.append('__RequestVerificationToken', token);
        }

        Object.entries(values).forEach(([key, value]) => {
            body.append(key, value == null ? '' : String(value));
        });

        let response;
        try {
            response = await fetch(url, {
                method: 'POST',
                body,
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });
        } catch (error) {
            throw createDashboardRequestError('network', 'Dashboard request could not reach the server.', 0, error);
        }

        if (response.status === 401 || response.status === 403 || isDashboardLoginRedirect(response)) {
            throw createDashboardRequestError('auth', `Dashboard request failed with status ${response.status}.`, response.status);
        }

        if (!response.ok) {
            throw createDashboardRequestError('server', `Dashboard request failed with status ${response.status}.`, response.status);
        }

        return response.headers.get('content-type')?.includes('application/json')
            ? response.json()
            : null;
    }

    async function postFormData(url, token, body) {
        if (!url) {
            return null;
        }

        if (token) {
            body.append('__RequestVerificationToken', token);
        }

        let response;
        try {
            response = await fetch(url, {
                method: 'POST',
                body,
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });
        } catch (error) {
            throw createDashboardRequestError('network', 'Dashboard request could not reach the server.', 0, error);
        }

        if (response.status === 401 || response.status === 403 || isDashboardLoginRedirect(response)) {
            throw createDashboardRequestError('auth', `Dashboard request failed with status ${response.status}.`, response.status);
        }

        if (!response.ok) {
            const payload = response.headers.get('content-type')?.includes('application/json')
                ? await response.json().catch(() => null)
                : null;
            throw createDashboardRequestError('server', payload?.message || `Dashboard request failed with status ${response.status}.`, response.status);
        }

        return response.headers.get('content-type')?.includes('application/json')
            ? response.json()
            : null;
    }

    function isDashboardLoginRedirect(response) {
        const url = (response?.url || '').toLowerCase();
        return (response?.redirected && url.includes('/login'))
            || url.includes('/auth/login');
    }

    function createDashboardRequestError(kind, message, status, cause) {
        const error = new Error(message);
        error.ompKind = kind;
        error.status = status;
        if (cause) {
            error.cause = cause;
        }

        return error;
    }

    function createWidgetElement(root, widget) {
        const element = document.createElement('article');
        element.className = 'dashboard-widget';
        element.dataset.dashboardWidget = '';
        element.dataset.userActiveWidgetId = String(widget.userActiveWidgetId);
        element.dataset.widgetId = String(widget.widgetId);
        element.dataset.widgetType = widget.widgetType || '';
        element.dataset.widgetPayload = widget.payload || '';
        element.dataset.widgetIntData = normalizeWidgetDataValue(widget.intData);
        element.dataset.widgetStringData = normalizeWidgetDataValue(widget.stringData);
        element.dataset.widgetContentScale = String(normalizeContentScale(widget.contentScale));
        element.dataset.widgetColumns = isColumnCountWidgetPayload(element.dataset.widgetPayload)
            ? String(normalizeColumnCount(widget.intData))
            : '';
        element.dataset.widgetPresetZoom = '';
        element.dataset.widgetTitlebarHidden = widget.hideTitlebarWhenViewing === true ? 'true' : 'false';
        element.style.top = `${widget.offsetTop || 0}px`;
        element.style.left = `${widget.offsetLeft || 0}px`;
        element.style.width = `${widget.width || 320}px`;
        element.style.height = `${widget.height || 192}px`;
        element.style.zIndex = `${widget.orderPriority || 10}`;

        if (widget.title) {
            const titlebar = document.createElement('div');
            titlebar.className = 'dashboard-widget__titlebar';
            titlebar.dataset.widgetTitlebar = '';
            titlebar.textContent = widget.title;
            element.appendChild(titlebar);
        }

        const body = document.createElement('div');
        body.className = 'dashboard-widget__body';
        const content = document.createElement('div');
        content.className = 'dashboard-widget__content';
        content.dataset.widgetContent = '';
        const scaleTarget = document.createElement('div');
        scaleTarget.className = 'dashboard-widget__content-scale-target';
        scaleTarget.dataset.widgetContentScaleTarget = '';
        scaleTarget.appendChild(createWidgetBodyContent(root, widget.payload));
        content.appendChild(scaleTarget);
        body.appendChild(content);
        element.appendChild(body);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'dashboard-widget__remove';
        remove.dataset.widgetRemove = '';
        remove.title = root.dataset.removeLabel || 'Remove widget';
        remove.setAttribute('aria-label', root.dataset.removeLabel || 'Remove widget');
        const removeIcon = document.createElement('span');
        removeIcon.className = 'dashboard-widget__remove-icon';
        removeIcon.setAttribute('aria-hidden', 'true');
        remove.appendChild(removeIcon);
        element.appendChild(remove);

        if (widget.title) {
            const titlebarToggle = document.createElement('button');
            titlebarToggle.type = 'button';
            titlebarToggle.className = 'dashboard-widget__titlebar-toggle';
            titlebarToggle.dataset.widgetTitlebarToggle = '';
            titlebarToggle.title = root.dataset.titlebarToggleLabel || 'Hide title bar outside edit mode';
            titlebarToggle.setAttribute('aria-label', root.dataset.titlebarToggleLabel || 'Hide title bar outside edit mode');
            titlebarToggle.setAttribute('aria-pressed', element.dataset.widgetTitlebarHidden === 'true' ? 'true' : 'false');
            const titlebarIcon = document.createElement('span');
            titlebarIcon.className = 'dashboard-widget__titlebar-toggle-icon';
            titlebarIcon.setAttribute('aria-hidden', 'true');
            titlebarToggle.appendChild(titlebarIcon);
            element.appendChild(titlebarToggle);
        }

        const settings = createWidgetSettingsControls(root, widget);
        if (settings) {
            if (settings.zoomToggle && settings.zoomPanel) {
                element.appendChild(settings.zoomToggle);
                element.appendChild(settings.zoomPanel);
            }

            if (settings.toggle && settings.panel) {
                element.appendChild(settings.toggle);
                element.appendChild(settings.panel);
            }
        }

        const resize = document.createElement('span');
        resize.className = 'dashboard-widget__resize';
        resize.dataset.widgetResize = '';
        resize.setAttribute('aria-hidden', 'true');
        element.appendChild(resize);

        applyWidgetSettings(element);
        return element;
    }

    function createWidgetSettingsControls(root, widget) {
        const zoomLabel = root.dataset.contentScaleLabel || 'Zoom';
        const zoomToggle = createWidgetPanelToggle(
            'dashboard-widget__zoom-toggle',
            'data-widget-zoom-toggle',
            'dashboard-widget__zoom-icon',
            zoomLabel);

        const zoomPanel = document.createElement('div');
        zoomPanel.className = 'dashboard-widget__zoom-panel';
        zoomPanel.dataset.widgetZoomPanel = '';
        zoomPanel.hidden = true;
        zoomPanel.appendChild(isPresetZoomWidgetPayload(widget.payload)
            ? createPresetZoomField(root, widget)
            : createContentScaleField(root, widget.contentScale));

        const settingFields = [];

        if (isColumnCountWidgetPayload(widget.payload)) {
            settingFields.push(createSelectField(
                root.dataset.columnCountLabel || 'Column count',
                [
                    ['1', root.dataset.oneColumnLabel || '1 column'],
                    ['2', root.dataset.twoColumnsLabel || '2 columns'],
                    ['3', root.dataset.threeColumnsLabel || '3 columns'],
                    ['4', root.dataset.fourColumnsLabel || '4 columns'],
                    ['5', root.dataset.fiveColumnsLabel || '5 columns']
                ],
                String(normalizeColumnCount(widget.intData)),
                (select) => {
                    select.dataset.widgetIntDataControl = '';
                }));
            if (isMainModuleFilterWidgetPayload(widget.payload)) {
                settingFields.push(createSelectField(
                    root.dataset.entryFilterModeLabel || 'Entry filter',
                    [
                        ['', root.dataset.entryFilterAllLabel || 'Show all entries'],
                        ['main-only', root.dataset.entryFilterMainOnlyLabel || 'Top-level entries only']
                    ],
                    normalizeMainModuleFilterValue(widget.stringData),
                    (select) => {
                        select.dataset.widgetEntryFilterControl = '';
                    }));
            }
        } else if (widget.payload === 'weekday-date') {
            settingFields.push(createSelectField(
                root.dataset.weekNumberLabel || 'Week number',
                [
                    ['0', root.dataset.showWeekNumberLabel || 'Show week number'],
                    ['1', root.dataset.hideWeekNumberLabel || 'Hide week number']
                ],
                parseNullableInteger(widget.intData) === 1 ? '1' : '0',
                (select) => {
                    select.dataset.widgetIntDataControl = '';
                }));
        } else if (isBlankWidgetPayload(widget.payload)) {
            settingFields.push(createBlankWidgetStyleField(root, widget));
        }

        if (settingFields.length === 0) {
            return { zoomToggle, zoomPanel };
        }

        const label = root.dataset.widgetSettingsLabel || 'Widget settings';
        const toggle = createWidgetPanelToggle(
            'dashboard-widget__settings-toggle',
            'data-widget-settings-toggle',
            'dashboard-widget__settings-icon',
            label);

        const panel = document.createElement('div');
        panel.className = 'dashboard-widget__settings-panel';
        panel.dataset.widgetSettingsPanel = '';
        panel.hidden = true;
        settingFields.forEach((field) => panel.appendChild(field));

        return { zoomToggle, zoomPanel, toggle, panel };
    }

    function createWidgetPanelToggle(className, dataAttributeName, iconClassName, label) {
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = className;
        toggle.setAttribute(dataAttributeName, '');
        toggle.title = label;
        toggle.setAttribute('aria-label', label);
        toggle.setAttribute('aria-expanded', 'false');

        const icon = document.createElement('span');
        icon.className = iconClassName;
        icon.setAttribute('aria-hidden', 'true');
        toggle.appendChild(icon);
        return toggle;
    }

    function createPresetZoomField(root, widget) {
        const element = {
            dataset: {
                widgetPayload: widget?.payload || '',
                widgetStringData: normalizeWidgetDataValue(widget?.stringData),
                widgetIntData: normalizeWidgetDataValue(widget?.intData)
            }
        };
        return createSelectField(
            root.dataset.contentScaleLabel || 'Zoom',
            [
                ['small', root.dataset.smallLabel || 'Small'],
                ['default', root.dataset.defaultLabel || 'Default'],
                ['large', root.dataset.largeLabel || 'Large']
            ],
            getWidgetPresetZoom(element),
            (select) => {
                select.dataset.widgetPresetZoomControl = '';
            });
    }

    function createContentScaleField(root, value) {
        const normalizedValue = normalizeContentScale(value);
        const field = document.createElement('div');
        field.className = 'dashboard-widget__settings-field';

        const text = document.createElement('span');
        text.textContent = root.dataset.contentScaleLabel || 'Zoom';
        field.appendChild(text);

        const control = document.createElement('div');
        control.className = 'dashboard-widget__zoom-control';
        control.dataset.widgetContentScaleControl = '';

        const range = document.createElement('input');
        range.type = 'range';
        range.min = '25';
        range.max = '200';
        range.value = String(normalizedValue);
        range.dataset.widgetContentScaleRange = '';
        range.setAttribute('aria-label', root.dataset.contentScaleLabel || 'Zoom');
        control.appendChild(range);

        const valueWrap = document.createElement('div');
        valueWrap.className = 'dashboard-widget__zoom-value';

        const input = document.createElement('input');
        input.type = 'number';
        input.min = '25';
        input.max = '200';
        input.value = String(normalizedValue);
        input.dataset.widgetContentScaleInput = '';
        input.setAttribute('aria-label', root.dataset.contentScaleValueLabel || 'Zoom value');
        valueWrap.appendChild(input);

        const suffix = document.createElement('span');
        suffix.textContent = '%';
        valueWrap.appendChild(suffix);
        control.appendChild(valueWrap);

        const reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'btn-link';
        reset.dataset.widgetContentScaleReset = '';
        reset.textContent = root.dataset.contentScaleResetLabel || 'Reset zoom';
        control.appendChild(reset);

        field.appendChild(control);
        return field;
    }

    function createSelectField(labelText, options, selectedValue, configureSelect) {
        const fieldLabel = document.createElement('label');
        const text = document.createElement('span');
        text.textContent = labelText;
        const select = document.createElement('select');
        if (configureSelect) {
            configureSelect(select);
        }

        options.forEach(([value, optionText]) => {
            const option = document.createElement('option');
            option.value = value;
            option.textContent = optionText;
            select.appendChild(option);
        });
        select.value = selectedValue;

        fieldLabel.appendChild(text);
        fieldLabel.appendChild(select);
        return fieldLabel;
    }

    function createBlankWidgetStyleField(root, widget) {
        const imageId = normalizeBlankWidgetImageId(widget?.stringData);
        const selected = imageId > 0
            ? `image:${imageId}`
            : `static:${normalizeBlankWidgetVariant(widget?.intData)}`;
        return createSelectField(
            root.dataset.blankWidgetStyleLabel || 'Blank widget style',
            [
                ['static:0', root.dataset.blankWidgetDefaultLabel || 'Default'],
                ['static:1', root.dataset.blankWidgetOneLabel || 'Variant 1'],
                ['static:2', root.dataset.blankWidgetTwoLabel || 'Variant 2']
            ],
            selected,
            (select) => {
                select.dataset.blankWidgetStyleControl = '';
            });
    }

    function createBlankWidgetAdminControls(root) {
        const panel = document.createElement('div');
        panel.className = 'dashboard-widget__blank-admin';
        panel.dataset.blankWidgetAdmin = '';
        panel.hidden = true;

        const header = document.createElement('div');
        header.className = 'dashboard-widget__blank-admin-header';
        const title = document.createElement('strong');
        title.textContent = root.dataset.blankWidgetImageLibraryLabel || 'Image library';
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'dashboard-widget__blank-admin-close';
        close.dataset.blankWidgetAdminClose = '';
        close.setAttribute('aria-label', root.dataset.closeLabel || 'Close');
        header.append(title, close);
        panel.appendChild(header);

        const tabs = document.createElement('div');
        tabs.className = 'dashboard-widget__blank-admin-tabs';
        tabs.setAttribute('role', 'tablist');
        tabs.setAttribute('aria-label', root.dataset.blankWidgetImageLibraryLabel || 'Image library');
        const imageTab = document.createElement('button');
        imageTab.type = 'button';
        imageTab.className = 'is-active';
        imageTab.dataset.blankWidgetAdminTab = 'image';
        imageTab.setAttribute('role', 'tab');
        imageTab.setAttribute('aria-selected', 'true');
        imageTab.textContent = root.dataset.blankWidgetSingleImageLabel || 'Single image';
        const zipTab = document.createElement('button');
        zipTab.type = 'button';
        zipTab.dataset.blankWidgetAdminTab = 'zip';
        zipTab.setAttribute('role', 'tab');
        zipTab.setAttribute('aria-selected', 'false');
        zipTab.textContent = root.dataset.blankWidgetZipPackageLabel || 'ZIP package';
        tabs.append(imageTab, zipTab);
        panel.appendChild(tabs);

        const imagePane = document.createElement('div');
        imagePane.className = 'dashboard-widget__blank-admin-pane';
        imagePane.dataset.blankWidgetAdminPane = 'image';
        const fileLabel = document.createElement('label');
        const fileText = document.createElement('span');
        fileText.textContent = root.dataset.blankWidgetImageFileLabel || 'Image or GIF file';
        const file = document.createElement('input');
        file.type = 'file';
        file.accept = 'image/gif,image/png,image/jpeg,.gif,.png,.jpg,.jpeg';
        file.dataset.blankWidgetFile = '';
        fileLabel.append(fileText, file);
        imagePane.appendChild(fileLabel);

        const displayNameLabel = document.createElement('label');
        const displayNameText = document.createElement('span');
        displayNameText.textContent = root.dataset.blankWidgetDisplayNameLabel || 'Display name';
        const displayName = document.createElement('input');
        displayName.type = 'text';
        displayName.maxLength = 200;
        displayName.dataset.blankWidgetDisplayName = '';
        displayNameLabel.append(displayNameText, displayName);
        imagePane.appendChild(displayNameLabel);

        const actions = document.createElement('div');
        actions.className = 'dashboard-widget__blank-admin-actions';
        const upload = document.createElement('button');
        upload.type = 'button';
        upload.className = 'btn btn-primary';
        upload.dataset.blankWidgetUpload = '';
        upload.textContent = root.dataset.blankWidgetUploadLabel || 'Upload image';
        actions.appendChild(upload);
        imagePane.appendChild(actions);
        panel.appendChild(imagePane);

        const zipPane = document.createElement('div');
        zipPane.className = 'dashboard-widget__blank-admin-pane';
        zipPane.dataset.blankWidgetAdminPane = 'zip';
        zipPane.hidden = true;
        const zipLabel = document.createElement('label');
        const zipText = document.createElement('span');
        zipText.textContent = root.dataset.blankWidgetZipPackageLabel || 'ZIP package';
        const zip = document.createElement('input');
        zip.type = 'file';
        zip.accept = '.zip,application/zip';
        zip.dataset.blankWidgetZip = '';
        zipLabel.append(zipText, zip);
        zipPane.appendChild(zipLabel);
        const zipActions = document.createElement('div');
        zipActions.className = 'dashboard-widget__blank-admin-actions';
        const zipUpload = document.createElement('button');
        zipUpload.type = 'button';
        zipUpload.className = 'btn btn-primary';
        zipUpload.dataset.blankWidgetZipUpload = '';
        zipUpload.textContent = root.dataset.blankWidgetImportZipLabel || 'Import ZIP';
        zipActions.appendChild(zipUpload);
        zipPane.appendChild(zipActions);
        panel.appendChild(zipPane);

        const status = document.createElement('div');
        status.className = 'dashboard-widget__blank-admin-status';
        status.dataset.blankWidgetStatus = '';
        panel.appendChild(status);
        return panel;
    }

    function createWidgetBodyContent(root, payload) {
        const template = payload
            ? root.querySelector(`[data-dashboard-widget-template="${cssEscape(payload)}"]`)
            : null;
        if (template?.content?.firstElementChild) {
            const clone = template.content.firstElementChild.cloneNode(true);
            clone.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
                delete button.dataset.dashboardFavoriteBound;
            });
            return clone;
        }

        const blank = document.createElement('div');
        blank.className = 'dashboard-widget__blank';
        blank.dataset.blankWidget = '';
        return blank;
    }

    function removeDashboardWidgetElement(widget) {
        closeDashboardWidgetPopupForWidget(widget);
        revokeDashboardMusicPlayerObjectUrls(widget);
        revokeBlankWidgetObjectUrls(widget);
        widget.remove();
    }

    function bringToFront(widget, order) {
        widget.style.zIndex = String(order);
    }

    function getMaxOrder(canvas) {
        return Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
            .reduce((max, widget) => Math.max(max, parseInt(widget.style.zIndex || '0', 10) || 0), 0);
    }

    function getNextWidgetOffset(canvas, state) {
        const widgetCount = canvas.querySelectorAll('[data-dashboard-widget]').length;
        const offset = Math.min(32 + (widgetCount % 8) * 32, 256);
        return snapIfNeeded(offset, state);
    }

    function parsePixel(value) {
        return Math.max(0, Math.round(parseFloat(value || '0') || 0));
    }

    function updateEmptyState(canvas) {
        const empty = canvas.querySelector('[data-dashboard-empty]');
        if (!empty) {
            return;
        }

        empty.classList.toggle('is-hidden', canvas.querySelectorAll('[data-dashboard-widget]').length > 0);
    }

    function closePicker(picker) {
        if (!picker) {
            return;
        }

        if (typeof picker.close === 'function') {
            picker.close();
        } else {
            picker.removeAttribute('open');
        }

        document.body.classList.remove('dashboard-widget-picker-open');
    }

    function openWidgetPicker(picker) {
        if (!picker) {
            return;
        }

        if (typeof picker.showModal === 'function') {
            picker.showModal();
        } else {
            picker.setAttribute('open', '');
        }

        document.body.classList.add('dashboard-widget-picker-open');
        resetWidgetPickerFilter(picker);
        clearWidgetPickerSelection(picker);
        syncWidgetPickerCompactMode(picker);
        window.requestAnimationFrame(() => {
            syncWidgetPickerCompactMode(picker);
            picker.querySelector('[data-widget-picker-filter]')?.focus();
        });
    }

    function bindWidgetPickerCompactToggle(picker) {
        const toggle = picker?.querySelector('[data-widget-picker-compact-toggle]');
        if (!toggle || toggle.dataset.dashboardWidgetPickerCompactBound === 'true') {
            return;
        }

        toggle.dataset.dashboardWidgetPickerCompactBound = 'true';
        toggle.addEventListener('click', () => {
            if (picker.classList.contains('is-auto-compact')) {
                return;
            }

            picker.dataset.manualCompact = picker.dataset.manualCompact === 'true' ? 'false' : 'true';
            syncWidgetPickerCompactMode(picker);
        });
        syncWidgetPickerCompactMode(picker);
    }

    function syncWidgetPickerCompactMode(picker) {
        if (!picker) {
            return;
        }

        const autoCompact = window.matchMedia('(max-width: 860px)').matches;
        const manualCompact = picker.dataset.manualCompact === 'true';
        const isCompact = autoCompact || manualCompact;
        const toggle = picker.querySelector('[data-widget-picker-compact-toggle]');

        picker.classList.toggle('is-auto-compact', autoCompact);
        picker.classList.toggle('is-compact', isCompact);

        if (toggle) {
            toggle.disabled = autoCompact;
            toggle.setAttribute('aria-pressed', manualCompact ? 'true' : 'false');
            toggle.textContent = isCompact
                ? (toggle.dataset.detailedLabel || 'Detailed view')
                : (toggle.dataset.compactLabel || 'Compact view');
        }
    }

    function clearWidgetPickerSelection(picker) {
        picker?.querySelectorAll('[data-widget-option]').forEach((option) => {
            option.classList.remove('is-selected');
            option.setAttribute('aria-selected', 'false');
        });

        const addButton = picker?.querySelector('[data-widget-picker-add]');
        if (addButton) {
            addButton.disabled = true;
        }

        const title = picker?.querySelector('[data-widget-picker-detail-title]');
        if (title) {
            title.textContent = title.closest('.dashboard-widget-picker__details')?.querySelector('.dashboard-widget-picker__eyebrow')?.textContent
                || 'Select a widget';
        }

        const meta = picker?.querySelector('[data-widget-picker-detail-meta]');
        if (meta) {
            meta.textContent = '';
            meta.hidden = true;
        }

        const description = picker?.querySelector('[data-widget-picker-detail-description]');
        const preview = picker?.querySelector('[data-widget-picker-preview]');
        const emptyLabel = preview?.dataset.emptyLabel || 'Select a widget for details and preview.';
        if (description) {
            description.textContent = emptyLabel;
        }

        if (preview) {
            preview.replaceChildren(createWidgetPickerEmptyPreview(emptyLabel));
        }
    }

    function getSelectedWidgetPickerOption(picker) {
        const selected = picker?.querySelector('[data-widget-option].is-selected:not([hidden])');
        return selected instanceof HTMLElement ? selected : null;
    }

    function selectWidgetPickerOption(root, picker, option) {
        if (!picker || !option || option.hidden) {
            return;
        }

        picker.querySelectorAll('[data-widget-option]').forEach((item) => {
            const isSelected = item === option;
            item.classList.toggle('is-selected', isSelected);
            item.setAttribute('aria-selected', isSelected ? 'true' : 'false');
        });

        const addButton = picker.querySelector('[data-widget-picker-add]');
        if (addButton) {
            addButton.disabled = false;
        }

        const title = picker.querySelector('[data-widget-picker-detail-title]');
        if (title) {
            title.textContent = option.dataset.widgetTitle || '';
        }

        const metaParts = [
            option.dataset.widgetModuleKey,
            option.dataset.widgetAuthor
        ].filter((value) => String(value || '').trim().length > 0);
        const meta = picker.querySelector('[data-widget-picker-detail-meta]');
        if (meta) {
            meta.textContent = metaParts.join(' / ');
            meta.hidden = metaParts.length === 0;
        }

        const description = picker.querySelector('[data-widget-picker-detail-description]');
        if (description) {
            description.textContent = option.dataset.widgetDescription
                || picker.dataset.noDescriptionLabel
                || 'No description available.';
        }

        renderWidgetPickerPreview(root, picker, option);
    }

    function renderWidgetPickerPreview(root, picker, option) {
        const preview = picker?.querySelector('[data-widget-picker-preview]');
        if (!preview) {
            return;
        }

        preview.replaceChildren();
        try {
            const content = createWidgetBodyContent(root, option?.dataset?.widgetPayload || '');
            const scaleTarget = document.createElement('div');
            scaleTarget.className = 'dashboard-widget-picker__preview-scale-target dashboard-widget__content-scale-target';
            scaleTarget.dataset.widgetContentScaleTarget = '';
            scaleTarget.appendChild(content);
            preview.appendChild(scaleTarget);
        } catch {
            preview.replaceChildren(createWidgetPickerEmptyPreview(preview.dataset.unavailableLabel || 'Preview unavailable'));
        }
    }

    function createWidgetPickerEmptyPreview(text) {
        const empty = document.createElement('div');
        empty.className = 'dashboard-widget-picker__preview-empty';
        empty.textContent = text;
        return empty;
    }

    function bindWidgetPickerFilter(picker) {
        const input = picker?.querySelector('[data-widget-picker-filter]');
        const moduleSelect = picker?.querySelector('[data-widget-picker-module-filter]');
        const authorSelect = picker?.querySelector('[data-widget-picker-author-filter]');
        if (!picker || picker.dataset.dashboardWidgetPickerFilterBound === 'true' || (!input && !moduleSelect && !authorSelect)) {
            return;
        }

        picker.dataset.dashboardWidgetPickerFilterBound = 'true';
        input?.addEventListener('input', () => applyWidgetPickerFilter(picker));
        input?.addEventListener('search', () => applyWidgetPickerFilter(picker));
        input?.addEventListener('keyup', () => applyWidgetPickerFilter(picker));
        moduleSelect?.addEventListener('change', () => applyWidgetPickerFilter(picker));
        authorSelect?.addEventListener('change', () => applyWidgetPickerFilter(picker));
        applyWidgetPickerFilter(picker);
    }

    function resetWidgetPickerFilter(picker) {
        const input = picker?.querySelector('[data-widget-picker-filter]');
        const moduleSelect = picker?.querySelector('[data-widget-picker-module-filter]');
        const authorSelect = picker?.querySelector('[data-widget-picker-author-filter]');
        if (input) {
            input.value = '';
        }

        if (moduleSelect) {
            moduleSelect.value = '';
        }

        if (authorSelect) {
            authorSelect.value = '';
        }

        applyWidgetPickerFilter(picker);
    }

    function applyWidgetPickerFilter(picker) {
        const input = picker?.querySelector('[data-widget-picker-filter]');
        const moduleSelect = picker?.querySelector('[data-widget-picker-module-filter]');
        const authorSelect = picker?.querySelector('[data-widget-picker-author-filter]');
        const query = normalizeSearchText(input?.value || '');
        const moduleFilter = normalizeSearchText(moduleSelect?.value || '');
        const authorFilter = normalizeSearchText(authorSelect?.value || '');
        const options = Array.from(picker?.querySelectorAll('[data-widget-option]') || []);
        let visibleCount = 0;

        options.forEach((option) => {
            const titleMatches = query.length === 0 || getWidgetPickerOptionSearchText(option).includes(query);
            const moduleMatches = moduleFilter.length === 0 || normalizeSearchText(option?.dataset?.widgetModuleKey || '') === moduleFilter;
            const authorMatches = authorFilter.length === 0 || normalizeSearchText(option?.dataset?.widgetAuthor || '') === authorFilter;
            const isVisible = titleMatches && moduleMatches && authorMatches;
            option.hidden = !isVisible;
            option.classList.toggle('is-filter-hidden', !isVisible);
            option.setAttribute('aria-hidden', isVisible ? 'false' : 'true');
            option.tabIndex = isVisible ? 0 : -1;
            if (isVisible) {
                visibleCount += 1;
            }
        });

        const empty = picker?.querySelector('[data-widget-picker-empty]');
        if (empty) {
            const hasActiveFilter = query.length > 0 || moduleFilter.length > 0 || authorFilter.length > 0;
            empty.hidden = !hasActiveFilter || visibleCount > 0 || options.length === 0;
        }

        const selected = getSelectedWidgetPickerOption(picker);
        if (selected && selected.hidden) {
            clearWidgetPickerSelection(picker);
        }
    }

    function getWidgetPickerOptionSearchText(option) {
        return normalizeSearchText([
            option?.dataset?.widgetSearchText,
            option?.dataset?.widgetTitle
        ].filter(Boolean).join(' '));
    }

    function cssEscape(value) {
        if (window.CSS?.escape) {
            return window.CSS.escape(value);
        }

        const text = String(value);
        const length = text.length;
        const firstCodeUnit = text.charCodeAt(0);
        let result = '';

        for (let index = 0; index < length; index += 1) {
            const character = text.charAt(index);
            const codeUnit = text.charCodeAt(index);

            if (codeUnit === 0) {
                result += '\uFFFD';
                continue;
            }

            const isControlCharacter = (codeUnit >= 1 && codeUnit <= 31) || codeUnit === 127;
            const isInitialDigit = index === 0 && codeUnit >= 48 && codeUnit <= 57;
            const isSecondDigitAfterHyphen = index === 1
                && codeUnit >= 48
                && codeUnit <= 57
                && firstCodeUnit === 45;

            if (isControlCharacter || isInitialDigit || isSecondDigitAfterHyphen) {
                result += `\\${codeUnit.toString(16)} `;
                continue;
            }

            if (index === 0 && codeUnit === 45 && length === 1) {
                result += '\\-';
                continue;
            }

            if (codeUnit >= 128
                || codeUnit === 45
                || codeUnit === 95
                || (codeUnit >= 48 && codeUnit <= 57)
                || (codeUnit >= 65 && codeUnit <= 90)
                || (codeUnit >= 97 && codeUnit <= 122)) {
                result += character;
                continue;
            }

            result += `\\${character}`;
        }

        return result;
    }

    function initAll() {
        document.querySelectorAll('[data-dashboard-root]').forEach(initDashboard);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAll, { once: true });
    } else {
        initAll();
    }

    window.addEventListener(favoriteChangedEvent, handleExternalFavoriteChanged);
    window.addEventListener(notificationChangedEvent, handleExternalNotificationChanged);
    window.addEventListener('beforeunload', () => {
        musicObjectUrls.forEach((url) => URL.revokeObjectURL(url));
        musicObjectUrls.clear();
        blankImageObjectUrls.forEach((url) => URL.revokeObjectURL(url));
        blankImageObjectUrls.clear();
    });
})();
