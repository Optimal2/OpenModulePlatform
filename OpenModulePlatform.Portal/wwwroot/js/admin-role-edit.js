// File: OpenModulePlatform.Portal/wwwroot/js/admin-role-edit.js
(() => {
    'use strict';

    const form = document.getElementById('role-details-form');
    const saveButton = document.getElementById('save-role-button');
    const roleNameInput = document.getElementById('Input_Name');
    const roleDescriptionInput = document.getElementById('Input_Description');
    const returnUrlInput = document.getElementById('ReturnUrl');
    const dialog = document.getElementById('unsaved-role-dialog');
    const saveAndLeaveButton = document.getElementById('unsaved-save-button');
    const discardButton = document.getElementById('unsaved-discard-button');
    const stayButton = document.getElementById('unsaved-stay-button');

    if (!form || !saveButton || !roleNameInput || !roleDescriptionInput || !returnUrlInput) {
        return;
    }

    const discardConfirmText = dialog?.dataset.discardConfirmText
        || 'You have unsaved role changes. Press OK to discard them and leave this page, or Cancel to stay.';

    const trackedInputs = [roleNameInput, roleDescriptionInput];
    const initialValues = trackedInputs.map(input => input.dataset.originalValue ?? '');
    let isDirty = false;
    let allowUnloadPrompt = true;
    let pendingUrl = null;

    function updateDirtyState() {
        isDirty = trackedInputs.some((input, index) => (input.value ?? '') !== initialValues[index]);
        saveButton.disabled = !isDirty;
        saveButton.setAttribute('aria-disabled', String(!isDirty));
    }

    function isModifiedClick(event) {
        return event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0;
    }

    function shouldInterceptNavigation(anchor) {
        if (!anchor || !anchor.href) {
            return false;
        }

        if (anchor.target && anchor.target !== '_self') {
            return false;
        }

        const href = anchor.getAttribute('href');
        if (!href || href.startsWith('#')) {
            return false;
        }

        const targetUrl = new URL(anchor.href, window.location.origin);
        return targetUrl.origin === window.location.origin;
    }

    trackedInputs.forEach(input => input.addEventListener('input', updateDirtyState));

    form.addEventListener('submit', () => {
        allowUnloadPrompt = false;
    });

    document.addEventListener('click', event => {
        const anchor = event.target.closest('a');
        if (!anchor || isModifiedClick(event) || !isDirty || !shouldInterceptNavigation(anchor)) {
            return;
        }

        event.preventDefault();
        pendingUrl = anchor.href;

        if (dialog && typeof dialog.showModal === 'function') {
            dialog.showModal();
            return;
        }

        const discard = window.confirm(discardConfirmText);
        if (discard) {
            allowUnloadPrompt = false;
            window.location.href = pendingUrl;
        }
    });

    window.addEventListener('beforeunload', event => {
        if (!isDirty || !allowUnloadPrompt) {
            return;
        }

        event.preventDefault();
        event.returnValue = '';
    });

    if (saveAndLeaveButton) {
        saveAndLeaveButton.addEventListener('click', () => {
            if (!pendingUrl) {
                dialog?.close();
                return;
            }

            returnUrlInput.value = pendingUrl;
            allowUnloadPrompt = false;
            dialog?.close();
            form.requestSubmit();
        });
    }

    if (discardButton) {
        discardButton.addEventListener('click', () => {
            if (!pendingUrl) {
                dialog?.close();
                return;
            }

            allowUnloadPrompt = false;
            const targetUrl = pendingUrl;
            dialog?.close();
            window.location.href = targetUrl;
        });
    }

    if (stayButton) {
        stayButton.addEventListener('click', () => {
            pendingUrl = null;
            dialog?.close();
        });
    }

    dialog?.addEventListener('cancel', () => {
        pendingUrl = null;
    });

    updateDirtyState();
})();
