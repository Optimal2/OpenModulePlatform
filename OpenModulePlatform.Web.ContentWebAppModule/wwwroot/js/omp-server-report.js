// File: OpenModulePlatform.Web.ContentWebAppModule/wwwroot/js/omp-server-report.js
(() => {
    'use strict';

    // DB_JSON_SCRIPT no longer emits an executable inline script (CSP migration): the
    // renderer writes a <script type="application/json"> data block and includes this
    // reader immediately after it, so a following script in the trusted content still
    // sees the documented window.<name> / window.<name>Report globals in source order.
    document.querySelectorAll('script[data-omp-server-report-json]').forEach((block) => {
        const variableName = block.getAttribute('data-variable-name') || '';
        if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(variableName)) {
            return;
        }

        let payload;
        try {
            payload = JSON.parse(block.textContent || '{}');
        } catch (error) {
            if (window.console && typeof window.console.warn === 'function') {
                window.console.warn('OMP server report JSON block could not be parsed.', error);
            }
            return;
        }

        window[variableName] = Array.isArray(payload.rows) ? payload.rows : [];
        window[variableName + 'Report'] = payload.report || null;
    });
})();
