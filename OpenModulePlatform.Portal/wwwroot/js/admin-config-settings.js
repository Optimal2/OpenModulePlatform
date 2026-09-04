// File: OpenModulePlatform.Portal/wwwroot/js/admin-config-settings.js
(() => {
    'use strict';

    const form = document.querySelector('[data-config-settings-form]');
    if (!form) {
        return;
    }

    const scopeSelect = form.querySelector('[data-scope-select]');
    const fields = Array.from(form.querySelectorAll('[data-scope-field]'));
    const settingValue = form.querySelector('[data-config-setting-selected-value]');
    const settingOptions = Array.from(form.querySelectorAll('[data-config-setting-option]'));
    const detailPanel = form.querySelector('[data-config-setting-details]');
    const emptyPanel = form.querySelector('[data-config-setting-empty]');
    const detailTitle = form.querySelector('[data-config-setting-detail-title]');
    const detailDescription = form.querySelector('[data-config-setting-detail-description]');
    const detailExamples = form.querySelector('[data-config-setting-detail-examples]');
    const detailExamplesLabel = form.querySelector('[data-config-setting-detail-examples-label]');
    const detailExampleChips = form.querySelector('[data-config-setting-detail-example-chips]');
    const configValueInput = form.querySelector('[data-config-setting-value-input]');
    const saveButton = form.querySelector('[data-config-settings-save]');
    const noDescriptionLabel = form.dataset.noDescriptionLabel || '';
    const exampleValuesLabel = form.dataset.exampleValuesLabel || '';
    const noExamplesLabel = form.dataset.noExamplesLabel || '';
    const useExampleLabel = form.dataset.useExampleLabel || '';
    const multiselects = Array.from(form.querySelectorAll('[data-config-multiselect]'));

    const parseExamples = (value) => {
        const seen = new Set();
        return (value || '')
            .split(/[;\r\n]+/)
            .map((item) => item.trim())
            .filter((item) => {
                if (item.length === 0 || seen.has(item)) {
                    return false;
                }

                seen.add(item);
                return true;
            });
    };

    const renderExampleChips = (examples) => {
        if (!detailExampleChips) {
            return;
        }

        detailExampleChips.replaceChildren();
        examples.forEach((example) => {
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'config-settings-picker__example-chip';
            chip.textContent = example;
            chip.title = useExampleLabel ? `${useExampleLabel}: ${example}` : example;
            chip.addEventListener('click', () => {
                if (!configValueInput) {
                    return;
                }

                configValueInput.value = example;
                configValueInput.focus();
                configValueInput.dispatchEvent(new Event('input', { bubbles: true }));
                configValueInput.dispatchEvent(new Event('change', { bubbles: true }));
            });

            detailExampleChips.appendChild(chip);
        });
    };

    const syncMultiselectSummary = (multiselect) => {
        const summary = multiselect.querySelector('[data-config-multiselect-summary]');
        if (!summary) {
            return;
        }

        const checked = Array.from(multiselect.querySelectorAll('[data-config-multiselect-checkbox]:checked'));
        const labels = checked.map((input) => input.dataset.configMultiselectLabel).filter(Boolean);
        summary.textContent = labels.length === 0
            ? summary.dataset.placeholder || ''
            : labels.join(', ');
    };

    multiselects.forEach((multiselect) => {
        multiselect.querySelectorAll('[data-config-multiselect-checkbox]').forEach((checkbox) => {
            checkbox.addEventListener('change', () => syncMultiselectSummary(multiselect));
        });

        multiselect.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
                multiselect.open = false;
            }
        });

        syncMultiselectSummary(multiselect);
    });

    document.addEventListener('click', (event) => {
        multiselects.forEach((multiselect) => {
            if (!multiselect.contains(event.target)) {
                multiselect.open = false;
            }
        });
    });

    const syncScopeFields = () => {
        if (!scopeSelect) {
            return;
        }

        const active = scopeSelect.value;
        fields.forEach((field) => {
            const visible = field.dataset.scopeField === active;
            field.hidden = !visible;
            field.querySelectorAll('select, input, textarea').forEach((input) => {
                input.disabled = !visible;
            });
        });
    };

    scopeSelect?.addEventListener('change', syncScopeFields);
    syncScopeFields();

    const syncSelectedSetting = () => {
        const selectedValue = settingValue?.value || '';
        const selected = settingOptions.find((option) => option.dataset.settingValue === selectedValue);
        settingOptions.forEach((option) => {
            const isSelected = option === selected;
            option.classList.toggle('is-selected', isSelected);
            option.setAttribute('aria-checked', isSelected ? 'true' : 'false');
        });

        const hasSelection = Boolean(selected);
        if (detailPanel) {
            detailPanel.hidden = !hasSelection;
        }

        if (emptyPanel) {
            emptyPanel.hidden = hasSelection;
        }

        if (saveButton) {
            saveButton.disabled = !hasSelection;
        }

        if (!selected) {
            return;
        }

        if (detailTitle) {
            detailTitle.textContent = selected.dataset.settingTitle || '';
        }

        if (detailDescription) {
            detailDescription.textContent = selected.dataset.settingDescription || noDescriptionLabel;
        }

        const examples = parseExamples(selected.dataset.settingExamples || '');
        const hasExamples = examples.length > 0;
        if (detailExamples) {
            detailExamples.hidden = false;
        }

        if (detailExamplesLabel) {
            detailExamplesLabel.textContent = hasExamples ? exampleValuesLabel : noExamplesLabel;
        }

        if (detailExampleChips) {
            detailExampleChips.hidden = !hasExamples;
            renderExampleChips(examples);
        }

        syncScopeFields();
    };

    settingOptions.forEach((option) => {
        option.addEventListener('click', () => {
            if (settingValue) {
                settingValue.value = option.dataset.settingValue || '';
            }

            syncSelectedSetting();
        });
    });
    syncSelectedSetting();
})();
