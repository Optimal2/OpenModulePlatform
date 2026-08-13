// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-forms.js
// Shared OMP form behaviours.
//
// window.ompConfirm(message, options) -> Promise<boolean>
//   Shows an OMP-styled modal <dialog> instead of the native window.confirm.
//   options: { okLabel, cancelLabel } - plain strings, already localized by the page.
//
// Declarative wiring (initialized on DOMContentLoaded):
//   <form data-omp-confirm="message"> - submit is intercepted until confirmed.
//   <a data-omp-confirm="message">    - navigation is intercepted until confirmed.
//   Optional on the same element: data-omp-confirm-ok / data-omp-confirm-cancel
//   set the button labels.
(function () {
    'use strict';

    function ensureDialog() {
        let dialog = document.getElementById('omp-confirm-dialog');
        if (dialog) {
            return dialog;
        }

        dialog = document.createElement('dialog');
        dialog.id = 'omp-confirm-dialog';
        dialog.className = 'omp-confirm-dialog';

        const body = document.createElement('form');
        body.method = 'dialog';
        body.className = 'omp-confirm-dialog__body';

        const message = document.createElement('p');
        message.className = 'omp-confirm-dialog__message';

        const actions = document.createElement('div');
        actions.className = 'omp-confirm-dialog__actions';

        const cancelButton = document.createElement('button');
        cancelButton.type = 'button';
        cancelButton.className = 'omp-confirm-dialog__cancel';

        const okButton = document.createElement('button');
        okButton.type = 'button';
        okButton.className = 'omp-confirm-dialog__ok';

        actions.append(cancelButton, okButton);
        body.append(message, actions);
        dialog.append(body);
        document.body.append(dialog);
        return dialog;
    }

    function ompConfirm(message, options) {
        const settings = options || {};
        if (typeof HTMLDialogElement !== 'function') {
            return Promise.resolve(window.confirm(message));
        }

        const dialog = ensureDialog();
        dialog.querySelector('.omp-confirm-dialog__message').textContent = message || '';
        const okButton = dialog.querySelector('.omp-confirm-dialog__ok');
        const cancelButton = dialog.querySelector('.omp-confirm-dialog__cancel');
        okButton.textContent = settings.okLabel || 'OK';
        cancelButton.textContent = settings.cancelLabel || 'Cancel';

        return new Promise((resolve) => {
            const finish = (result) => {
                okButton.removeEventListener('click', onOk);
                cancelButton.removeEventListener('click', onCancel);
                dialog.removeEventListener('cancel', onDialogCancel);
                if (dialog.open) {
                    dialog.close();
                }
                resolve(result);
            };
            const onOk = () => finish(true);
            const onCancel = () => finish(false);
            const onDialogCancel = (event) => {
                event.preventDefault();
                finish(false);
            };

            okButton.addEventListener('click', onOk);
            cancelButton.addEventListener('click', onCancel);
            dialog.addEventListener('cancel', onDialogCancel);
            dialog.showModal();
            cancelButton.focus();
        });
    }

    function labelsFrom(element) {
        return {
            okLabel: element.getAttribute('data-omp-confirm-ok') || undefined,
            cancelLabel: element.getAttribute('data-omp-confirm-cancel') || undefined
        };
    }

    // Forms whose confirmation has just been accepted, so the re-submit passes through.
    const confirmedForms = new WeakSet();

    // Delegated from the document rather than bound per element at load time. Several
    // pages replace a container's innerHTML on a push event or a 60-second poll --
    // IbsPackager's manual review list, jobs list and review history all do -- and the
    // replacement markup carried no listeners, so the confirmation on Force, Reject and
    // Run-again silently stopped appearing within a minute of page load. Those are
    // irreversible actions, which is exactly what the dialog exists to guard (R7-C2).
    function initConfirmWiring() {
        document.addEventListener('submit', (event) => {
            const form = event.target;
            if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-omp-confirm')) {
                return;
            }

            if (confirmedForms.has(form)) {
                confirmedForms.delete(form);
                return;
            }

            event.preventDefault();
            // Nothing downstream should act on a submit the operator has not confirmed.
            event.stopPropagation();

            const submitter = event.submitter;
            ompConfirm(form.getAttribute('data-omp-confirm'), labelsFrom(form)).then((ok) => {
                if (!ok) {
                    return;
                }

                confirmedForms.add(form);
                // requestSubmit keeps submit-event side effects (e.g. validation) and
                // preserves which button was pressed; it falls back to submit() on
                // older engines.
                if (typeof form.requestSubmit === 'function') {
                    form.requestSubmit(submitter && submitter.form === form ? submitter : undefined);
                } else {
                    form.submit();
                }
            });
        }, true);

        document.addEventListener('click', (event) => {
            const link = event.target instanceof Element
                ? event.target.closest('a[data-omp-confirm]')
                : null;
            if (!link) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            ompConfirm(link.getAttribute('data-omp-confirm'), labelsFrom(link)).then((ok) => {
                if (ok) {
                    window.location.href = link.href;
                }
            });
        }, true);
    }

    window.ompConfirm = ompConfirm;

    initConfirmWiring();
})();
