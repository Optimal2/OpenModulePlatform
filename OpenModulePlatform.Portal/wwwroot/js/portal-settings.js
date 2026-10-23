// File: OpenModulePlatform.Portal/wwwroot/js/portal-settings.js
// The settings page: every section with settings is one form that saves on
// request. The script watches each form, and as soon as a field differs
// from what the page was rendered with it shows the form's "Unsaved
// changes" popover (non-modal: the page stays usable) with Discard and
// Save; leaving for another section, or the page, with changes pending
// asks first. The theme drop-down is applied through the theme script on
// Save (it lives in a browser cookie, not on the server); the language and
// the switches post with the form. The user section also previews a
// chosen image in the profile card and offers a reset arrow for the stored
// one. "Put the menu back" clears the dashboard menu's dragged position.
// Without this script the forms post like any other and the popovers stay
// hidden.
(function () {
    'use strict';

    var panel = document.querySelector('[data-settings-panel]');
    if (!panel) {
        return;
    }

    // ---- the shell's height -----------------------------------------------
    // The menu and the panel share one height: the room from the shell's top
    // to a little below where the popover's bottom edge lies (24px up from
    // the screen's bottom), so the page never scrolls because of them and
    // each box scrolls inside itself instead.

    var layout = panel.closest('.settings-layout');
    if (layout) {
        var sizeLayout = function () {
            var top = layout.getBoundingClientRect().top + window.scrollY;
            var height = window.innerHeight - top - 8;
            if (height <= 240) {
                layout.style.removeProperty('--settings-layout-height');
                return;
            }
            layout.style.setProperty('--settings-layout-height', height + 'px');
            // Whatever the page keeps under the shell (its own bottom padding)
            // must not make the page scroll: take it off the height.
            var overflow = document.documentElement.scrollHeight - window.innerHeight;
            if (overflow > 0 && height - overflow > 240) {
                layout.style.setProperty('--settings-layout-height', (height - overflow) + 'px');
            }
        };
        sizeLayout();
        window.addEventListener('resize', sizeLayout);
        window.addEventListener('load', sizeLayout);
        // Whatever stands above the shell (the navbar is drawn by script
        // after this runs) moves it; the body's size says when.
        if (typeof ResizeObserver === 'function') {
            new ResizeObserver(sizeLayout).observe(document.body);
        }
    }

    var forms = Array.prototype.slice.call(panel.querySelectorAll('[data-settings-form]'));
    var submitting = false;

    // ---- one settings form ---------------------------------------------------

    function serialize(form) {
        // The stored state is the rendered state: every field's value as the
        // page came, hidden bookkeeping fields and the token left out. A file
        // input counts as changed when a file is chosen.
        var parts = [];
        Array.prototype.forEach.call(form.elements, function (element) {
            if (!element.name || element.disabled) {
                return;
            }
            if (element.type === 'hidden' || element.type === 'submit' || element.type === 'button') {
                return;
            }
            if (element.type === 'file') {
                parts.push(element.name + '=' + (element.files && element.files.length ? 'file' : ''));
                return;
            }
            if (element.type === 'checkbox' || element.type === 'radio') {
                parts.push(element.name + '=' + (element.checked ? '1' : '0'));
                return;
            }
            parts.push(element.name + '=' + element.value);
        });
        return parts.join('&');
    }

    function initForm(form) {
        var unsaved = form.querySelector('[data-settings-unsaved]');
        var discard = form.querySelector('[data-settings-discard]');
        var returnTab = form.querySelector('[data-settings-return-tab]');
        var theme = form.querySelector('[data-settings-theme]');
        var api = window.ompTheme;

        // The theme drop-down starts at the browser's current mode; marking
        // the option as the default lets a reset return to it.
        if (theme && api && typeof api.getMode === 'function') {
            var mode = api.getMode();
            Array.prototype.forEach.call(theme.options, function (option) {
                option.defaultSelected = option.value === mode;
            });
            theme.value = mode;
        }

        var user = form.hasAttribute('data-settings-user') ? initUserSection(form) : null;
        var stored = serialize(form);

        var isDirty = function () {
            return serialize(form) !== stored || (user ? user.isDirty() : false);
        };

        var setUnsavedShown = function (shown) {
            if (!unsaved) {
                return;
            }
            if (typeof unsaved.showPopover === 'function') {
                var open = unsaved.matches(':popover-open');
                if (shown && !open) {
                    unsaved.showPopover();
                } else if (!shown && open) {
                    unsaved.hidePopover();
                }
                return;
            }
            unsaved.hidden = !shown;
        };

        var sync = function () {
            var dirty = isDirty();
            setUnsavedShown(dirty);
            // Room along the bottom of the page for the popover, so nothing
            // ends up underneath it.
            form.classList.toggle('is-dirty', dirty);
            if (user) {
                user.sync();
            }
        };

        form.addEventListener('input', sync);
        form.addEventListener('change', sync);

        if (discard) {
            discard.addEventListener('click', function () {
                form.reset();
                if (user) {
                    user.discard();
                }
                sync();
            });
        }

        form.addEventListener('submit', function (event) {
            // The validation script runs first and cancels an invalid post;
            // then nothing is leaving and the guards stay on.
            if (event.defaultPrevented) {
                if (returnTab) {
                    returnTab.value = '';
                }
                return;
            }
            submitting = true;
            if (theme && api && typeof api.setMode === 'function') {
                api.setMode(theme.value);
            }
        });

        var saveAndGo = function (tab) {
            if (returnTab) {
                returnTab.value = tab || '';
            }
            if (typeof form.requestSubmit === 'function') {
                form.requestSubmit();
            } else {
                form.submit();
            }
        };

        var entry = { form: form, isDirty: isDirty, saveAndGo: saveAndGo };
        sync();
        return entry;
    }

    // ---- the user section --------------------------------------------------
    // The chosen image previews in the profile card, the reset arrow drops
    // the stored image (a hidden flag the handler reads), and the card's name
    // follows the field.

    function initUserSection(form) {
        var nameInput = form.querySelector('[data-settings-name]');
        var fileInput = form.querySelector('[data-settings-file]');
        var removeFlag = form.querySelector('[data-settings-remove-image]');
        var resetButton = form.querySelector('[data-settings-reset-image]');
        var avatarImage = form.querySelector('[data-settings-avatar-image]');
        var avatarInitials = form.querySelector('[data-settings-avatar-initials]');
        var cardName = form.querySelector('[data-settings-card-name]');
        var fileName = form.querySelector('[data-settings-file-name]');
        var original = {
            name: form.getAttribute('data-original-name') || '',
            hasImage: form.getAttribute('data-has-image') === 'true',
            imageUrl: avatarImage ? avatarImage.getAttribute('src') || '' : '',
            fileName: fileName ? fileName.textContent : ''
        };
        var previewUrl = null;

        var hasChosenFile = function () {
            return !!(fileInput && fileInput.files && fileInput.files.length > 0);
        };

        var removing = function () {
            return !!(removeFlag && removeFlag.value === 'true');
        };

        var showAvatar = function (url) {
            if (!avatarImage || !avatarInitials) {
                return;
            }
            if (url) {
                avatarImage.src = url;
                avatarImage.hidden = false;
                avatarInitials.hidden = true;
            } else {
                avatarImage.removeAttribute('src');
                avatarImage.hidden = true;
                avatarInitials.hidden = false;
            }
        };

        var releasePreview = function () {
            if (previewUrl) {
                try {
                    URL.revokeObjectURL(previewUrl);
                } catch (error) {
                    // Nothing to release.
                }
                previewUrl = null;
            }
        };

        var showFileName = function (text) {
            if (fileName) {
                fileName.textContent = text;
                fileName.hidden = !text;
            }
        };

        if (fileInput) {
            fileInput.addEventListener('change', function () {
                releasePreview();
                if (hasChosenFile()) {
                    var file = fileInput.files[0];
                    if (removeFlag) {
                        removeFlag.value = 'false';
                    }
                    try {
                        previewUrl = URL.createObjectURL(file);
                        showAvatar(previewUrl);
                    } catch (error) {
                        // No preview: the stored avatar stays until Save.
                    }
                    showFileName(file.name);
                } else {
                    showAvatar(original.hasImage && !removing() ? original.imageUrl : null);
                    showFileName(original.fileName);
                }
            });
        }

        if (resetButton) {
            resetButton.addEventListener('click', function () {
                releasePreview();
                if (fileInput) {
                    fileInput.value = '';
                }
                if (removeFlag) {
                    removeFlag.value = 'true';
                }
                showAvatar(null);
                showFileName('');
                form.dispatchEvent(new Event('change', { bubbles: true }));
            });
        }

        return {
            isDirty: function () {
                return removing() || hasChosenFile() || (nameInput ? nameInput.value.trim() !== original.name : false);
            },
            sync: function () {
                if (cardName && nameInput) {
                    cardName.textContent = nameInput.value.trim() || original.name;
                }
                if (resetButton) {
                    // The arrow shows while there is an image to drop: the
                    // stored one (not yet reset) or a newly chosen file.
                    resetButton.hidden = !(hasChosenFile() || (original.hasImage && !removing()));
                }
            },
            discard: function () {
                releasePreview();
                if (removeFlag) {
                    removeFlag.value = 'false';
                }
                if (nameInput) {
                    nameInput.value = original.name;
                }
                showAvatar(original.hasImage ? original.imageUrl : null);
                showFileName(original.fileName);
            }
        };
    }

    var entries = forms.map(initForm);

    // ---- leaving with changes pending ------------------------------------------
    // Another section: Save (Enter) and open it afterwards, or stay. The
    // page itself: the browser's own prompt.

    var dirtyEntry = function () {
        for (var i = 0; i < entries.length; i += 1) {
            if (entries[i].isDirty()) {
                return entries[i];
            }
        }
        return null;
    };

    document.querySelectorAll('.settings-nav__link').forEach(function (link) {
        link.addEventListener('click', function (event) {
            var entry = submitting ? null : dirtyEntry();
            if (!entry) {
                return;
            }
            event.preventDefault();
            var form = entry.form;
            var message = form.getAttribute('data-unsaved-message') || 'You have unsaved changes.';
            var tab = '';
            try {
                tab = new URL(link.href, window.location.href).searchParams.get('tab') || '';
            } catch (error) {
                tab = '';
            }
            var proceed = function (save) {
                if (save) {
                    entry.saveAndGo(tab);
                }
            };
            if (typeof window.ompConfirm === 'function') {
                window.ompConfirm(message, {
                    okLabel: form.getAttribute('data-unsaved-save') || undefined,
                    cancelLabel: form.getAttribute('data-unsaved-keep') || undefined,
                    enter: 'ok'
                }).then(proceed);
            } else {
                proceed(window.confirm(message));
            }
        });
    });

    window.addEventListener('beforeunload', function (event) {
        if (!submitting && dirtyEntry()) {
            event.preventDefault();
            event.returnValue = '';
        }
    });

    // ---- the dashboard menu's position -------------------------------------
    // The dashboard's edit menu keeps the place it was dragged to in the
    // browser, per user, under the key portal-dashboard.js uses
    // (bindDashboardMenuDrag); clearing it stands the menu in its corner
    // the next time the dashboard is shown. The button is red, so it asks
    // first, through the shared confirm when it is there; the texts come in
    // the button's own attributes, so the shared forms script (which asks
    // for data-omp-confirm) does not ask a second time.

    var menuPanel = panel.querySelector('[data-dashboard-menu-settings]');
    var menuButton = menuPanel ? menuPanel.querySelector('[data-dashboard-menu-reset-position-button]') : null;
    if (menuPanel && menuButton) {
        var storageKey = 'omp.dashboard.editMenuPosition:' + (menuPanel.getAttribute('data-dashboard-draft-key') || 'anonymous');
        var resetMenuPosition = function () {
            try {
                window.localStorage.removeItem(storageKey);
            } catch (error) {
                // Storage blocked: nothing was stored to clear.
            }
            var status = menuPanel.querySelector('[data-dashboard-menu-reset-status]');
            if (status) {
                status.hidden = false;
            }
        };
        menuButton.addEventListener('click', function () {
            var message = menuButton.getAttribute('data-settings-confirm');
            if (message && typeof window.ompConfirm === 'function') {
                window.ompConfirm(message, {
                    okLabel: menuButton.getAttribute('data-settings-confirm-ok') || undefined,
                    cancelLabel: menuButton.getAttribute('data-settings-confirm-cancel') || undefined
                }).then(function (confirmed) {
                    if (confirmed) {
                        resetMenuPosition();
                    }
                });
                return;
            }
            resetMenuPosition();
        });
    }
})();
