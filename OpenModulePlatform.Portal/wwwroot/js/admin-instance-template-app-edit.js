// File: OpenModulePlatform.Portal/wwwroot/js/admin-instance-template-app-edit.js
(() => {
    'use strict';

    const appSelect = document.getElementById('Input_AppId');
    const artifactSelect = document.getElementById('Input_DesiredArtifactId');
    if (!appSelect || !artifactSelect) {
        return;
    }

    const applyArtifactFilter = () => {
        const appId = appSelect.value || '';
        let selectedStillVisible = false;

        for (const option of artifactSelect.options) {
            if (!option.value) {
                option.hidden = false;
                option.disabled = false;
                continue;
            }

            const visible = appId && option.dataset.appId === appId && option.dataset.compatible === 'true';
            option.hidden = !visible;
            option.disabled = !visible;

            if (visible && option.selected) {
                selectedStillVisible = true;
            }
        }

        if (!selectedStillVisible) {
            artifactSelect.value = '';
        }
    };

    appSelect.addEventListener('change', applyArtifactFilter);
    applyArtifactFilter();
})();
