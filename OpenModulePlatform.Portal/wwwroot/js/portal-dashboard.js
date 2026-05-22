// File: OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js
(() => {
    'use strict';

    const minWidth = 160;
    const minHeight = 96;
    const defaultGridSize = 32;
    const defaultMinCanvasHeight = 640;
    const defaultBottomPadding = 64;
    const favoriteChangedEvent = 'omp:navigation-favorite-changed';

    function initDashboard(root) {
        const canvas = root.querySelector('[data-dashboard-canvas]');
        const editToggle = root.querySelector('[data-dashboard-edit-toggle]');
        const editLabel = root.querySelector('[data-dashboard-edit-label]');
        const addButton = root.querySelector('[data-dashboard-add-open]');
        const resetButton = root.querySelector('[data-dashboard-reset]');
        const alignToggle = root.querySelector('[data-dashboard-align-toggle]');
        const picker = root.querySelector('[data-widget-picker]');
        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';

        if (!canvas || !editToggle || root.dataset.canEdit !== 'true') {
            if (canvas) {
                updateCanvasHeight(root, canvas);
                window.addEventListener('resize', () => updateCanvasHeight(root, canvas));
            }
            return;
        }

        let isEditing = false;
        let maxOrder = getMaxOrder(canvas);
        const state = {
            alignToGrid: root.dataset.alignToGrid !== 'false',
            gridSize: parsePositiveInteger(root.dataset.gridSize, defaultGridSize),
            minCanvasHeight: parsePositiveInteger(root.dataset.minCanvasHeight, defaultMinCanvasHeight),
            bottomPadding: parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding)
        };

        updateAlignToGridState(root, alignToggle, state.alignToGrid);
        updateCanvasHeight(root, canvas, state);
        window.addEventListener('resize', () => updateCanvasHeight(root, canvas, state));

        editToggle.addEventListener('click', async () => {
            if (isEditing) {
                await saveLayout(root, canvas, token);
                setEditing(false);
                return;
            }

            setEditing(true);
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
        });

        alignToggle?.addEventListener('change', async () => {
            const next = alignToggle.checked;
            updateAlignToGridState(root, alignToggle, next);
            state.alignToGrid = next;

            try {
                await postForm(root.dataset.preferenceUrl, token, { alignToGrid: next });
            } catch (error) {
                state.alignToGrid = !next;
                updateAlignToGridState(root, alignToggle, state.alignToGrid);
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

                const element = createWidgetElement(root, widget);
                canvas.appendChild(element);
                snapWidgetToGrid(element, state);
                bindWidget(root, canvas, element, token, () => ++maxOrder, state);
                bindEntryFavoriteToggles(root, element, token);
                updateEmptyState(canvas);
                updateCanvasHeight(root, canvas, state);
                closePicker(picker);
                setEditing(true);
            });
        });

        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            bindWidget(root, canvas, widget, token, () => ++maxOrder, state);
        });
        bindEntryFavoriteToggles(root, root, token);

        function setEditing(next) {
            isEditing = next;
            root.classList.toggle('is-editing', isEditing);
            editToggle.setAttribute('aria-pressed', isEditing ? 'true' : 'false');
            if (editLabel) {
                editLabel.textContent = isEditing
                    ? root.dataset.doneLabel || 'Done'
                    : root.dataset.editLabel || 'Edit dashboard';
            }
        }
    }

    function bindWidget(root, canvas, widget, token, nextOrder, state) {
        const removeButton = widget.querySelector('[data-widget-remove]');
        const resizeHandle = widget.querySelector('[data-widget-resize]');

        widget.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            if (event.target.closest('button, a, input, textarea, select, [data-widget-resize]')) {
                return;
            }

            event.preventDefault();
            bringToFront(widget, nextOrder());
            startDrag(root, canvas, widget, event, state);
        });

        resizeHandle?.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            bringToFront(widget, nextOrder());
            startResize(root, canvas, widget, event, state);
        });

        removeButton?.addEventListener('click', async (event) => {
            event.preventDefault();
            event.stopPropagation();

            const userActiveWidgetId = parseInt(widget.dataset.userActiveWidgetId || '0', 10);
            if (!userActiveWidgetId) {
                return;
            }

            await postForm(root.dataset.removeUrl, token, { userActiveWidgetId });
            widget.remove();
            updateEmptyState(canvas);
            updateCanvasHeight(root, canvas, state);
        });
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

        getEntryLists(root, 'favorites').forEach((list) => {
            const existing = Array.from(list.querySelectorAll('[data-dashboard-entry-row]'))
                .find((row) => row.dataset.entryKey === entryKey);

            if (isFavorite && !existing) {
                const source = findSourceEntryRow(root, entryKey, sourceRow);
                if (source) {
                    const clone = source.cloneNode(true);
                    clone.querySelectorAll('[data-dashboard-entry-favorite-toggle]').forEach((button) => {
                        delete button.dataset.dashboardFavoriteBound;
                    });
                    updateEntryRowFavoriteState(root, clone, true);
                    list.appendChild(clone);
                    bindEntryFavoriteToggles(root, clone, root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '');
                } else if (payload) {
                    const row = createEntryRowFromFavoritePayload(root, payload);
                    if (row) {
                        list.appendChild(row);
                        bindEntryFavoriteToggles(root, row, root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '');
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

    function createEntryRowFromFavoritePayload(root, payload) {
        const normalized = normalizeFavoritePayload(payload);
        if (!normalized) {
            return null;
        }

        const title = normalized.entryTitle || normalized.label || normalized.entryKey;
        const href = normalized.href || '#';
        const logoFallback = normalized.logoFallback || buildLogoFallback(title);
        const row = document.createElement('div');
        row.className = 'dashboard-entry-list__row';
        row.setAttribute('data-dashboard-entry-row', '');
        row.dataset.entryKey = normalized.entryKey;
        row.dataset.entryTitle = title;
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
        updateEntryRowFavoriteState(root, row, true);
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
        const allRows = getEntryLists(root, 'all')
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
            empty.hidden = list.querySelectorAll('[data-dashboard-entry-row]').length > 0;
        }
    }

    function startDrag(root, canvas, widget, event, state) {
        const canvasRect = canvas.getBoundingClientRect();
        const widgetRect = widget.getBoundingClientRect();
        const startX = event.clientX;
        const startY = event.clientY;
        const startLeft = widgetRect.left - canvasRect.left + canvas.scrollLeft;
        const startTop = widgetRect.top - canvasRect.top + canvas.scrollTop;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-moving');

        const move = (moveEvent) => {
            const nextLeft = Math.max(0, startLeft + moveEvent.clientX - startX);
            const nextTop = Math.max(0, startTop + moveEvent.clientY - startY);
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
        };

        widget.addEventListener('pointermove', move);
        widget.addEventListener('pointerup', end);
        widget.addEventListener('pointercancel', end);
    }

    function startResize(root, canvas, widget, event, state) {
        const startX = event.clientX;
        const startY = event.clientY;
        const startWidth = widget.offsetWidth;
        const startHeight = widget.offsetHeight;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-resizing');

        const move = (moveEvent) => {
            const nextWidth = Math.max(minWidth, startWidth + moveEvent.clientX - startX);
            const nextHeight = Math.max(minHeight, startHeight + moveEvent.clientY - startY);
            widget.style.width = `${Math.max(minWidth, snapIfNeeded(nextWidth, state))}px`;
            widget.style.height = `${Math.max(minHeight, snapIfNeeded(nextHeight, state))}px`;
            updateCanvasHeight(root, canvas, state);
        };

        const end = () => {
            widget.classList.remove('is-resizing');
            widget.removeEventListener('pointermove', move);
            widget.removeEventListener('pointerup', end);
            widget.removeEventListener('pointercancel', end);
            updateCanvasHeight(root, canvas, state);
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
        widget.style.width = `${Math.max(minWidth, snapIfNeeded(widget.offsetWidth, state))}px`;
        widget.style.height = `${Math.max(minHeight, snapIfNeeded(widget.offsetHeight, state))}px`;
    }

    function snapIfNeeded(value, state) {
        if (!state?.alignToGrid) {
            return Math.round(value);
        }

        const gridSize = state.gridSize || defaultGridSize;
        return Math.round(value / gridSize) * gridSize;
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
        const minCanvasHeight = getMinCanvasHeight(root, canvas, state);
        const bottomPadding = state?.bottomPadding ?? parsePositiveInteger(root.dataset.canvasBottomPadding, defaultBottomPadding);
        const lowestWidgetBottom = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
            .reduce((max, widget) => Math.max(max, parsePixel(widget.style.top) + widget.offsetHeight), 0);
        const nextHeight = Math.max(minCanvasHeight, lowestWidgetBottom + bottomPadding);
        canvas.style.setProperty('--dashboard-canvas-height', `${Math.ceil(nextHeight)}px`);
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

    async function saveLayout(root, canvas, token) {
        const widgets = Array.from(canvas.querySelectorAll('[data-dashboard-widget]'))
            .map((widget) => ({
                userActiveWidgetId: parseInt(widget.dataset.userActiveWidgetId || '0', 10),
                offsetTop: parsePixel(widget.style.top),
                offsetLeft: parsePixel(widget.style.left),
                width: Math.round(widget.offsetWidth),
                height: Math.round(widget.offsetHeight),
                orderPriority: parseInt(widget.style.zIndex || '0', 10) || 0,
                title: null,
                intData: null,
                stringData: null
            }))
            .filter((widget) => widget.userActiveWidgetId > 0);

        await postForm(root.dataset.saveUrl, token, {
            widgetsJson: JSON.stringify(widgets)
        });
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

        const resize = document.createElement('span');
        resize.className = 'dashboard-widget__resize';
        resize.dataset.widgetResize = '';
        resize.setAttribute('aria-hidden', 'true');
        element.appendChild(resize);

        return element;
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
