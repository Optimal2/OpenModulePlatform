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
    const sessionStatusWarningEvent = 'omp:session-status-warning';
    const musicObjectUrls = new Set();

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

            if (typeof picker.showModal === 'function') {
                picker.showModal();
            } else {
                picker.setAttribute('open', '');
            }
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

        picker?.addEventListener('click', (event) => {
            if (event.target === picker) {
                closePicker(picker);
            }
        });

        picker?.querySelectorAll('[data-widget-option]').forEach((option) => {
            option.addEventListener('click', async () => {
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
                updateEmptyState(canvas);
                updateCanvasHeight(root, canvas, state);
                updateDirtyState();
                closePicker(picker);
                if (!isEditing) {
                    setEditing(true);
                }
            });
        });

        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            bindWidget(root, canvas, widget, token, () => ++maxOrder, state, updateDirtyState);
        });
        bindEntryFavoriteToggles(root, root, token);
        bindEntryListFilters(root);
        bindDashboardMusicPlayers(root);
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
        applyWidgetSettings(widget);

        widget.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            if (event.target.closest('button, a, input, textarea, select, label, summary, [data-widget-resize], [data-widget-settings-panel]')) {
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
        const toggle = widget.querySelector('[data-widget-settings-toggle]');
        const panel = widget.querySelector('[data-widget-settings-panel]');
        if (toggle && panel && toggle.dataset.dashboardWidgetSettingsBound !== 'true') {
            toggle.dataset.dashboardWidgetSettingsBound = 'true';
            toggle.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopPropagation();
                const isOpen = panel.hidden;
                panel.hidden = !isOpen;
                toggle.setAttribute('aria-expanded', String(isOpen));
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
                setScale(0, true);
            });
        });
    }

    function applyWidgetSettings(widget) {
        const contentScale = normalizeContentScale(widget.dataset.widgetContentScale);
        widget.dataset.widgetContentScale = String(contentScale);
        widget.style.setProperty('--dashboard-widget-content-scale-factor', formatScaleFactor(contentScale));
        syncWidgetContentScaleControls(widget, contentScale);

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

        if (widget.dataset.widgetPayload === 'portal-entry-list') {
            const columns = normalizeColumnCount(widget.dataset.widgetIntData);
            widget.dataset.widgetColumns = columns > 0 ? String(columns) : '';
            widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
                control.value = String(columns);
            });
            return;
        }

        if (isBlankWidgetPayload(widget.dataset.widgetPayload)) {
            const variant = normalizeBlankWidgetVariant(widget.dataset.widgetIntData);
            widget.dataset.widgetIntData = variant > 0 ? String(variant) : '';
            widget.querySelectorAll('[data-widget-int-data-control]').forEach((control) => {
                control.value = String(variant);
            });
            renderBlankWidgetVariant(widget, variant);
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

        list.querySelectorAll('[data-dashboard-entry-row]').forEach((row) => {
            row.hidden = isFiltering && !getEntryRowSearchText(row).includes(query);
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
        state.tracks = await loadMusicPlaylist(player.dataset.playlistUrl || '');
        setTrack(0, false);
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
        const nextHeight = isEditing
            ? Math.max(
                getMinCanvasHeight(root, canvas, state),
                lowestWidgetBottom + (state?.bottomPadding ?? parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding)))
            : widgets.length > 0
                ? lowestWidgetBottom + (state?.viewBottomPadding ?? parsePositiveInteger(root.dataset.canvasViewBottomPadding, defaultViewBottomPadding))
                : (state?.emptyCanvasHeight ?? parsePositiveInteger(root.dataset.emptyCanvasHeight, defaultEmptyCanvasHeight));
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
        return parsed && parsed >= 1 && parsed <= 3 ? parsed : 0;
    }

    function normalizeContentScale(value) {
        const parsed = parseNullableInteger(value);
        if (parsed === null) {
            return 0;
        }

        return Math.max(-100, Math.min(100, parsed));
    }

    function formatScaleFactor(contentScale) {
        const factor = Math.max(0.1, (100 + normalizeContentScale(contentScale)) / 100);
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

    function isBlankWidgetPayload(payload) {
        return !payload || payload === 'blank-rectangle';
    }

    function renderBlankWidgetVariant(widget, variant) {
        const blank = widget.querySelector('[data-blank-widget]');
        if (!blank) {
            return;
        }

        blank.classList.toggle('dashboard-widget__blank--image', variant > 0);
        blank.dataset.blankWidgetVariant = variant > 0 ? String(variant) : '';

        if (variant <= 0) {
            blank.replaceChildren();
            return;
        }

        let image = blank.querySelector('img');
        if (!image) {
            image = document.createElement('img');
            image.alt = '';
            blank.replaceChildren(image);
        }

        const nextSrc = `/img/blank-widget/${variant}.gif`;
        if (!image.src.endsWith(nextSrc)) {
            image.src = nextSrc;
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
                    contentScale: normalizeContentScale(widget.dataset.widgetContentScale)
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
                stringData: null,
                contentScale: item.contentScale
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
                contentScale: normalizeContentScale(widget.dataset.widgetContentScale)
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
                contentScale: normalizeContentScale(widget.dataset.widgetContentScale)
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
        content.appendChild(createWidgetBodyContent(root, widget.payload));
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

        const settings = createWidgetSettingsControls(root, widget);
        if (settings) {
            element.appendChild(settings.toggle);
            element.appendChild(settings.panel);
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
        const label = root.dataset.widgetSettingsLabel || 'Widget settings';
        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'dashboard-widget__settings-toggle';
        toggle.dataset.widgetSettingsToggle = '';
        toggle.title = label;
        toggle.setAttribute('aria-label', label);
        toggle.setAttribute('aria-expanded', 'false');

        const icon = document.createElement('span');
        icon.className = 'dashboard-widget__settings-icon';
        icon.setAttribute('aria-hidden', 'true');
        toggle.appendChild(icon);

        const panel = document.createElement('div');
        panel.className = 'dashboard-widget__settings-panel';
        panel.dataset.widgetSettingsPanel = '';
        panel.hidden = true;

        panel.appendChild(createContentScaleField(root, widget.contentScale));

        if (widget.payload === 'portal-entry-list') {
            panel.appendChild(createSelectField(
                root.dataset.columnCountLabel || 'Column count',
                [
                    ['0', root.dataset.defaultColumnsLabel || 'Default'],
                    ['1', root.dataset.oneColumnLabel || '1 column'],
                    ['2', root.dataset.twoColumnsLabel || '2 columns'],
                    ['3', root.dataset.threeColumnsLabel || '3 columns']
                ],
                String(normalizeColumnCount(widget.intData)),
                (select) => {
                    select.dataset.widgetIntDataControl = '';
                }));
        } else if (widget.payload === 'weekday-date') {
            panel.appendChild(createSelectField(
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
            panel.appendChild(createSelectField(
                root.dataset.blankWidgetStyleLabel || 'Blank widget style',
                [
                    ['0', root.dataset.blankWidgetDefaultLabel || 'Default'],
                    ['1', root.dataset.blankWidgetOneLabel || 'Variant 1'],
                    ['2', root.dataset.blankWidgetTwoLabel || 'Variant 2']
                ],
                String(normalizeBlankWidgetVariant(widget.intData)),
                (select) => {
                    select.dataset.widgetIntDataControl = '';
                }));
        }

        return { toggle, panel };
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
        range.min = '-100';
        range.max = '100';
        range.value = String(normalizedValue);
        range.dataset.widgetContentScaleRange = '';
        range.setAttribute('aria-label', root.dataset.contentScaleLabel || 'Zoom');
        control.appendChild(range);

        const valueWrap = document.createElement('div');
        valueWrap.className = 'dashboard-widget__zoom-value';

        const input = document.createElement('input');
        input.type = 'number';
        input.min = '-100';
        input.max = '100';
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
        revokeDashboardMusicPlayerObjectUrls(widget);
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
    }

    function cssEscape(value) {
        if (window.CSS?.escape) {
            return window.CSS.escape(value);
        }

        return String(value).replace(/["\\]/g, '\\$&');
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
    window.addEventListener('beforeunload', () => {
        musicObjectUrls.forEach((url) => URL.revokeObjectURL(url));
        musicObjectUrls.clear();
    });
})();
