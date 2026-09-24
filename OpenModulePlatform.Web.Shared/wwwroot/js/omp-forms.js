// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-forms.js
// Shared OMP form behaviours.
//
// window.ompConfirm(message, options) -> Promise<boolean | "extra">
//   Shows an OMP-styled modal <dialog> instead of the native window.confirm.
//   options: { okLabel, cancelLabel, extraLabel, title, content, focus }
//     okLabel, cancelLabel: plain strings, already localized by the page.
//     extraLabel: a third choice between Cancel and OK (say "Discard
//       changes" beside "Save changes", with Cancel meaning keep editing);
//       Cancel then sits alone at the left. The promise resolves with the
//       string "extra" when it is chosen.
//     focus: "ok" starts with the OK button focused (so Enter is OK and
//       Escape is Cancel); the default starts on Cancel.
//     title: a heading above the message; none when empty.
//     content: an element shown between the message and the buttons, holding
//       the page's own fields (a note, an optional text field, a checkbox). It
//       is moved into the dialog while the dialog is open and put back where
//       it was when the dialog closes, so the page reads the values out of it
//       afterwards. A checkbox in it marked data-omp-confirm-requires keeps
//       the OK button disabled until it is checked; an element marked
//       autofocus takes the focus when the dialog opens; Enter in a text
//       field there is OK. The building blocks omp-forms.css dresses:
//       omp-confirm-dialog__facts (a dl), __field (a label around an input),
//       __warning (a p) and __check (a label around a checkbox).
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

        const title = document.createElement('h2');
        title.className = 'omp-confirm-dialog__title';
        title.hidden = true;

        const message = document.createElement('p');
        message.className = 'omp-confirm-dialog__message';

        const content = document.createElement('div');
        content.className = 'omp-confirm-dialog__content';
        content.hidden = true;

        const actions = document.createElement('div');
        actions.className = 'omp-confirm-dialog__actions';

        const cancelButton = document.createElement('button');
        cancelButton.type = 'button';
        cancelButton.className = 'omp-confirm-dialog__cancel';

        const extraButton = document.createElement('button');
        extraButton.type = 'button';
        extraButton.className = 'omp-confirm-dialog__extra';
        extraButton.hidden = true;
        const okButton = document.createElement('button');
        okButton.type = 'button';
        okButton.className = 'omp-confirm-dialog__ok';

        actions.append(cancelButton, extraButton, okButton);
        body.append(title, message, content, actions);
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
        const title = dialog.querySelector('.omp-confirm-dialog__title');
        title.textContent = settings.title || '';
        title.hidden = !settings.title;
        dialog.querySelector('.omp-confirm-dialog__message').textContent = message || '';
        const okButton = dialog.querySelector('.omp-confirm-dialog__ok');
        const cancelButton = dialog.querySelector('.omp-confirm-dialog__cancel');
        const extraButton = dialog.querySelector('.omp-confirm-dialog__extra');
        okButton.textContent = settings.okLabel || 'OK';
        cancelButton.textContent = settings.cancelLabel || 'Cancel';
        extraButton.textContent = settings.extraLabel || '';
        extraButton.hidden = !settings.extraLabel;
        dialog.classList.toggle('omp-confirm-dialog--spread', !!settings.extraLabel);

        // The page's content is borrowed, not copied: a placeholder keeps its
        // seat so it goes back exactly where it came from, values and all.
        const slot = dialog.querySelector('.omp-confirm-dialog__content');
        const content = settings.content instanceof Element ? settings.content : null;
        const placeholder = content && content.parentNode ? document.createComment('omp-confirm-content') : null;
        if (placeholder) {
            content.parentNode.insertBefore(placeholder, content);
        }
        slot.replaceChildren(...(content ? [content] : []));
        slot.hidden = !content;
        dialog.classList.toggle('omp-confirm-dialog--content', !!content);
        const body = dialog.querySelector('.omp-confirm-dialog__body');
        const syncOk = () => {
            okButton.disabled = Array.from(slot.querySelectorAll('input[data-omp-confirm-requires]'))
                .some((input) => !input.checked);
        };
        syncOk();

        return new Promise((resolve) => {
            let settled = false;
            const finish = (result) => {
                if (settled) {
                    return;
                }
                settled = true;
                okButton.removeEventListener('click', onOk);
                cancelButton.removeEventListener('click', onCancel);
                extraButton.removeEventListener('click', onExtra);
                dialog.removeEventListener('cancel', onDialogCancel);
                dialog.removeEventListener('close', onClose);
                body.removeEventListener('submit', onSubmit);
                slot.removeEventListener('change', syncOk);
                if (dialog.open) {
                    dialog.close();
                }
                if (content) {
                    if (placeholder && placeholder.parentNode) {
                        placeholder.replaceWith(content);
                    } else {
                        content.remove();
                    }
                }
                okButton.disabled = false;
                resolve(result);
            };
            const onOk = () => finish(true);
            const onCancel = () => finish(false);
            const onExtra = () => finish('extra');
            const onDialogCancel = (event) => {
                event.preventDefault();
                finish(false);
            };
            // Closed by other means (a script, the browser): the answer is no.
            // The close event is queued, not fired on the spot, so the one
            // from a dialog that has just finished may reach the next question
            // on the same element; a dialog that is open again ignores it.
            const onClose = () => {
                if (!dialog.open) {
                    finish(false);
                }
            };
            // Enter in a text field in the content submits the dialog's own
            // form; that is OK, unless a required checkbox still holds it back.
            const onSubmit = (event) => {
                event.preventDefault();
                if (!okButton.disabled) {
                    finish(true);
                }
            };

            okButton.addEventListener('click', onOk);
            cancelButton.addEventListener('click', onCancel);
            extraButton.addEventListener('click', onExtra);
            dialog.addEventListener('cancel', onDialogCancel);
            dialog.addEventListener('close', onClose);
            body.addEventListener('submit', onSubmit);
            slot.addEventListener('change', syncOk);
            dialog.showModal();
            const focusTarget = content ? content.querySelector('[autofocus]') : null;
            (focusTarget || (settings.focus === 'ok' ? okButton : cancelButton)).focus();
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

        // R8-P5-16: submit buttons, not only whole forms. A form-level attribute cannot
        // express "confirm Delete but not Save" when both buttons live in the same form,
        // which is why four Portal pages had each written their own click handler around
        // window.confirm -- and each of those lost the localized button labels, because
        // the native dialog renders OK/Cancel in the browser's language regardless of
        // what the page says.
        document.addEventListener('click', (event) => {
            const button = event.target instanceof Element
                ? event.target.closest('button[data-omp-confirm]')
                : null;
            if (!button || button.disabled) {
                return;
            }

            const form = button.form;
            if (form && confirmedForms.has(form)) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            ompConfirm(button.getAttribute('data-omp-confirm'), labelsFrom(button)).then((ok) => {
                if (!ok) {
                    return;
                }

                if (!form) {
                    button.click();
                    return;
                }

                // The form's own submit handler must not ask a second time for the same
                // click; the button already carried the question.
                confirmedForms.add(form);
                if (typeof form.requestSubmit === 'function') {
                    form.requestSubmit(button);
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
