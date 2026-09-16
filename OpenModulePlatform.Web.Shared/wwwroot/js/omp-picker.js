// File: OpenModulePlatform.Web.Shared/wwwroot/js/omp-picker.js
// The behaviour behind omp-picker.css: a <details data-omp-picker> whose
// panel holds radio or checkbox rows. Radios are a single choice: the chosen
// row's text becomes the field label and the panel closes. Checkboxes are a
// multiple choice: the label stays and the count badge says how many are on.
// Either way the row that is checked is highlighted, the field is marked
// active while a non-empty value is chosen, and a click outside or Escape
// closes the panel. The inputs are ordinary form fields, so the page's own
// change handling (a submit, a fetch) works untouched.
(() => {
    'use strict';

    const enhance = (picker) => {
        if (picker._ompPicker) {
            return;
        }
        picker._ompPicker = true;

        const label = picker.querySelector('[data-omp-picker-label]');
        const count = picker.querySelector('[data-omp-picker-count]');
        const inputs = () => Array.from(picker.querySelectorAll('.omp-picker__option input'));

        const sync = () => {
            const all = inputs();
            const checked = all.filter((input) => input.checked);
            all.forEach((input) => {
                input.closest('.omp-picker__option')?.classList.toggle('omp-picker__option--active', input.checked);
            });
            // A picker that always holds a value (a page size) is never "active";
            // it says so with data-omp-picker-plain.
            picker.classList.toggle('omp-picker--active', !picker.hasAttribute('data-omp-picker-plain') && checked.some((input) => input.value !== ''));

            const single = all.some((input) => input.type === 'radio');
            if (single && label && checked[0]) {
                label.textContent = (checked[0].closest('.omp-picker__option')?.textContent || '').trim();
            }
            if (count) {
                const chosen = checked.filter((input) => input.value !== '').length;
                count.textContent = String(chosen);
                count.hidden = chosen === 0;
            }
        };

        picker.addEventListener('change', (event) => {
            sync();
            if (event.target instanceof HTMLInputElement && event.target.type === 'radio') {
                picker.open = false;
            }
        });
        document.addEventListener('click', (event) => {
            if (picker.open && !picker.contains(event.target)) {
                picker.open = false;
            }
        });
        document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape' && picker.open) {
                picker.open = false;
            }
        });
        sync();
    };

    const init = (root) => {
        (root || document).querySelectorAll('details[data-omp-picker]').forEach(enhance);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => init(document));
    } else {
        init(document);
    }

    window.ompPicker = { init };
})();
