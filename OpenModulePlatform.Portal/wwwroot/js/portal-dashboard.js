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
    const favoriteChangedEvent = 'omp:navigation-favorite-changed';

    function initDashboard(root) {
        const canvas = root.querySelector('[data-dashboard-canvas]');
        const editToggle = root.querySelector('[data-dashboard-edit-toggle]');
        const editLabel = root.querySelector('[data-dashboard-edit-label]');
        const saveButton = root.querySelector('[data-dashboard-save]');
        const addButton = root.querySelector('[data-dashboard-add-open]');
        const resetChangesButton = root.querySelector('[data-dashboard-reset-changes]');
        const resetButton = root.querySelector('[data-dashboard-reset]');
        const alignToggle = root.querySelector('[data-dashboard-align-toggle]');
        const picker = root.querySelector('[data-widget-picker]');
        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';

        bindRoleSwitchConfirmations(root);

        if (!canvas || !editToggle || root.dataset.canEdit !== 'true') {
            if (canvas) {
                updateCanvasHeight(root, canvas);
                window.addEventListener('resize', () => updateCanvasHeight(root, canvas));
            }
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
            savedSignature: ''
        };
        const updateDirtyState = () => updateDashboardDirtyState(root, canvas, state, saveButton);

        updateAlignToGridState(root, alignToggle, state.alignToGrid);
        updateExpandedCanvasState(root, null, state.expandedCanvas);
        updateCanvasHeight(root, canvas, state);
        window.addEventListener('resize', () => updateCanvasHeight(root, canvas, state));

        editToggle.addEventListener('click', async () => {
            if (isEditing) {
                if (!updateDirtyState()) {
                    setEditing(false);
                    return;
                }

                const choice = await requestDashboardDoneChoice(root);
                if (choice === 'save') {
                    await saveDashboardChanges(root, canvas, token, state);
                    maxOrder = getMaxOrder(canvas);
                    setEditing(false);
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

            await saveDashboardChanges(root, canvas, token, state);
            maxOrder = getMaxOrder(canvas);
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

        resetButton?.addEventListener('click', async () => {
            const message = root.dataset.resetConfirm || 'This clears your current dashboard widgets. Continue?';
            if (!window.confirm(message)) {
                return;
            }

            await postForm(root.dataset.resetUrl, token, {});
            canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => widget.remove());
            maxOrder = 0;
            updateEmptyState(canvas);
            updateCanvasHeight(root, canvas, state);
            state.addedWidgetIds.clear();
            state.pendingRemovedWidgetIds.clear();
            state.savedSnapshot = captureDashboardSnapshot(canvas, state);
            state.savedSignature = getDashboardSignature(canvas, state);
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
        state.savedSnapshot = captureDashboardSnapshot(canvas, state);
        state.savedSignature = getDashboardSignature(canvas, state);
        updateDirtyState();

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
            editToggle.setAttribute('aria-pressed', isEditing ? 'true' : 'false');
            if (editLabel) {
                editLabel.textContent = isEditing
                    ? root.dataset.doneLabel || 'Done'
                    : '';
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

            if (event.target.closest('button, a, input, textarea, select, [data-widget-resize], [data-widget-settings-panel]')) {
                return;
            }

            event.preventDefault();
            bringToFront(widget, nextOrder());
            startDrag(root, canvas, widget, event, state, onChange);
        });

        resizeHandle?.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
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
            widget.remove();
            updateEmptyState(canvas);
            updateCanvasHeight(root, canvas, state);
            onChange();
        });
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
    }

    function applyWidgetSettings(widget) {
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

    function normalizeSearchText(value) {
        return String(value || '').trim().toLocaleLowerCase();
    }

    function startDrag(root, canvas, widget, event, state, onChange = () => {}) {
        const canvasRect = canvas.getBoundingClientRect();
        const widgetRect = widget.getBoundingClientRect();
        const startX = event.clientX;
        const startY = event.clientY;
        const startScrollY = window.scrollY || window.pageYOffset || 0;
        const startLeft = widgetRect.left - canvasRect.left + canvas.scrollLeft;
        const startTop = widgetRect.top - canvasRect.top + canvas.scrollTop;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-moving');

        const move = (moveEvent) => {
            autoScrollPageVertically(moveEvent);
            const nextLeft = Math.max(0, startLeft + moveEvent.clientX - startX);
            const scrollDeltaY = (window.scrollY || window.pageYOffset || 0) - startScrollY;
            const nextTop = Math.max(0, startTop + moveEvent.clientY - startY + scrollDeltaY);
            widget.style.left = `${snapIfNeeded(nextLeft, state)}px`;
            widget.style.top = `${snapIfNeeded(nextTop, state)}px`;
            updateCanvasHeight(root, canvas, state);
        };

        const end = () => {
            widget.classList.remove('is-moving');
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

    function autoScrollPageVertically(event) {
        const edgeSize = 90;
        const maxStep = 28;
        let scrollDelta = 0;

        if (event.clientY > window.innerHeight - edgeSize) {
            scrollDelta = Math.ceil(((event.clientY - (window.innerHeight - edgeSize)) / edgeSize) * maxStep);
        } else if (event.clientY < edgeSize) {
            scrollDelta = -Math.ceil(((edgeSize - event.clientY) / edgeSize) * maxStep);
        }

        if (scrollDelta !== 0) {
            window.scrollBy({ top: scrollDelta, left: 0, behavior: 'auto' });
        }
    }

    function startResize(root, canvas, widget, event, state, onChange = () => {}) {
        const startX = event.clientX;
        const startY = event.clientY;
        const startWidth = widget.offsetWidth;
        const startHeight = widget.offsetHeight;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-resizing');

        const move = (moveEvent) => {
            const nextWidth = Math.max(minWidth, startWidth + moveEvent.clientX - startX);
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
        if (lowestWidgetRight <= 0) {
            canvas.style.setProperty('--dashboard-canvas-width', '100%');
            document.documentElement.style.setProperty('--portal-page-chrome-width', '100%');
            return;
        }

        const canvasLeft = Math.max(0, canvas.getBoundingClientRect().left);
        const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
        const padding = root.classList.contains('is-editing')
            ? state?.bottomPadding ?? parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding)
            : state?.viewBottomPadding ?? parsePositiveInteger(root.dataset.canvasViewBottomPadding, defaultViewBottomPadding);
        const canvasWidth = Math.max(0, Math.ceil(lowestWidgetRight + padding));
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

    function normalizeWidgetDataValue(value) {
        if (value === null || value === undefined) {
            return '';
        }

        return String(value).trim();
    }

    async function saveDashboardChanges(root, canvas, token, state) {
        await saveLayout(root, canvas, token, state);
        state.pendingRemovedWidgetIds.clear();
        state.addedWidgetIds.clear();
        state.savedSnapshot = captureDashboardSnapshot(canvas, state);
        state.savedSignature = getDashboardSignature(canvas, state);
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
                stringData: normalizeWidgetDataValue(widget.dataset.widgetStringData)
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
                stringData: normalizeWidgetDataValue(widget.dataset.widgetStringData) || null
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
        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => widget.remove());

        snapshot.widgets.forEach((savedWidget) => {
            const widget = savedWidget.cloneNode(true);
            clearDashboardBindingMarkers(widget);
            canvas.appendChild(widget);
            bindWidget(root, canvas, widget, token, nextOrder, state, onChange);
            bindEntryFavoriteToggles(root, widget, token);
            bindEntryListFilters(widget);
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

        const response = await fetch(url, {
            method: 'POST',
            body,
            credentials: 'same-origin',
            headers: {
                'X-Requested-With': 'XMLHttpRequest'
            }
        });

        if (!response.ok) {
            throw new Error(`Dashboard request failed with status ${response.status}.`);
        }

        return response.headers.get('content-type')?.includes('application/json')
            ? response.json()
            : null;
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
        body.appendChild(createWidgetBodyContent(root, widget.payload));
        element.appendChild(body);

        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'dashboard-widget__remove';
        remove.dataset.widgetRemove = '';
        remove.title = root.dataset.removeLabel || 'Remove widget';
        remove.setAttribute('aria-label', root.dataset.removeLabel || 'Remove widget');
        remove.textContent = '×';
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
        if (widget.payload !== 'weekday-date' && widget.payload !== 'portal-entry-list') {
            return null;
        }

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

        const fieldLabel = document.createElement('label');
        const text = document.createElement('span');
        text.textContent = widget.payload === 'portal-entry-list'
            ? root.dataset.columnCountLabel || 'Column count'
            : root.dataset.weekNumberLabel || 'Week number';
        const select = document.createElement('select');
        select.dataset.widgetIntDataControl = '';

        if (widget.payload === 'portal-entry-list') {
            [
                ['0', root.dataset.defaultColumnsLabel || 'Default'],
                ['1', root.dataset.oneColumnLabel || '1 column'],
                ['2', root.dataset.twoColumnsLabel || '2 columns'],
                ['3', root.dataset.threeColumnsLabel || '3 columns']
            ].forEach(([value, labelText]) => {
                const option = document.createElement('option');
                option.value = value;
                option.textContent = labelText;
                select.appendChild(option);
            });
            select.value = String(normalizeColumnCount(widget.intData));
        } else {
            const showOption = document.createElement('option');
            showOption.value = '0';
            showOption.textContent = root.dataset.showWeekNumberLabel || 'Show week number';

            const hideOption = document.createElement('option');
            hideOption.value = '1';
            hideOption.textContent = root.dataset.hideWeekNumberLabel || 'Hide week number';

            select.appendChild(showOption);
            select.appendChild(hideOption);
            select.value = parseNullableInteger(widget.intData) === 1 ? '1' : '0';
        }

        fieldLabel.appendChild(text);
        fieldLabel.appendChild(select);
        panel.appendChild(fieldLabel);

        return { toggle, panel };
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
        return blank;
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
})();
