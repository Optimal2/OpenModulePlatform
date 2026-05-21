// File: OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js
(() => {
    'use strict';

    const minWidth = 160;
    const minHeight = 96;

    function initDashboard(root) {
        const canvas = root.querySelector('[data-dashboard-canvas]');
        const editToggle = root.querySelector('[data-dashboard-edit-toggle]');
        const editLabel = root.querySelector('[data-dashboard-edit-label]');
        const addButton = root.querySelector('[data-dashboard-add-open]');
        const resetButton = root.querySelector('[data-dashboard-reset]');
        const picker = root.querySelector('[data-widget-picker]');
        const token = root.querySelector('[data-dashboard-token-form] input[name="__RequestVerificationToken"]')?.value || '';

        if (!canvas || !editToggle || root.dataset.canEdit !== 'true') {
            return;
        }

        let isEditing = false;
        let maxOrder = getMaxOrder(canvas);

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
                bindWidget(root, canvas, element, token, () => ++maxOrder);
                updateEmptyState(canvas);
                closePicker(picker);
                setEditing(true);
            });
        });

        canvas.querySelectorAll('[data-dashboard-widget]').forEach((widget) => {
            bindWidget(root, canvas, widget, token, () => ++maxOrder);
        });

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

    function bindWidget(root, canvas, widget, token, nextOrder) {
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
            startDrag(canvas, widget, event);
        });

        resizeHandle?.addEventListener('pointerdown', (event) => {
            if (!root.classList.contains('is-editing') || event.button !== 0) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            bringToFront(widget, nextOrder());
            startResize(widget, event);
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
        });
    }

    function startDrag(canvas, widget, event) {
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
            widget.style.left = `${Math.round(nextLeft)}px`;
            widget.style.top = `${Math.round(nextTop)}px`;
        };

        const end = () => {
            widget.classList.remove('is-moving');
            widget.removeEventListener('pointermove', move);
            widget.removeEventListener('pointerup', end);
            widget.removeEventListener('pointercancel', end);
        };

        widget.addEventListener('pointermove', move);
        widget.addEventListener('pointerup', end);
        widget.addEventListener('pointercancel', end);
    }

    function startResize(widget, event) {
        const startX = event.clientX;
        const startY = event.clientY;
        const startWidth = widget.offsetWidth;
        const startHeight = widget.offsetHeight;

        widget.setPointerCapture(event.pointerId);
        widget.classList.add('is-resizing');

        const move = (moveEvent) => {
            const nextWidth = Math.max(minWidth, startWidth + moveEvent.clientX - startX);
            const nextHeight = Math.max(minHeight, startHeight + moveEvent.clientY - startY);
            widget.style.width = `${Math.round(nextWidth)}px`;
            widget.style.height = `${Math.round(nextHeight)}px`;
        };

        const end = () => {
            widget.classList.remove('is-resizing');
            widget.removeEventListener('pointermove', move);
            widget.removeEventListener('pointerup', end);
            widget.removeEventListener('pointercancel', end);
        };

        widget.addEventListener('pointermove', move);
        widget.addEventListener('pointerup', end);
        widget.addEventListener('pointercancel', end);
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
        element.style.height = `${widget.height || 180}px`;
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
            return template.content.firstElementChild.cloneNode(true);
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
})();
