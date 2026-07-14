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

    function initConfirmWiring() {
        for (const form of document.querySelectorAll('form[data-omp-confirm]')) {
            let confirmed = false;
            form.addEventListener('submit', (event) => {
                if (confirmed) {
                    confirmed = false;
                    return;
                }

                event.preventDefault();
                ompConfirm(form.getAttribute('data-omp-confirm'), labelsFrom(form)).then((ok) => {
                    if (ok) {
                        confirmed = true;
                        // requestSubmit keeps submit-event side effects (e.g. validation)
                        // and falls back to submit() on older engines.
                        if (typeof form.requestSubmit === 'function') {
                            form.requestSubmit();
                        } else {
                            form.submit();
                        }
                    }
                });
            });
        }

        for (const link of document.querySelectorAll('a[data-omp-confirm]')) {
            link.addEventListener('click', (event) => {
                event.preventDefault();
                ompConfirm(link.getAttribute('data-omp-confirm'), labelsFrom(link)).then((ok) => {
                    if (ok) {
                        window.location.href = link.href;
                    }
                });
            });
        }
    }

    window.ompConfirm = ompConfirm;

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initConfirmWiring);
    } else {
        initConfirmWiring();
    }
})();
