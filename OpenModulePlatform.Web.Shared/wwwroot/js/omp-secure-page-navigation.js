(function () {
    function isSecurePageProtected() {
        var marker = document.querySelector('meta[name="omp-prevent-back-cache"]');
        if (!marker) {
            return false;
        }

        var value = (marker.getAttribute('content') || '').toLowerCase();
        return value === 'true' || value === '1' || value === 'yes';
    }

    function shouldReloadForHistoryNavigation(event) {
        if (!isSecurePageProtected()) {
            return false;
        }

        if (event && event.persisted) {
            return true;
        }

        if (typeof window.performance === 'undefined' || typeof window.performance.getEntriesByType !== 'function') {
            return false;
        }

        var entries = window.performance.getEntriesByType('navigation');
        if (!entries || entries.length === 0) {
            return false;
        }

        return entries[0].type === 'back_forward';
    }

    function clearSensitivePageData() {
        var sensitiveFields = document.querySelectorAll(
            'input[type="password"], input[autocomplete="new-password"], ' +
            'input[autocomplete="current-password"], input[autocomplete="one-time-code"], ' +
            'input[autocomplete="off"], textarea[autocomplete="off"]'
        );

        for (var i = 0; i < sensitiveFields.length; i++) {
            sensitiveFields[i].value = '';
        }
    }

    function handlePageShow(event) {
        if (!shouldReloadForHistoryNavigation(event)) {
            return;
        }

        clearSensitivePageData();
        window.location.reload();
    }

    window.addEventListener('pageshow', handlePageShow);
})();
