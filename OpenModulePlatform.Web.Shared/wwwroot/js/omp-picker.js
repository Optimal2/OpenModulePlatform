// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-picker.js
// The behaviour behind omp-picker.css: a <details data-omp-picker> whose
// panel holds radio or checkbox rows. Radios are a single choice: the chosen
// row's text becomes the field label, and a click on a row closes the panel
// (arrow keys move the choice without closing, so the keyboard can browse).
// Checkboxes are a multiple choice: the label shows the first chosen row's
// text and keeps it while that row stays on, so a later choice does not push
// it out (or the picker's data-omp-picker-empty text when nothing is on), the
// badge says "+N" for the rows beyond the first, and the field's title lists
// them all when the summary carries data-omp-picker-title (what the field is
// for, shown when nothing is on). Either way the row that is checked is highlighted, the
// field is marked active while a non-empty value is chosen, and a click
// outside or Escape closes the panel; a panel that closes with the focus
// inside hands the focus back to its field, so the keyboard never rests in
// hidden content. The inputs are ordinary form fields, so the page's own
// change handling (a submit, a fetch) works untouched. One pair of document
// listeners serves every picker on the page.
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
        if (label) {
            // With nothing on, the field says what "nothing" means (the text in
            // data-omp-picker-empty), never a name that is no longer chosen.
            label.textContent = shown ? text(shown) : (picker.getAttribute('data-omp-picker-empty') || '');
        }
        if (count) {
            count.hidden = names.length < 2;
            count.textContent = count.hidden ? '' : `+${names.length - 1}`;
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

    const init = (root) => {
        (root || document).querySelectorAll('details[data-omp-picker]').forEach(enhance);
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
