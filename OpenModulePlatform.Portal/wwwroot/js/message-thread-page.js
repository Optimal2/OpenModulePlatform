// File: OpenModulePlatform.Portal/wwwroot/js/message-thread-page.js
(() => {
    'use strict';

    const UPDATE_MANUAL_MODE = 'manual';
    const UPDATE_POLL_MODE = 'poll';
    const UPDATE_PUSH_MODE = 'push';
    const DEFAULT_POLL_INTERVAL_SECONDS = 60;
    const MIN_POLL_INTERVAL_SECONDS = 10;
    const MAX_POLL_INTERVAL_SECONDS = 3600;
    const MESSAGE_PUSH_CATEGORY = 'topbar.message-state-changed';
    const MESSAGE_CHANGED_EVENT_NAME = 'omp:message-state-changed';
    const PUSH_EVENT_NAME = 'omp:push-event';
    const REFRESH_DEBOUNCE_MS = 350;

    const scrollContainer = document.querySelector('[data-message-thread-scroll]');
    const form = document.querySelector('[data-message-thread-composer]');
    const scrollInput = document.querySelector('[data-message-thread-scroll-input]');
    const status = document.querySelector('[data-message-thread-status]');
    const errorBanner = document.querySelector('.portal-message-thread__error-banner');

    if (!scrollContainer || !form) {
        return;
    }

    const restoreValue = Number.parseInt(scrollContainer.dataset.restoreScrollTop || '', 10);
    if (Number.isFinite(restoreValue) && restoreValue >= 0) {
        scrollContainer.scrollTop = restoreValue;
    } else {
        scrollContainer.scrollTop = scrollContainer.scrollHeight;
    }

    const syncScrollInput = () => {
        if (scrollInput) {
            const distanceFromBottom = scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;
            scrollInput.value = distanceFromBottom <= 24
                ? ''
                : Math.round(scrollContainer.scrollTop).toString();
        }
    };

    form?.addEventListener('submit', syncScrollInput);

    // Pasting an image (a screenshot, an image copied from a page) into the
    // composer attaches it, the same way picking it with the attach button
    // would. Both paths end in the file input, so the send path is unchanged.
    // Only files reach here: an image copied as a bitmap arrives as a PNG,
    // so animation survives only when the clipboard holds the GIF file itself.
    const fileInput = form.querySelector('input[type="file"]');
    const pendingTray = form.querySelector('[data-message-thread-pending]');
    let pendingUrls = [];
    const formatSize = (bytes) => bytes >= 1024 * 1024
        ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
        : `${Math.max(1, Math.round(bytes / 1024))} kB`;
    // The tray mirrors the file input: one chip per waiting file with a
    // thumbnail (object URL for images, the attachments glyph otherwise), the
    // name, the size and a remove button. Removing rebuilds the FileList
    // without that file, so the input stays the single source of truth.
    const renderPending = () => {
        if (!pendingTray || !fileInput) {
            return;
        }
        pendingUrls.forEach((url) => URL.revokeObjectURL(url));
        pendingUrls = [];
        pendingTray.replaceChildren();
        const files = Array.from(fileInput.files || []);
        pendingTray.hidden = files.length === 0;
        files.forEach((file, index) => {
            const item = document.createElement('div');
            item.className = 'portal-message-thread__pending-item';
            if (file.type.startsWith('image/')) {
                const url = URL.createObjectURL(file);
                pendingUrls.push(url);
                const thumb = document.createElement('img');
                thumb.className = 'portal-message-thread__pending-thumb';
                thumb.src = url;
                thumb.alt = '';
                item.appendChild(thumb);
            } else {
                const glyph = document.createElement('span');
                glyph.className = 'portal-message-thread__pending-glyph';
                glyph.setAttribute('aria-hidden', 'true');
                item.appendChild(glyph);
            }
            const text = document.createElement('span');
            text.className = 'portal-message-thread__pending-text';
            const name = document.createElement('span');
            name.className = 'portal-message-thread__pending-name';
            name.textContent = file.name;
            name.title = file.name;
            const size = document.createElement('span');
            size.className = 'portal-message-thread__pending-size';
            size.textContent = formatSize(file.size);
            text.append(name, size);
            item.appendChild(text);
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'portal-message-thread__pending-remove';
            remove.dataset.pendingRemove = String(index);
            remove.title = pendingTray.dataset.removeText || 'Remove';
            remove.setAttribute('aria-label', `${pendingTray.dataset.removeText || 'Remove'}: ${file.name}`);
            item.appendChild(remove);
            pendingTray.appendChild(item);
        });
    };
    const updateAttachCount = () => renderPending();
    if (fileInput && typeof DataTransfer === 'function') {
        fileInput.addEventListener('change', updateAttachCount);
        form.addEventListener('reset', () => window.setTimeout(updateAttachCount, 0));
        pendingTray?.addEventListener('click', (event) => {
            const remove = event.target.closest('[data-pending-remove]');
            if (!remove) {
                return;
            }
            const skip = Number.parseInt(remove.dataset.pendingRemove || '', 10);
            const transfer = new DataTransfer();
            Array.from(fileInput.files || []).forEach((file, index) => {
                if (index !== skip) {
                    transfer.items.add(file);
                }
            });
            fileInput.files = transfer.files;
            renderPending();
        });
        form.addEventListener('paste', (event) => {
            // An edit cannot take attachments, so a pasted image is ignored
            // while one is under way, the same as the hidden attach button.
            if (form.dataset.editingMessageId) {
                return;
            }
            const pasted = Array.from(event.clipboardData?.files || [])
                .filter((file) => file.type.startsWith('image/'));
            if (pasted.length === 0) {
                return;
            }
            event.preventDefault();
            const transfer = new DataTransfer();
            Array.from(fileInput.files || []).forEach((file) => transfer.items.add(file));
            pasted.forEach((file, index) => {
                const extension = (file.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
                const name = file.name && file.name !== 'image.png' ? file.name : `pasted-${Date.now()}-${index + 1}.${extension}`;
                transfer.items.add(new File([file], name, { type: file.type }));
            });
            fileInput.files = transfer.files;
            updateAttachCount();
        });
        updateAttachCount();
    }

    // Edit mode: clicking the pencil on one of the user's own messages puts its
    // text in the composer, points the form at the Edit handler and shows a
    // strip with Cancel. Sending then rewrites the message; Escape or Cancel
    // returns to composing a new one. Attachments cannot be added to an edit.
    const sendAction = form.getAttribute('action') || '';
    const editUrlTemplate = form.dataset.editUrlTemplate || '';
    const editingStrip = form.querySelector('[data-message-thread-editing]');
    const textInput = form.querySelector('input[name="MessageContent"]');
    const attachButton = form.querySelector('.portal-message-thread__composer-button--attach');
    const attachInput = form.querySelector('input[type="file"]');

    // What the user had typed (and attached) before the pencil was pressed: put
    // aside while a message is edited, and put back when the edit is saved or
    // abandoned, so a half-written new message is never lost to an edit.
    let draft = null;
    const setAttachedFiles = (files) => {
        if (!attachInput) {
            return;
        }
        if (typeof DataTransfer === 'function') {
            const transfer = new DataTransfer();
            Array.from(files ?? []).forEach((file) => transfer.items.add(file));
            attachInput.files = transfer.files;
        } else {
            attachInput.value = '';
        }
        attachInput.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const exitEditMode = () => {
        if (!form.dataset.editingMessageId) {
            return;
        }
        delete form.dataset.editingMessageId;
        form.setAttribute('action', sendAction);
        if (editingStrip) {
            editingStrip.hidden = true;
        }
        if (attachButton) {
            attachButton.hidden = false;
        }
        if (textInput) {
            textInput.value = draft?.text ?? '';
        }
        setAttachedFiles(draft?.files ?? []);
        draft = null;
        scrollContainer.querySelectorAll('.portal-message-thread__message.is-editing')
            .forEach((message) => message.classList.remove('is-editing'));
    };

    const enterEditMode = (messageId, content) => {
        if (!editUrlTemplate || !textInput) {
            return;
        }
        // Leaving a previous edit first puts its draft back, so the draft taken
        // here is always the user's own text, never another message's.
        exitEditMode();
        draft = { text: textInput.value, files: Array.from(attachInput?.files ?? []) };
        form.dataset.editingMessageId = String(messageId);
        form.setAttribute('action', editUrlTemplate.replace(/messageId=0/i, `messageId=${encodeURIComponent(messageId)}`));
        if (editingStrip) {
            editingStrip.hidden = false;
        }
        if (attachButton) {
            attachButton.hidden = true;
        }
        setAttachedFiles([]);
        textInput.value = content;
        scrollContainer.querySelector(`[data-message-id="${CSS.escape(String(messageId))}"]`)?.classList.add('is-editing');
        textInput.focus();
        textInput.setSelectionRange(textInput.value.length, textInput.value.length);
    };

    scrollContainer.addEventListener('click', (event) => {
        const edit = event.target.closest('[data-message-edit]');
        if (!edit || form.dataset.messageThreadSubmitting === 'true') {
            return;
        }
        enterEditMode(edit.dataset.messageEdit, edit.dataset.messageContent || '');
    });
    form.querySelector('[data-message-thread-edit-cancel]')?.addEventListener('click', exitEditMode);
    textInput?.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && form.dataset.editingMessageId) {
            event.preventDefault();
            exitEditMode();
        }
    });

    // Remove, leave and delete-message are plain forms; a confirm keeps a stray
    // click from taking someone out of the group or a message out of the
    // thread. Delegated: the message list is replaced on every refresh, so a
    // listener bound per button would be gone within a minute.
    document.addEventListener('click', (event) => {
        const button = event.target.closest('button[data-confirm]');
        if (button && !window.confirm(button.dataset.confirm)) {
            event.preventDefault();
        }
    });

    // Everything below is the live part (refresh, fetch send, push); the
    // history view has none of it, but editing and the confirm guards above
    // work there too through a plain form post.
    if (scrollContainer.dataset.liveDisabled === 'true') {
        return;
    }

    const conversationId = Number.parseInt(form.dataset.conversationId || '', 10);
    const refreshUrl = form.dataset.refreshUrl || '';
    if (!Number.isFinite(conversationId) || conversationId <= 0 || refreshUrl.length === 0) {
        return;
    }

    let refreshTimer = 0;
    let pollTimer = 0;
    let refreshRunning = false;
    let pendingRefresh = false;

    const parseIntervalSeconds = (value, fallback) => {
        const parsed = Number.parseInt(value || '', 10);
        if (!Number.isFinite(parsed)) {
            return fallback;
        }

        return Math.min(MAX_POLL_INTERVAL_SECONDS, Math.max(MIN_POLL_INTERVAL_SECONDS, parsed));
    };

    const getTopbarConfig = () => {
        const topbar = document.querySelector('[data-portal-topbar-root]');
        const rawMode = (topbar?.getAttribute('data-notification-update-mode') || UPDATE_POLL_MODE)
            .trim()
            .toLowerCase();
        const mode = rawMode === UPDATE_MANUAL_MODE || rawMode === UPDATE_PUSH_MODE
            ? rawMode
            : UPDATE_POLL_MODE;
        const visibleInterval = parseIntervalSeconds(
            topbar?.getAttribute('data-notification-poll-interval'),
            DEFAULT_POLL_INTERVAL_SECONDS) * 1000;
        const hiddenInterval = parseIntervalSeconds(
            topbar?.getAttribute('data-topbar-polling-hidden-interval'),
            visibleInterval / 1000) * 1000;

        return {
            mode,
            interval: document.visibilityState === 'hidden' ? hiddenInterval : visibleInterval
        };
    };

    const getContentRoot = () => scrollContainer.querySelector('[data-message-thread-content]');

    const getLatestMessageId = () => {
        const latest = Number.parseInt(getContentRoot()?.dataset.latestMessageId || '0', 10);
        return Number.isFinite(latest) ? latest : 0;
    };

    const isOwnMessage = (messageId) => {
        const id = String(messageId);
        const message = Array.from(scrollContainer.querySelectorAll('[data-message-id]'))
            .find((item) => item.dataset.messageId === id);
        return message?.classList.contains('is-own') === true;
    };

    const playIncomingMessageSound = (messageId, options) => {
        if (options.suppressSound === true || isOwnMessage(messageId)) {
            return;
        }

        window.ompToastSound?.playMessage(false);
    };

    const isNearBottom = () => {
        const distanceFromBottom = scrollContainer.scrollHeight - scrollContainer.clientHeight - scrollContainer.scrollTop;
        return distanceFromBottom <= 80;
    };

    const announceUpdated = () => {
        if (!status) {
            return;
        }

        status.textContent = '';
        window.setTimeout(() => {
            status.textContent = form.dataset.updatedText || 'Conversation updated';
        }, 0);
    };

    const replaceMessages = (html, options = {}) => {
        const currentRoot = getContentRoot();
        if (!currentRoot) {
            return false;
        }

        const template = document.createElement('template');
        template.innerHTML = (html || '').trim();
        const nextRoot = template.content.querySelector('[data-message-thread-content]');
        if (!nextRoot) {
            return false;
        }

        const previousLatestMessageId = getLatestMessageId();
        const shouldStickToBottom = options.stickToBottom === true || isNearBottom();
        const previousScrollTop = scrollContainer.scrollTop;
        currentRoot.replaceWith(nextRoot);

        if (shouldStickToBottom) {
            scrollContainer.scrollTop = scrollContainer.scrollHeight;
        } else {
            scrollContainer.scrollTop = previousScrollTop;
        }

        const latestMessageId = getLatestMessageId();
        if (latestMessageId > previousLatestMessageId) {
            announceUpdated();
            playIncomingMessageSound(latestMessageId, options);
        }

        return true;
    };

    const setComposerErrors = (messages) => {
        if (!errorBanner) {
            return;
        }

        const list = Array.isArray(messages)
            ? messages.filter((message) => typeof message === 'string' && message.trim().length > 0)
            : [];

        errorBanner.textContent = list.join(' ');
        errorBanner.classList.toggle('validation-summary-errors', list.length > 0);
        errorBanner.classList.toggle('validation-summary-valid', list.length === 0);
    };

    const readSubmitErrors = async (response) => {
        const fallback = form.dataset.sendErrorText || 'The message could not be sent.';
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            try {
                const payload = await response.json();
                if (Array.isArray(payload?.errors) && payload.errors.length > 0) {
                    return payload.errors;
                }
            } catch {
                return [fallback];
            }
        }

        return [fallback];
    };

    const setComposerDisabled = (disabled) => {
        form.querySelectorAll('input, button').forEach((control) => {
            control.disabled = disabled;
        });
    };

    const refreshMessages = async () => {
        if (refreshRunning) {
            pendingRefresh = true;
            return;
        }

        refreshRunning = true;
        try {
            const response = await fetch(refreshUrl, {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403) {
                // A refresh that is refused after the page loaded means the user
                // is no longer in the conversation (removed from the group, or
                // left it in another tab): back to the list.
                const removedUrl = form.dataset.removedUrl;
                if (response.status === 403 && removedUrl) {
                    window.location.assign(removedUrl);
                }
                return;
            }

            if (!response.ok) {
                throw new Error('Message thread refresh failed with status ' + response.status + '.');
            }

            replaceMessages(await response.text());
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP message thread refresh failed.', error);
            }
        } finally {
            refreshRunning = false;
            if (pendingRefresh) {
                pendingRefresh = false;
                scheduleRefresh(REFRESH_DEBOUNCE_MS);
            }
        }
    };

    // Deleting a message: the per-message form is posted in the background
    // and the returned list swapped in, the same as sending. If the message
    // was being edited, the edit is abandoned and the draft put back.
    const submitDelete = async (event) => {
        const deleteForm = event.target;
        if (!(deleteForm instanceof HTMLFormElement) || !deleteForm.hasAttribute('data-message-delete')) {
            return;
        }
        event.preventDefault();
        if (deleteForm.dataset.messageThreadSubmitting === 'true' || form.dataset.messageThreadSubmitting === 'true') {
            return;
        }

        deleteForm.dataset.messageThreadSubmitting = 'true';
        setComposerErrors([]);
        try {
            const response = await fetch(deleteForm.action, {
                method: 'POST',
                body: new FormData(deleteForm),
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403 || response.redirected) {
                setComposerErrors([form.dataset.deleteErrorText || 'The message could not be deleted.']);
                return;
            }

            if (!response.ok) {
                setComposerErrors(await readSubmitErrors(response));
                return;
            }

            if (form.dataset.editingMessageId === deleteForm.dataset.messageDelete) {
                exitEditMode();
            }

            if (!replaceMessages(await response.text(), { suppressSound: true })) {
                setComposerErrors([form.dataset.deleteErrorText || 'The message could not be deleted.']);
            }
        } catch (error) {
            setComposerErrors([form.dataset.deleteErrorText || 'The message could not be deleted.']);
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP message delete failed.', error);
            }
        } finally {
            delete deleteForm.dataset.messageThreadSubmitting;
        }
    };
    scrollContainer.addEventListener('submit', submitDelete);

    const submitMessage = async (event) => {
        event.preventDefault();
        syncScrollInput();

        if (form.dataset.messageThreadSubmitting === 'true') {
            return;
        }

        form.dataset.messageThreadSubmitting = 'true';
        setComposerErrors([]);

        const body = new FormData(form);
        setComposerDisabled(true);

        try {
            const response = await fetch(form.action, {
                method: 'POST',
                body,
                credentials: 'same-origin',
                cache: 'no-store',
                headers: {
                    'Accept': 'text/html',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 403 || response.redirected) {
                setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
                return;
            }

            if (!response.ok) {
                setComposerErrors(await readSubmitErrors(response));
                return;
            }

            const html = await response.text();
            if (!replaceMessages(html, { stickToBottom: true, suppressSound: true })) {
                setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
                return;
            }

            form.reset();
            exitEditMode();
            if (scrollInput) {
                scrollInput.value = '';
            }
        } catch (error) {
            setComposerErrors([form.dataset.sendErrorText || 'The message could not be sent.']);
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP message send failed.', error);
            }
        } finally {
            form.dataset.messageThreadSubmitting = 'false';
            setComposerDisabled(false);
        }
    };

    form.addEventListener('submit', submitMessage);

    function scheduleRefresh(delay) {
        if (refreshTimer) {
            window.clearTimeout(refreshTimer);
        }

        refreshTimer = window.setTimeout(() => {
            refreshTimer = 0;
            refreshMessages();
        }, Math.max(0, delay));
    }

    const schedulePoll = () => {
        if (pollTimer) {
            window.clearTimeout(pollTimer);
            pollTimer = 0;
        }

        const config = getTopbarConfig();
        if (config.mode !== UPDATE_POLL_MODE) {
            return;
        }

        pollTimer = window.setTimeout(async () => {
            pollTimer = 0;
            await refreshMessages();
            schedulePoll();
        }, config.interval);
    };

    const getPayloadConversationId = (payload) => {
        if (!payload || typeof payload !== 'object') {
            return null;
        }

        const value = payload.conversationId;
        const parsed = typeof value === 'number'
            ? value
            : Number.parseInt(value || '', 10);

        return Number.isFinite(parsed) ? parsed : null;
    };

    const handlePushEvent = (event) => {
        const detail = event?.detail || {};
        const category = (detail.category || detail.envelope?.category || '').toString().toLowerCase();
        if (category !== MESSAGE_PUSH_CATEGORY) {
            return;
        }

        const payload = detail.payload || detail.envelope?.payload || null;
        const payloadAction = (payload?.action || '').toString().toLowerCase();
        const isReadAll = payloadAction === 'read-all';
        if (!isReadAll && getPayloadConversationId(payload) !== conversationId) {
            return;
        }

        const config = getTopbarConfig();
        if (config.mode !== UPDATE_PUSH_MODE) {
            return;
        }

        scheduleRefresh(REFRESH_DEBOUNCE_MS);
    };

    const handleMessageChanged = (event) => {
        const detail = event?.detail || {};
        if (detail.allRead === true) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
            return;
        }

        if (Number.isFinite(Number(detail.unreadCount)) && Number(detail.unreadCount) === 0) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    const handleVisibilityOrFocus = () => {
        const config = getTopbarConfig();
        if (config.mode === UPDATE_POLL_MODE) {
            schedulePoll();
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
            return;
        }

        if (config.mode === UPDATE_PUSH_MODE && document.visibilityState === 'visible' && document.hasFocus()) {
            scheduleRefresh(REFRESH_DEBOUNCE_MS);
        }
    };

    window.addEventListener(PUSH_EVENT_NAME, handlePushEvent);
    window.addEventListener(MESSAGE_CHANGED_EVENT_NAME, handleMessageChanged);
    window.addEventListener('focus', handleVisibilityOrFocus);
    document.addEventListener('visibilitychange', handleVisibilityOrFocus);

    if (getTopbarConfig().mode === UPDATE_POLL_MODE) {
        schedulePoll();
    }
})();
