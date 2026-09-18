// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-picker.js
// The behaviour behind omp-picker.css: a <details data-omp-picker> whose
// panel holds radio or checkbox rows. Radios are a single choice: the chosen
// row's text becomes the field label, and a click on a row closes the panel
// (arrow keys move the choice without closing, so the keyboard can browse).
// Checkboxes are a multiple choice: the label shows the first chosen row's
// text and keeps it while that row stays on, so a later choice does not push
// it out (or the picker's data-omp-picker-empty text when nothing is on);
// with data-omp-picker-more wording ("{0} and {1} others", and
// data-omp-picker-more-one for exactly one more) the label says how many
// more are on, otherwise the badge says "+N"; and the field's title lists
// them all when the summary carries data-omp-picker-title (what the field is
// for, shown when nothing is on). A picker with data-omp-picker-static keeps
// the label the page rendered (what the field is for) and lets the badge
// say how many are on, "0" included. A button marked data-omp-picker-clear inside the
// panel turns every row off (and is disabled while none is on); it fires a
// change on a row so the page's own change handling runs. Either way the
// row that is checked is highlighted, the
// field is marked active while a non-empty value is chosen, and a click
// outside or Escape closes the panel; a panel that closes with the focus
// inside hands the focus back to its field, so the keyboard never rests in
// hidden content. The inputs are ordinary form fields, so the page's own
// change handling (a submit, a fetch) works untouched. One pair of document
// listeners serves every picker on the page.
//
// The same script gives a segmented choice (.omp-segmented, see
// omp-picker.css) its sliding fill: a thumb behind the options that glides
// from the option that was on to the one that is, and follows a wrapped
// row or a resized window. Without the script the chosen option carries
// its own fill, so the choice reads the same either way.
(() => {
    'use strict';

    const pickers = new Set();

    const close = (picker) => {
        if (!picker.open) {
            return;
        }
        const summary = picker.querySelector('summary');
        const focusInside = picker.contains(document.activeElement) && document.activeElement !== summary;
        picker.open = false;
        if (focusInside && summary) {
            summary.focus();
        }
    };

    const sync = (picker) => {
        const label = picker.querySelector('[data-omp-picker-label]');
        const count = picker.querySelector('[data-omp-picker-count]');
        const all = Array.from(picker.querySelectorAll('.omp-picker__option input'));
        const checked = all.filter((input) => input.checked);
        all.forEach((input) => {
            input.closest('.omp-picker__option')?.classList.toggle('omp-picker__option--active', input.checked);
        });
        // A picker that always holds a value (a page size) is never "active";
        // it says so with data-omp-picker-plain.
        picker.classList.toggle('omp-picker--active', !picker.hasAttribute('data-omp-picker-plain') && checked.some((input) => input.value !== ''));
        const clearButton = picker.querySelector('[data-omp-picker-clear]');
        if (clearButton) {
            clearButton.disabled = !checked.some((input) => input.value !== '');
        }

        const text = (input) => (input.closest('.omp-picker__option')?.textContent || '').trim();
        const single = all.some((input) => input.type === 'radio');
        if (single) {
            if (label && checked[0]) {
                label.textContent = text(checked[0]);
            }
            if (count) {
                const chosen = checked.filter((input) => input.value !== '').length;
                count.textContent = String(chosen);
                count.hidden = chosen === 0;
            }
            return;
        }

        picker.classList.add('omp-picker--multi');
        const names = checked.map(text);
        // The row the field names stays the one it named as long as it is on
        // (the first the user chose, typically); only when that row goes off
        // does the first row on, in list order, take its place.
        const shown = checked.find((input) => input.value === picker._ompPickerShown) || checked[0];
        picker._ompPickerShown = shown?.value;
        // A static label is the page's own words for the field ("User");
        // only the badge and the title tell what is on.
        const staticLabel = picker.hasAttribute('data-omp-picker-static');
        if (label && !staticLabel) {
            // Three states: nothing on says what "nothing" means (the text in
            // data-omp-picker-empty), one on says its name, several say the
            // named one and how many more, through the page's own wording in
            // data-omp-picker-more-one / data-omp-picker-more ({0} the name,
            // {1} the number of others). Without that wording the badge below
            // carries the number instead.
            const more = picker.getAttribute('data-omp-picker-more');
            const moreOne = picker.getAttribute('data-omp-picker-more-one') || more;
            const others = names.length - 1;
            if (!shown) {
                label.textContent = picker.getAttribute('data-omp-picker-empty') || '';
            } else {
                // The name sits in its own span so a long one is clipped on its
                // own while the wording around it ("and 2 others") stays whole.
                const wording = others > 0 && more ? (others === 1 ? moreOne : more).replace('{1}', String(others)) : '{0}';
                const [before, after] = wording.split('{0}');
                const name = document.createElement('span');
                name.className = 'omp-picker__name';
                name.textContent = text(shown);
                label.replaceChildren(document.createTextNode(before || ''), name, document.createTextNode(after || ''));
            }
        }
        if (count) {
            if (staticLabel) {
                // Always in view, "0" included: the count is the field's state.
                count.hidden = false;
                count.textContent = String(names.length);
            } else {
                count.hidden = names.length < 2 || picker.hasAttribute('data-omp-picker-more');
                count.textContent = count.hidden ? '' : `+${names.length - 1}`;
            }
        }
        // The field's title lists every chosen name; with nothing on it says
        // what the field is for, which the page hands over in
        // data-omp-picker-title (the title itself may already hold names).
        const field = picker.querySelector('summary');
        const purpose = field?.getAttribute('data-omp-picker-title');
        if (field && purpose !== null) {
            field.setAttribute('title', names.length > 0 ? names.join(', ') : purpose);
        }
    };

    const enhance = (picker) => {
        if (picker._ompPicker) {
            return;
        }
        picker._ompPicker = true;
        pickers.add(picker);

        picker.addEventListener('change', () => sync(picker));
        // A pointer click on a single-choice row is a decision: the panel
        // closes. The change event alone is not, since arrow keys fire it too,
        // and so does the click the browser synthesises when a key changes a
        // radio; that one carries detail 0, a pointer click never does.
        picker.addEventListener('click', (event) => {
            // Clear turns every row off and tells the page through one change
            // event on a row that was on, so a page listening to its inputs
            // (a fetch, a submit) hears it; the panel stays open.
            const clearButton = event.target.closest('[data-omp-picker-clear]');
            if (clearButton) {
                const on = Array.from(picker.querySelectorAll('.omp-picker__option input')).filter((input) => input.checked && input.value !== '');
                on.forEach((input) => {
                    input.checked = false;
                });
                if (on.length > 0) {
                    on[0].dispatchEvent(new Event('change', { bubbles: true }));
                } else {
                    sync(picker);
                }
                // The button has just disabled itself; a keyboard that was on
                // it would fall out of the panel, so the field takes the focus.
                if (clearButton.disabled && picker.contains(document.activeElement)) {
                    picker.querySelector('summary')?.focus();
                }
                return;
            }
            if (event.detail === 0) {
                return;
            }
            const option = event.target.closest('.omp-picker__option');
            const input = option?.querySelector('input');
            if (input && input.type === 'radio') {
                window.setTimeout(() => close(picker), 0);
            }
        });
        sync(picker);
    };

    document.addEventListener('click', (event) => {
        pickers.forEach((picker) => {
            if (picker.open && !picker.contains(event.target)) {
                close(picker);
            }
        });
    });
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            pickers.forEach(close);
        }
    });

    // The thumb is placed over the option that is on; it moves on change
    // and re-measures when the window or the fonts change the layout. The
    // first placement is still (no glide in from the corner on load).
    const enhanceSegmented = (track) => {
        if (track._ompSegmented) {
            return;
        }
        track._ompSegmented = true;
        const thumb = document.createElement('span');
        thumb.className = 'omp-segmented__thumb';
        thumb.setAttribute('aria-hidden', 'true');
        track.prepend(thumb);
        const place = (still) => {
            const on = track.querySelector('.omp-segmented__option input:checked')?.closest('.omp-segmented__option');
            track.classList.toggle('omp-segmented--thumb', !!on);
            if (!on) {
                return;
            }
            thumb.classList.toggle('omp-segmented__thumb--still', !!still);
            thumb.style.transform = `translate(${on.offsetLeft}px, ${on.offsetTop}px)`;
            thumb.style.width = `${on.offsetWidth}px`;
            thumb.style.height = `${on.offsetHeight}px`;
            if (still) {
                // Flush the still placement before the transition comes back.
                void thumb.offsetWidth;
                thumb.classList.remove('omp-segmented__thumb--still');
            }
        };
        track.addEventListener('change', () => place(false));
        window.addEventListener('resize', () => place(true));
        place(true);
        document.fonts?.ready?.then(() => place(true));
    };

    const init = (root) => {
        (root || document).querySelectorAll('details[data-omp-picker]').forEach(enhance);
        (root || document).querySelectorAll('.omp-segmented').forEach(enhanceSegmented);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => init(document));
    } else {
        init(document);
    }

    // Pages that render a picker later call init(root); a page that swaps
    // markup around an existing picker calls sync(picker) to redraw it.
    window.ompPicker = { init, sync };
})();
